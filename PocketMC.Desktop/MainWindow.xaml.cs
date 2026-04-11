using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PocketMC.Desktop.Core.Interfaces;
using PocketMC.Desktop.Services;
using PocketMC.Desktop.Views;
using PocketMC.Desktop.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace PocketMC.Desktop;

public partial class MainWindow : FluentWindow, IStartupShellHost, INavigationHost
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ApplicationState _applicationState;
    private readonly ResourceMonitorService _globalMonitor;
    private readonly ShellStartupCoordinator _startupCoordinator;
    private readonly ShellViewModel _viewModel;
    private readonly ILogger<MainWindow> _logger;
    private Type _lastShellPageType = typeof(DashboardPage);
    private ITitleBarContextSource? _titleBarContextSource;
    private bool _isNavigationLockedToRootSetup;
    private readonly Dictionary<Type, Page> _shellPageCache = new();

    public MainWindow(
        IServiceProvider serviceProvider,
        ApplicationState applicationState,
        ResourceMonitorService globalMonitor,
        ShellStartupCoordinator startupCoordinator,
        ShellViewModel viewModel,
        ILogger<MainWindow> logger)
    {
        _serviceProvider = serviceProvider;
        _applicationState = applicationState;
        _globalMonitor = globalMonitor;
        _startupCoordinator = startupCoordinator;
        _viewModel = viewModel;
        _logger = logger;

        DataContext = _viewModel;

        InitializeComponent();

        // Wire up WPF-UI theme engine
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this);

        // Set up the NavigationView to resolve pages via DI
        RootNavigation.SetServiceProvider(_serviceProvider);

        // Listen for navigation events to update breadcrumb
        RootNavigation.Navigating += OnNavigating;
        RootNavigation.Navigated += OnNavigated;
        Closing += MainWindow_Closing;
        _globalMonitor.OnGlobalMetricsUpdated += UpdateGlobalHealth;
        _startupCoordinator.AttachHost(this);

        // Win10 fallback: listen for wallpaper changes to refresh simulated Mica
        if (!WallpaperMicaService.IsWindows11OrLater)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    // ──────────────────────────────────────────────
    //  Navigation & Breadcrumb
    // ──────────────────────────────────────────────

    private void OnNavigated(NavigationView sender, NavigatedEventArgs args)
    {
        var pageType = args.Page?.GetType();
        if (IsShellPageType(pageType))
        {
            _lastShellPageType = pageType!;
            DetachTitleBarContextSource();
            SyncNavigationSelection(pageType);
        }
        UpdateBreadcrumb(pageType);
    }

    private void UpdateBreadcrumb(Type? pageType)
    {
        string? label = pageType?.Name switch
        {
            nameof(DashboardPage)      => "Dashboard",
            nameof(TunnelPage)         => "Tunnel",
            nameof(JavaSetupPage)       => "Java Setup",
            nameof(AboutPage)           => "About",
            nameof(AppSettingsPage)     => "Settings",
            nameof(NewInstancePage)     => "New Instance",
            nameof(ServerSettingsPage)  => "Server Settings",
            nameof(ServerConsolePage)   => "Console",
            _ => null
        };

        UpdateBreadcrumb(label);
    }

    public void UpdateBreadcrumb(string? label)
    {
        _viewModel.BreadcrumbCurrentText = label;
        _viewModel.IsBreadcrumbVisible = !string.IsNullOrEmpty(label);
    }

    private static bool IsShellPageType(Type? pageType) =>
        pageType == typeof(DashboardPage) ||
        pageType == typeof(TunnelPage) ||
        pageType == typeof(JavaSetupPage) ||
        pageType == typeof(AboutPage) ||
        pageType == typeof(AppSettingsPage);

    public void NavigateToPage(Type pageType, object? dataContext = null)
    {
        NavigateToShellPage(pageType, dataContext);
    }

    public bool NavigateToShellPage(Type pageType, object? parameter = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(() => NavigateToShellPage(pageType, parameter));
        }

        if (!CanNavigateToPage(pageType))
        {
            return false;
        }

        return ReplaceShellContent(pageType);
    }

    public bool NavigateToDashboard()
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(NavigateToDashboard);
        }

        if (!CanNavigateToPage(typeof(DashboardPage)))
        {
            return false;
        }

        return ReplaceShellContent(typeof(DashboardPage));
    }

    public bool NavigateToDetailPage(Page page, string breadcrumbLabel)
    {
        if (!CanNavigateToPage(page.GetType()))
        {
            return false;
        }

        bool replaced = RootNavigation.ReplaceContent(page, null);
        if (replaced)
        {
            AttachTitleBarContextSource(page as ITitleBarContextSource);
            UpdateBreadcrumb(breadcrumbLabel);
        }

        return replaced;
    }

    public bool NavigateBackFromDetail()
    {
        if (_isNavigationLockedToRootSetup)
        {
            return false;
        }

        return NavigateToShellPage(_lastShellPageType);
    }

    private bool ReplaceShellContent(Type pageType)
    {
        if (!CanNavigateToPage(pageType))
        {
            return false;
        }

        Page shellPage = GetOrCreateShellPage(pageType);
        bool replaced = RootNavigation.ReplaceContent(shellPage, null);
        if (replaced)
        {
            _lastShellPageType = pageType;
            DetachTitleBarContextSource();
            SyncNavigationSelection(pageType);
            UpdateBreadcrumb(pageType);
        }

        return replaced;
    }

    private void OnNavigating(NavigationView sender, NavigatingCancelEventArgs args)
    {
        Type? pageType = GetRequestedPageType(args.Page);

        if (_isNavigationLockedToRootSetup)
        {
            if (pageType == typeof(RootDirectorySetupPage))
            {
                return;
            }

            args.Cancel = true;
            _logger.LogDebug(
                "Blocked navigation to {PageType} until the PocketMC root directory has been selected.",
                pageType?.Name ?? "<unknown>");
            return;
        }

        if (!IsShellPageType(pageType))
        {
            return;
        }

        args.Cancel = true;
        if (_serviceProvider.GetService(typeof(IAppNavigationService)) is IAppNavigationService navigationService)
        {
            navigationService.NavigateToShellPage(pageType!);
            return;
        }

        ReplaceShellContent(pageType!);
    }

    private void SyncNavigationSelection(Type? pageType)
    {
        if (!IsShellPageType(pageType))
        {
            return;
        }

        NavigationViewItem? targetItem = GetShellNavigationItem(pageType);
        if (targetItem == null)
        {
            return;
        }

        try
        {
            PropertyInfo? selectedItemProperty = RootNavigation.GetType().GetProperty("SelectedItem");
            selectedItemProperty?.SetValue(RootNavigation, targetItem);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to synchronize NavigationView selected item for page {PageType}.", pageType?.Name);
        }

        SetNavigationItemActiveState(NavDashboard, ReferenceEquals(targetItem, NavDashboard));
        SetNavigationItemActiveState(NavTunnel, ReferenceEquals(targetItem, NavTunnel));
        SetNavigationItemActiveState(NavJavaSetup, ReferenceEquals(targetItem, NavJavaSetup));
        SetNavigationItemActiveState(NavAbout, ReferenceEquals(targetItem, NavAbout));
        SetNavigationItemActiveState(NavSettings, ReferenceEquals(targetItem, NavSettings));
    }

    private void ClearNavigationSelection()
    {
        try
        {
            PropertyInfo? selectedItemProperty = RootNavigation.GetType().GetProperty("SelectedItem");
            selectedItemProperty?.SetValue(RootNavigation, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clear the NavigationView selected item.");
        }

        SetNavigationItemActiveState(NavDashboard, false);
        SetNavigationItemActiveState(NavTunnel, false);
        SetNavigationItemActiveState(NavJavaSetup, false);
        SetNavigationItemActiveState(NavAbout, false);
        SetNavigationItemActiveState(NavSettings, false);
    }

    private NavigationViewItem? GetShellNavigationItem(Type? pageType)
    {
        if (pageType == typeof(DashboardPage)) return NavDashboard;
        if (pageType == typeof(TunnelPage)) return NavTunnel;
        if (pageType == typeof(JavaSetupPage)) return NavJavaSetup;
        if (pageType == typeof(AboutPage)) return NavAbout;
        if (pageType == typeof(AppSettingsPage)) return NavSettings;
        return null;
    }

    private Page GetOrCreateShellPage(Type pageType)
    {
        if (_shellPageCache.TryGetValue(pageType, out Page? cachedPage))
        {
            return cachedPage;
        }

        object page = _serviceProvider.GetRequiredService(pageType);
        if (page is not Page shellPage)
        {
            throw new InvalidOperationException($"{pageType.Name} is not a WPF Page.");
        }

        _shellPageCache[pageType] = shellPage;
        return shellPage;
    }

    private void SetNavigationItemActiveState(NavigationViewItem item, bool isActive)
    {
        try
        {
            PropertyInfo? isActiveProperty = item.GetType().GetProperty("IsActive");
            if (isActiveProperty?.CanWrite == true)
            {
                isActiveProperty.SetValue(item, isActive);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update NavigationViewItem active state for {ItemName}.", item.Name);
        }
    }

    private void AttachTitleBarContextSource(ITitleBarContextSource? source)
    {
        if (ReferenceEquals(_titleBarContextSource, source))
        {
            UpdateTitleBarContext();
            return;
        }

        DetachTitleBarContextSource();
        _titleBarContextSource = source;

        if (_titleBarContextSource != null)
        {
            _titleBarContextSource.TitleBarContextChanged += OnTitleBarContextChanged;
        }

        UpdateTitleBarContext();
    }

    private void DetachTitleBarContextSource()
    {
        if (_titleBarContextSource != null)
        {
            _titleBarContextSource.TitleBarContextChanged -= OnTitleBarContextChanged;
            _titleBarContextSource = null;
        }

        ClearTitleBarContext();
    }

    private void OnTitleBarContextChanged()
    {
        Dispatcher.Invoke(UpdateTitleBarContext);
    }

    private void UpdateTitleBarContext()
    {
        if (_titleBarContextSource == null)
        {
            ClearTitleBarContext();
            return;
        }

        _viewModel.TitleBarTitle = _titleBarContextSource.TitleBarContextTitle;
        _viewModel.TitleBarStatusText = _titleBarContextSource.TitleBarContextStatusText;
        _viewModel.TitleBarStatusBrush = _titleBarContextSource.TitleBarContextStatusBrush
            ?? TryFindBrush("TextFillColorSecondaryBrush", Brushes.Silver);

        _viewModel.IsTitleBarContextVisible = !string.IsNullOrWhiteSpace(_viewModel.TitleBarTitle) || 
                                              !string.IsNullOrWhiteSpace(_viewModel.TitleBarStatusText);
    }

    private void ClearTitleBarContext()
    {
        _viewModel.TitleBarTitle = null;
        _viewModel.TitleBarStatusText = null;
        _viewModel.IsTitleBarContextVisible = false;
    }

    private bool CanNavigateToPage(Type? pageType)
    {
        if (!_isNavigationLockedToRootSetup)
        {
            return true;
        }

        return pageType == typeof(RootDirectorySetupPage);
    }

    private static Type? GetRequestedPageType(object? page)
    {
        if (page is Type pageType) return pageType;
        if (page is Page pageInstance) return pageInstance.GetType();
        return page?.GetType();
    }

    public void SetNavigationLocked(bool isLocked)
    {
        _isNavigationLockedToRootSetup = isLocked;
        _viewModel.IsNavigationLocked = isLocked;

        if (isLocked)
        {
            DetachTitleBarContextSource();
            _viewModel.IsPaneVisible = false;
            _viewModel.IsPaneToggleVisible = false;
            SetShellNavigationEnabled(false);
            ClearNavigationSelection();
            UpdateBreadcrumb((Type?)null);
        }
        else
        {
            _viewModel.IsPaneVisible = true;
            _viewModel.IsPaneToggleVisible = true;
            SetShellNavigationEnabled(true);
            UpdateBreadcrumb((Type?)null);
        }
    }

    private void SetShellNavigationEnabled(bool isEnabled)
    {
        NavDashboard.IsEnabled = isEnabled;
        NavTunnel.IsEnabled = isEnabled;
        NavJavaSetup.IsEnabled = isEnabled;
        NavAbout.IsEnabled = isEnabled;
        NavSettings.IsEnabled = isEnabled;
    }

    // ──────────────────────────────────────────────
    //  Win10 Fallback Mica
    // ──────────────────────────────────────────────

    private void ApplyWin10MicaFallback()
    {
        if (WallpaperMicaService.IsWindows11OrLater || !_applicationState.Settings.EnableMicaEffect)
            return;

        var w = (int)Math.Max(ActualWidth, SystemParameters.PrimaryScreenWidth);
        var h = (int)Math.Max(ActualHeight, SystemParameters.PrimaryScreenHeight);

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var bg = WallpaperMicaService.CreateMicaBackground(
                    targetWidth: w,
                    targetHeight: h,
                    blurRadius: 80,
                    tintOpacity: 0.78,
                    tintColor: Color.FromRgb(32, 32, 32));

                Dispatcher.Invoke(() =>
                {
                    if (bg != null)
                    {
                        MicaFallbackBackground.Source = bg;
                        MicaFallbackBackground.Visibility = Visibility.Visible;
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Win10 Mica fallback failed.");
            }
        });
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.Desktop)
            ApplyWin10MicaFallback();
    }

    public void RequestMicaUpdate()
    {
        bool enableMica = _applicationState.Settings.EnableMicaEffect;

        if (WallpaperMicaService.IsWindows11OrLater)
        {
            WindowBackdropType = enableMica 
                ? Wpf.Ui.Controls.WindowBackdropType.Mica 
                : Wpf.Ui.Controls.WindowBackdropType.None;
        }
        else
        {
            if (enableMica)
            {
                ApplyWin10MicaFallback();
            }
            else
            {
                MicaFallbackBackground.Visibility = Visibility.Collapsed;
            }
        }
    }

    // ──────────────────────────────────────────────
    //  Global Health Monitor
    // ──────────────────────────────────────────────

    private void UpdateGlobalHealth()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                GlobalResourceSummary summary = _globalMonitor.CurrentSummary;
                _viewModel.GlobalHealthStatusText = summary.DisplayText;
                _viewModel.GlobalHealthStatusBrush = summary.IsHighUsage
                    ? Brushes.Red
                    : TryFindBrush("TextFillColorSecondaryBrush", Brushes.Silver);
            }
            catch
            {
                // Window may be closing or not fully initialized
            }
        });
    }

    private Brush TryFindBrush(string resourceKey, Brush fallback)
    {
        try
        {
            if (FindResource(resourceKey) is Brush brush)
                return brush;
        }
        catch { }
        return fallback;
    }

    // ──────────────────────────────────────────────
    //  Lifecycle
    // ──────────────────────────────────────────────

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!WallpaperMicaService.IsWindows11OrLater)
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        RootNavigation.Navigating -= OnNavigating;
        RootNavigation.Navigated -= OnNavigated;
        DetachTitleBarContextSource();
        _globalMonitor.OnGlobalMetricsUpdated -= UpdateGlobalHealth;
        _startupCoordinator.Shutdown();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _startupCoordinator.Start();
    }

    public void ShowRootDirectorySetup()
    {
        SetNavigationLocked(true);

        var setupPage = ActivatorUtilities.CreateInstance<RootDirectorySetupPage>(_serviceProvider);
        setupPage.DirectorySelected += OnRootDirectorySelected;
        setupPage.Unloaded += RootDirectorySetupPage_Unloaded;

        if (!RootNavigation.ReplaceContent(setupPage, null))
        {
            throw new InvalidOperationException("PocketMC could not open the root directory setup page.");
        }
    }

    private void RootDirectorySetupPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not RootDirectorySetupPage setupPage) return;
        setupPage.DirectorySelected -= OnRootDirectorySelected;
        setupPage.Unloaded -= RootDirectorySetupPage_Unloaded;
    }

    private void OnRootDirectorySelected(object? sender, string rootPath)
    {
        if (sender is RootDirectorySetupPage setupPage)
        {
            setupPage.DirectorySelected -= OnRootDirectorySelected;
            setupPage.Unloaded -= RootDirectorySetupPage_Unloaded;
        }

        _startupCoordinator.CompleteRootDirectorySelection(rootPath);
    }

    public void CompleteRootDirectorySetup()
    {
        SetNavigationLocked(false);
    }

    public bool NavigateToTunnel()
    {
        return NavigateToShellPage(typeof(TunnelPage));
    }

    public bool NavigateToPlayitGuide(string claimUrl, bool navigateToDashboardOnCompletion)
    {
        return Dispatcher.Invoke(() =>
        {
            var guidePage = ActivatorUtilities.CreateInstance<PlayitGuidePage>(_serviceProvider, claimUrl, navigateToDashboardOnCompletion);
            if (_serviceProvider.GetService(typeof(IAppNavigationService)) is IAppNavigationService navigationService)
            {
                return navigationService.NavigateToDetailPage(
                    guidePage,
                    "Playit.gg Setup",
                    DetailRouteKind.PlayitGuide,
                    DetailBackNavigation.Tunnel,
                    clearDetailStack: true);
            }

            return NavigateToDetailPage(guidePage, "Playit.gg Setup");
        });
    }

    public void ShowError(string title, string message)
    {
        System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }

    public void ShutdownApplication()
    {
        Application.Current.Shutdown();
    }
}
