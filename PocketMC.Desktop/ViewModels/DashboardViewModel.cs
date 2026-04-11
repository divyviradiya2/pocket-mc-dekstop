using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PocketMC.Desktop.Core.Interfaces;
using PocketMC.Desktop.Core.Mvvm;
using PocketMC.Desktop.Models;
using PocketMC.Desktop.Services;
using PocketMC.Desktop.Infrastructure;
using PocketMC.Desktop.Views;
using PocketMC.Desktop.Utils;

namespace PocketMC.Desktop.ViewModels
{
    public class DashboardViewModel : ViewModelBase
    {
        private readonly ApplicationState _applicationState;
        private readonly InstanceManager _instanceManager;
        private readonly ServerConfigurationService _serverConfigurationService;
        private readonly ServerProcessManager _serverProcessManager;
        private readonly ResourceMonitorService _resourceMonitorService;
        private readonly TunnelService _tunnelService;
        private readonly InstanceTunnelOrchestrator _tunnelOrchestrator;
        private readonly IDialogService _dialogService;
        private readonly IAppNavigationService _navigationService;
        private readonly IAppDispatcher _dispatcher;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<DashboardViewModel> _logger;
        private readonly object _lock = new();
        private bool _isActive;

        public ObservableCollection<InstanceCardViewModel> Instances { get; } = new();

        public ICommand NewInstanceCommand { get; }
        public ICommand RefreshInstancesCommand { get; }
        public ICommand StartServerCommand { get; }
        public ICommand StopServerCommand { get; }
        public ICommand DeleteInstanceCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand CopyCrashReportCommand { get; }
        public ICommand ServerSettingsCommand { get; }
        public ICommand OpenConsoleCommand { get; }

        public DashboardViewModel(
            ApplicationState applicationState,
            InstanceManager instanceManager,
            ServerConfigurationService serverConfigurationService,
            ServerProcessManager serverProcessManager,
            ResourceMonitorService resourceMonitorService,
            TunnelService tunnelService,
            InstanceTunnelOrchestrator tunnelOrchestrator,
            IDialogService dialogService,
            IAppNavigationService navigationService,
            IAppDispatcher dispatcher,
            IServiceProvider serviceProvider,
            ILogger<DashboardViewModel> logger)
        {
            _applicationState = applicationState;
            _instanceManager = instanceManager;
            _serverConfigurationService = serverConfigurationService;
            _serverProcessManager = serverProcessManager;
            _resourceMonitorService = resourceMonitorService;
            _tunnelService = tunnelService;
            _tunnelOrchestrator = tunnelOrchestrator;
            _dialogService = dialogService;
            _navigationService = navigationService;
            _dispatcher = dispatcher;
            _serviceProvider = serviceProvider;
            _logger = logger;

            NewInstanceCommand = new RelayCommand(_ => NavigateToNewInstance());
            RefreshInstancesCommand = new RelayCommand(_ => LoadInstances());
            StartServerCommand = new RelayCommand(StartServer);
            StopServerCommand = new RelayCommand(StopServer);
            DeleteInstanceCommand = new AsyncRelayCommand(DeleteInstanceAsync);
            OpenFolderCommand = new RelayCommand(OpenFolder);
            CopyCrashReportCommand = new RelayCommand(CopyCrashReport);
            ServerSettingsCommand = new RelayCommand(OpenSettings);
            OpenConsoleCommand = new RelayCommand(OpenConsole);
        }

        public void Activate()
        {
            if (_isActive)
            {
                LoadInstances();
                return;
            }

            _instanceManager.InstancesChanged += OnInstancesChanged;
            _serverProcessManager.OnInstanceStateChanged += OnInstanceStateChanged;
            _serverProcessManager.OnRestartCountdownTick += OnRestartCountdownTick;
            _resourceMonitorService.OnGlobalMetricsUpdated += OnGlobalMetricsUpdated;
            _isActive = true;
            LoadInstances();
        }

        public void Deactivate()
        {
            if (!_isActive) return;

            _instanceManager.InstancesChanged -= OnInstancesChanged;
            _serverProcessManager.OnInstanceStateChanged -= OnInstanceStateChanged;
            _serverProcessManager.OnRestartCountdownTick -= OnRestartCountdownTick;
            _resourceMonitorService.OnGlobalMetricsUpdated -= OnGlobalMetricsUpdated;
            _isActive = false;
        }

        private void OnInstancesChanged(object? sender, EventArgs e)
        {
            _dispatcher.Invoke(LoadInstances);
        }

        private void OnInstanceStateChanged(Guid instanceId, ServerState state)
        {
            _dispatcher.Invoke(() =>
            {
                var vm = Instances.FirstOrDefault(i => i.Id == instanceId);
                if (vm == null) return;

                vm.UpdateState(state);
                
                if (state == ServerState.Stopped || state == ServerState.Crashed)
                {
                    _applicationState.ClearTunnelAddress(instanceId);
                    vm.TunnelAddress = null;
                }

                ApplyLiveMetrics(vm);
            });
        }

        private void OnRestartCountdownTick(Guid instanceId, int secondsRemaining)
        {
            _dispatcher.Invoke(() =>
            {
                var vm = Instances.FirstOrDefault(i => i.Id == instanceId);
                if (vm != null)
                {
                    vm.UpdateCountdown(secondsRemaining);
                    ApplyLiveMetrics(vm);
                }
            });
        }

        private void OnGlobalMetricsUpdated()
        {
            _dispatcher.Invoke(UpdateAllLiveMetrics);
        }

        private void NavigateToNewInstance()
        {
            var newInstancePage = ActivatorUtilities.CreateInstance<NewInstancePage>(_serviceProvider);
            _navigationService.NavigateToDetailPage(
                newInstancePage,
                "New Instance",
                DetailRouteKind.NewInstance,
                DetailBackNavigation.Dashboard,
                clearDetailStack: true);
        }

        public void LoadInstances()
        {
            if (!_applicationState.IsConfigured) return;

            var existingVms = Instances.ToList();
            Instances.Clear();
            var metas = _instanceManager.GetAllInstances();
            foreach (var meta in metas)
            {
                var existing = existingVms.FirstOrDefault(v => v.Id == meta.Id);
                if (existing != null)
                {
                    existing.UpdateFromMetadata(meta);
                    Instances.Add(existing);
                }
                else
                {
                    var newVm = new InstanceCardViewModel(meta, _serverProcessManager);
                    Instances.Add(newVm);
                }
            }

            foreach (var vm in Instances)
            {
                var process = _serverProcessManager.GetProcess(vm.Id);
                if (process != null)
                {
                    vm.UpdateState(process.State);
                }

                if (TryGetServerProperty(vm.Id, "max-players", out string? maxPlayerStr) &&
                    int.TryParse(maxPlayerStr, out int maxPlayers) && maxPlayers > 0)
                {
                    vm.MaxPlayers = maxPlayers;
                }

                ApplyLiveMetrics(vm);

                var cached = _applicationState.GetTunnelAddress(vm.Id);
                if (!string.IsNullOrEmpty(cached))
                {
                    vm.TunnelAddress = cached;
                }
                else if (vm.State == ServerState.Online)
                {
                    _ = _tunnelOrchestrator.EnsureTunnelFlowAsync(vm.Id, vm.Name, addr => vm.TunnelAddress = addr);
                }
            }
        }

        private void UpdateAllLiveMetrics()
        {
            foreach (var vm in Instances)
            {
                ApplyLiveMetrics(vm);
            }
        }

        private void ApplyLiveMetrics(InstanceCardViewModel vm)
        {
            if (_resourceMonitorService.Metrics.TryGetValue(vm.Id, out var metrics))
            {
                double maxRamGb = vm.Metadata.MaxRamMb / 1024.0;
                double usedRamGb = metrics.RamUsageMb / 1024.0;

                vm.CpuText = $"{Math.Round(metrics.CpuUsage):0}%";
                vm.RamText = $"{usedRamGb:F1} / {maxRamGb:F0} GB";
                vm.PlayerStatus = $"{metrics.PlayerCount} / {vm.MaxPlayers}";
                return;
            }

            if (!vm.IsRunning)
            {
                vm.CpuText = "\u00b7 \u00b7 \u00b7";
                vm.RamText = "\u00b7 \u00b7 \u00b7";
                vm.PlayerStatus = "\u00b7 \u00b7 \u00b7";
            }
        }

        private async void StartServer(object? parameter)
        {
            if (parameter is InstanceCardViewModel vm)
            {
                try
                {
                    string? instancePath = _instanceManager.GetInstancePath(vm.Id);
                    if (instancePath == null) return;

                    // RAM Availability Check
                    var availableMb = MemoryHelper.GetAvailablePhysicalMemoryMb();
                    var requiredMb = (ulong)vm.Metadata.MaxRamMb;
                    if (availableMb < requiredMb + 512) // Reserve 512MB for OS
                    {
                        var result = await _dialogService.ShowDialogAsync("Low Memory", 
                            $"Your system only has {availableMb}MB of available RAM. Starting this server ({requiredMb}MB) might cause significant lag or crashes.\n\n" +
                            "Close other applications like Chrome or Spotify to free up memory.\n\nContinue anyway?", 
                            DialogType.Warning, true);
                        if (result != DialogResult.Yes) return;
                    }

                    // Port Collision Check
                    int targetPort = _serverConfigurationService.GetActivePortForInstance(vm.Id);
                    var otherRunningPaths = _serverProcessManager.ActiveProcesses
                        .Where(kvp => kvp.Key != vm.Id)
                        .Select(kvp => _instanceManager.GetInstancePath(kvp.Key))
                        .Where(p => p != null)
                        .ToList();

                    foreach (var otherPath in otherRunningPaths)
                    {
                        if (_serverConfigurationService.TryGetProperty(otherPath!, "server-port", out string? otherPortStr) &&
                            int.TryParse(otherPortStr, out int otherPort) &&
                            otherPort == targetPort)
                        {
                            var result = await _dialogService.ShowDialogAsync("Port Collision",
                                $"Another running server is already using port {targetPort}.\n\n" +
                                "Change the port for this server in Settings -> Networking before starting, or stop the other server.",
                                DialogType.Warning);
                            return;
                        }
                    }

                    var process = await _serverProcessManager.StartProcessAsync(vm.Metadata, _applicationState.GetRequiredAppRootPath());
                    vm.UpdateState(process.State);
                    ApplyLiveMetrics(vm);

                    _ = _tunnelOrchestrator.EnsureTunnelFlowAsync(vm.Id, vm.Name, addr => vm.TunnelAddress = addr);
                }
                catch (Exception ex)
                {
                    vm.UpdateState(ServerState.Stopped);
                    ApplyLiveMetrics(vm);
                    _logger.LogError(ex, "Failed to start server {ServerName}.", vm.Name);
                    _dialogService.ShowMessage("Start Failed", $"PocketMC could not start '{vm.Name}'.\n\n{ex.Message}", DialogType.Error);
                }
            }
        }

        private bool TryGetServerProperty(Guid instanceId, string key, out string? value)
        {
            value = null;
            string? instancePath = _instanceManager.GetInstancePath(instanceId);
            if (string.IsNullOrWhiteSpace(instancePath)) return false;
            return _serverConfigurationService.TryGetProperty(instancePath, key, out value);
        }

        private async void StopServer(object? parameter)
        {
            if (parameter is InstanceCardViewModel vm)
            {
                try
                {
                    if (_serverProcessManager.IsWaitingToRestart(vm.Id))
                    {
                        _serverProcessManager.AbortRestartDelay(vm.Id);
                        vm.UpdateState(ServerState.Crashed);
                        ApplyLiveMetrics(vm);
                        return;
                    }

                    if (_serverProcessManager.GetProcess(vm.Id) == null) return;

                    vm.UpdateState(ServerState.Stopping);
                    await _serverProcessManager.StopProcessAsync(vm.Id);
                }
                catch (Exception ex)
                {
                    var currentState = _serverProcessManager.GetProcess(vm.Id)?.State ?? ServerState.Stopped;
                    vm.UpdateState(currentState);
                    ApplyLiveMetrics(vm);
                    _logger.LogError(ex, "Failed to stop server {ServerName}.", vm.Name);
                    _dialogService.ShowMessage("Stop Failed", $"PocketMC could not stop '{vm.Name}' cleanly.\n\n{ex.Message}", DialogType.Error);
                }
                finally
                {
                    _applicationState.ClearTunnelAddress(vm.Id);
                    vm.TunnelAddress = null;
                }
            }
        }

        private async Task DeleteInstanceAsync(object? parameter)
        {
            if (parameter is InstanceCardViewModel vm)
            {
                if (_serverProcessManager.IsRunning(vm.Id))
                {
                    _dialogService.ShowMessage("Server Running", "Cannot delete a running server. Stop it first.", DialogType.Warning);
                    return;
                }

                var prompt = await _dialogService.ShowDialogAsync("Delete Server", $"Are you sure you want to completely erase the {vm.Name} server? All worlds and files will be permanently deleted.", DialogType.Warning, false);
                if (prompt == DialogResult.Yes)
                {
                    try
                    {
                        bool deleted = await _instanceManager.DeleteInstanceAsync(vm.Id);
                        if (!deleted)
                        {
                            _dialogService.ShowMessage("Delete Failed", $"PocketMC could not delete '{vm.Name}'. Close any apps using its files and try again.", DialogType.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete server {ServerName}.", vm.Name);
                        _dialogService.ShowMessage("Delete Failed", $"PocketMC could not delete '{vm.Name}'.\n\n{ex.Message}", DialogType.Error);
                    }
                }
            }
        }

        private void OpenFolder(object? parameter)
        {
            if (parameter is InstanceCardViewModel vm)
            {
                string? path = _instanceManager.GetInstancePath(vm.Id);
                if (path != null && Directory.Exists(path))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true, Verb = "open" });
                }
            }
        }

        private void CopyCrashReport(object? parameter)
        {
            if (parameter is InstanceCardViewModel vm)
            {
                string? path = _instanceManager.GetInstancePath(vm.Id);
                if (path == null) return;

                var crashReportsDir = Path.Combine(path, "crash-reports");
                if (Directory.Exists(crashReportsDir))
                {
                    var latestReport = new DirectoryInfo(crashReportsDir).GetFiles("*.txt").OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                    if (latestReport != null)
                    {
                        string content = File.ReadAllText(latestReport.FullName);
                        System.Windows.Clipboard.SetText(content);
                        _dialogService.ShowMessage("Copied", "The latest crash report has been copied to your clipboard.", DialogType.Information);
                        return;
                    }
                }
                _dialogService.ShowMessage("No Crash Reports", "No crash reports found for this server.", DialogType.Information);
            }
        }

        private void OpenSettings(object? parameter)
        {
            if (parameter is InstanceCardViewModel vm)
            {
                var settingsViewModel = ActivatorUtilities.CreateInstance<ServerSettingsViewModel>(_serviceProvider, vm.Metadata);
                var settingsPage = ActivatorUtilities.CreateInstance<ServerSettingsPage>(_serviceProvider, settingsViewModel);
                _navigationService.NavigateToDetailPage(settingsPage, $"Settings: {vm.Name}", DetailRouteKind.ServerSettings, DetailBackNavigation.Dashboard, clearDetailStack: true);
            }
        }

        private void OpenConsole(object? parameter)
        {
            if (parameter is InstanceCardViewModel vm)
            {
                var process = _serverProcessManager.GetProcess(vm.Id);
                if (process == null)
                {
                    _dialogService.ShowMessage("Unavailable", "Start the server at least once before opening the console.", DialogType.Information);
                    return;
                }

                var consolePage = ActivatorUtilities.CreateInstance<ServerConsolePage>(_serviceProvider, vm.Metadata, process);
                _navigationService.NavigateToDetailPage(consolePage, $"Console: {vm.Name}", DetailRouteKind.ServerConsole, DetailBackNavigation.Dashboard, clearDetailStack: true);
            }
        }
    }
}
