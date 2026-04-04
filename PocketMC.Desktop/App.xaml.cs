using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PocketMC.Desktop.Services;
using PocketMC.Desktop.Views;

namespace PocketMC.Desktop;

public partial class App : Application
{
    private IHost? _host;

    public IServiceProvider Services =>
        _host?.Services ?? throw new InvalidOperationException("Application host has not been initialized.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<SettingsManager>();
                services.AddSingleton<ApplicationState>();
                services.AddSingleton<JobObject>();
                services.AddSingleton<ServerProcessManager>();
                services.AddSingleton<ResourceMonitorService>();
                services.AddSingleton<BackupService>();
                services.AddSingleton<BackupSchedulerService>();
                services.AddSingleton<PlayitApiClient>();
                services.AddSingleton<PlayitAgentService>();
                services.AddSingleton<InstanceManager>();
                services.AddSingleton<WorldManager>();
                services.AddTransient<TunnelService>();
                services.AddTransient<MainWindow>();
                services.AddTransient<JavaSetupPage>();
                services.AddTransient<DashboardPage>();
                services.AddTransient<NewInstanceDialog>();
            })
            .Build();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        await _host.StartAsync();

        var mainWindow = Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services.GetRequiredService<ILogger<App>>()
            .LogError(e.Exception, "Unhandled dispatcher exception.");
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Services.GetRequiredService<ILogger<App>>()
                .LogCritical(exception, "Unhandled non-UI exception.");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Services.GetRequiredService<ILogger<App>>()
            .LogError(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }
}
