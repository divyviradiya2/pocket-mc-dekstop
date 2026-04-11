using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PocketMC.Desktop.Core.Interfaces;
using PocketMC.Desktop.Models;
using PocketMC.Desktop.Infrastructure;

namespace PocketMC.Desktop.Services;

/// <summary>
/// Global singleton managing all active Minecraft server processes.
/// Thread-safe via ConcurrentDictionary.
/// </summary>
public class ServerProcessManager
{
    private readonly JobObject _jobObject;
    private readonly InstanceManager _instanceManager;
    private readonly INotificationService _notificationService;
    private readonly ServerLaunchConfigurator _launchConfigurator;
    private readonly ILogger<ServerProcessManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<Guid, ServerProcess> _activeProcesses = new();
    private readonly ConcurrentDictionary<Guid, ServerProcess> _historicalProcesses = new();

    // Auto-Restart Tracking State
    private readonly ConcurrentDictionary<Guid, int> _consecutiveRestarts = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastStartTime = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _restartCancellations = new();

    public ServerProcessManager(
        JobObject jobObject,
        InstanceManager instanceManager,
        INotificationService notificationService,
        ServerLaunchConfigurator launchConfigurator,
        ILogger<ServerProcessManager> logger,
        ILoggerFactory loggerFactory)
    {
        _jobObject = jobObject;
        _instanceManager = instanceManager;
        _notificationService = notificationService;
        _launchConfigurator = launchConfigurator;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Fires when any instance changes state (started, stopped, crashed).
    /// </summary>
    public event Action<Guid, ServerState>? OnInstanceStateChanged;

    /// <summary>
    /// Fires every second while a crashed server is waiting to auto-restart.
    /// </summary>
    public event Action<Guid, int>? OnRestartCountdownTick;

    public ConcurrentDictionary<Guid, ServerProcess> ActiveProcesses => _activeProcesses;

    public async Task<ServerProcess> StartProcessAsync(InstanceMetadata meta, string appRootPath)
    {
        if (_activeProcesses.ContainsKey(meta.Id))
        {
            throw new InvalidOperationException($"Server '{meta.Name}' is already running.");
        }

        if (_lastStartTime.TryGetValue(meta.Id, out var lastStart) &&
            (DateTime.UtcNow - lastStart).TotalMinutes > 10)
        {
            _consecutiveRestarts[meta.Id] = 0;
        }

        _lastStartTime[meta.Id] = DateTime.UtcNow;
        var instancePath = _instanceManager.GetInstancePath(meta.Id);
        if (string.IsNullOrEmpty(instancePath))
        {
            throw new DirectoryNotFoundException($"Could not locate directory for instance {meta.Name}.");
        }

        var serverProcess = new ServerProcess(
            meta.Id,
            _jobObject,
            _launchConfigurator,
            _loggerFactory.CreateLogger<ServerProcess>());

        serverProcess.OnStateChanged += state =>
        {
            OnInstanceStateChanged?.Invoke(meta.Id, state);
            if (state == ServerState.Stopped || state == ServerState.Crashed)
            {
                _activeProcesses.TryRemove(meta.Id, out _);
            }
        };

        serverProcess.OnServerCrashed += async crashLog =>
        {
            _logger.LogWarning("Server {ServerName} crashed. Crash context length: {CrashLength}", meta.Name, crashLog.Length);
            await HandleServerCrashAsync(meta, appRootPath);
        };

        _activeProcesses[meta.Id] = serverProcess;
        _historicalProcesses[meta.Id] = serverProcess;

        try
        {
            await serverProcess.StartAsync(meta, instancePath, appRootPath);
        }
        catch
        {
            _activeProcesses.TryRemove(meta.Id, out _);
            throw;
        }

        return serverProcess;
    }

    private async Task HandleServerCrashAsync(InstanceMetadata meta, string appRootPath)
    {
        if (!meta.EnableAutoRestart) return;

        int attempts = _consecutiveRestarts.GetOrAdd(meta.Id, 0);
        if (attempts >= meta.MaxAutoRestarts)
        {
            _logger.LogWarning("Server {ServerName} reached the max auto-restart limit.", meta.Name);
            _notificationService.ShowInformation("PocketMC Server Crashed", $"Server '{meta.Name}' hit the max auto-restart limit.");
            return;
        }

        var cts = new CancellationTokenSource();
        _restartCancellations[meta.Id] = cts;

        var backoffSeconds = (int)Math.Min(meta.AutoRestartDelaySeconds * Math.Pow(2, attempts), 300);
        
        try
        {
            for (int i = backoffSeconds; i > 0; i--)
            {
                if (cts.Token.IsCancellationRequested) break;
                OnRestartCountdownTick?.Invoke(meta.Id, i);
                await Task.Delay(1000, cts.Token);
            }
        }
        catch (TaskCanceledException) { }
        finally { _restartCancellations.TryRemove(meta.Id, out _); }

        if (!cts.IsCancellationRequested)
        {
            _consecutiveRestarts[meta.Id] = attempts + 1;
            await StartProcessAsync(meta, appRootPath);
        }
    }

    public bool IsWaitingToRestart(Guid instanceId) => _restartCancellations.ContainsKey(instanceId);

    public static int CalculateRestartDelaySeconds(InstanceMetadata meta, int consecutiveRestarts)
    {
        return CalculateRestartDelaySeconds(meta.AutoRestartDelaySeconds, consecutiveRestarts);
    }

    public static int CalculateRestartDelaySeconds(int baseDelay, int consecutiveRestarts)
    {
        return (int)Math.Min(baseDelay * Math.Pow(2, consecutiveRestarts), 300);
    }

    public void AbortRestartDelay(Guid instanceId)
    {
        if (_restartCancellations.TryGetValue(instanceId, out var cts))
        {
            cts.Cancel();
            OnInstanceStateChanged?.Invoke(instanceId, ServerState.Crashed);
        }
    }

    public async Task StopProcessAsync(Guid instanceId)
    {
        AbortRestartDelay(instanceId);
        if (_activeProcesses.TryGetValue(instanceId, out var process))
            await process.StopAsync();
    }

    public void KillProcess(Guid instanceId)
    {
        AbortRestartDelay(instanceId);
        if (_activeProcesses.TryGetValue(instanceId, out var process))
            process.Kill();
    }

    public bool IsRunning(Guid instanceId)
    {
        return _activeProcesses.ContainsKey(instanceId) &&
               _activeProcesses[instanceId].State != ServerState.Stopped &&
               _activeProcesses[instanceId].State != ServerState.Crashed;
    }

    public ServerProcess? GetProcess(Guid instanceId)
    {
        if (_activeProcesses.TryGetValue(instanceId, out var process)) return process;
        _historicalProcesses.TryGetValue(instanceId, out var historical);
        return historical;
    }

    public void KillAll()
    {
        foreach (var cts in _restartCancellations.Values) cts.Cancel();
        _restartCancellations.Clear();
        foreach (var kvp in _activeProcesses)
        {
            try { kvp.Value.Kill(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill server instance {InstanceId}.", kvp.Key); }
        }
        _activeProcesses.Clear();
    }
}
