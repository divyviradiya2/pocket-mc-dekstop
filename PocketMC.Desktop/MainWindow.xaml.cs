using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PocketMC.Desktop.Services;
using PocketMC.Desktop.Views;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace PocketMC.Desktop;

public partial class MainWindow : FluentWindow
{
    private readonly IServiceProvider _serviceProvider;
    private readonly SettingsManager _settingsManager;
    private readonly ApplicationState _applicationState;
    private readonly ResourceMonitorService _globalMonitor;
    private readonly BackupSchedulerService _backupScheduler;
    private readonly ServerProcessManager _serverProcessManager;
    private readonly ILogger<MainWindow> _logger;

    public MainWindow(
        IServiceProvider serviceProvider,
        SettingsManager settingsManager,
        ApplicationState applicationState,
        ResourceMonitorService globalMonitor,
        BackupSchedulerService backupScheduler,
        ServerProcessManager serverProcessManager,
        ILogger<MainWindow> logger)
    {
        _serviceProvider = serviceProvider;
        _settingsManager = settingsManager;
        _applicationState = applicationState;
        _globalMonitor = globalMonitor;
        _backupScheduler = backupScheduler;
        _serverProcessManager = serverProcessManager;
        _logger = logger;

        InitializeComponent();

        // Wire up WPF-UI theme engine
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this);

        // Set up the NavigationView to resolve pages via DI
        RootNavigation.SetServiceProvider(_serviceProvider);

        // Listen for navigation events to update breadcrumb
        RootNavigation.Navigated += OnNavigated;

        Closing += MainWindow_Closing;
        _globalMonitor.OnGlobalMetricsUpdated += UpdateGlobalHealth;

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
        UpdateBreadcrumb(pageType);
    }

    private void UpdateBreadcrumb(Type? pageType)
    {
        string? label = pageType?.Name switch
        {
            nameof(DashboardPage)      => "Dashboard",
            nameof(JavaSetupPage)       => "Java Setup",
            nameof(ServerSettingsPage)  => "Server Settings",
            nameof(ServerConsolePage)   => "Console",
            _ => null
        };

        if (!string.IsNullOrEmpty(label))
        {
            BreadcrumbSeparator.Visibility = Visibility.Visible;
            BreadcrumbCurrent.Text = label;
            BreadcrumbCurrent.Visibility = Visibility.Visible;
        }
        else
        {
            BreadcrumbSeparator.Visibility = Visibility.Collapsed;
            BreadcrumbCurrent.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Allows child pages to navigate within the NavigationView.
    /// Called from pages that need to navigate to ServerSettings/Console.
    /// </summary>
    public void NavigateToPage(Type pageType, object? dataContext = null)
    {
        RootNavigation.Navigate(pageType);
    }

    // ──────────────────────────────────────────────
    //  Win10 Fallback Mica
    // ──────────────────────────────────────────────

    private void ApplyWin10MicaFallback()
    {
        if (WallpaperMicaService.IsWindows11OrLater)
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
                GlobalHealthTextBlock.Text = summary.DisplayText;

                GlobalHealthTextBlock.Foreground = summary.IsHighUsage
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

        _backupScheduler.Stop();
        _globalMonitor.OnGlobalMetricsUpdated -= UpdateGlobalHealth;
        _serverProcessManager.KillAll();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyWin10MicaFallback();

        var settings = _settingsManager.Load();

        if (string.IsNullOrEmpty(settings.AppRootPath))
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select First-Run Root Folder for PocketMC",
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                settings.AppRootPath = dialog.FolderName;
                _settingsManager.Save(settings);
            }
            else
            {
                Application.Current.Shutdown();
                return;
            }
        }

        _applicationState.ApplySettings(settings);
        _backupScheduler.Start();

        try
        {
            // Navigate to Dashboard — Java Setup is now a management page
            RootNavigation.Navigate(typeof(DashboardPage));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to the Dashboard page.");
            System.Windows.MessageBox.Show(
                "PocketMC could not initialize the main workflow. Check the debug log for details.",
                "Initialization Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Application.Current.Shutdown();
        }
    }
}
