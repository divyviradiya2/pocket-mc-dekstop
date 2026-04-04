using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PocketMC.Desktop.Services;
using PocketMC.Desktop.Views;

namespace PocketMC.Desktop;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
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
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this);
        Closing += MainWindow_Closing;

        _globalMonitor.OnGlobalMetricsUpdated += UpdateGlobalHealth;
    }

    private void UpdateGlobalHealth()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            GlobalResourceSummary summary = _globalMonitor.CurrentSummary;
            GlobalHealthTextBlock.Text = summary.DisplayText;

            if (summary.IsHighUsage)
                GlobalHealthTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            else
                GlobalHealthTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
        });
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _backupScheduler.Stop();
        _globalMonitor.OnGlobalMetricsUpdated -= UpdateGlobalHealth;
        _serverProcessManager.KillAll();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
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
            RootFrame.Navigate(_serviceProvider.GetRequiredService<JavaSetupPage>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to the Java setup page.");
            MessageBox.Show(
                "PocketMC could not initialize the main workflow. Check the debug log for details.",
                "Initialization Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Application.Current.Shutdown();
        }
    }
}
