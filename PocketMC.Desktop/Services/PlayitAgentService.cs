using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PocketMC.Desktop.Utils;

namespace PocketMC.Desktop.Services
{
    /// <summary>
    /// Represents the current state of the Playit.gg background agent.
    /// </summary>
    public enum PlayitAgentState
    {
        Stopped,
        Starting,
        WaitingForClaim,
        Connected,
        Error,
        Disconnected
    }

    /// <summary>
    /// Manages the lifecycle of playit.exe as an app-scoped background agent.
    /// The agent starts when PocketMC starts and stops when PocketMC closes.
    /// Implements NET-02, NET-03, NET-04, NET-05, NET-11.
    /// </summary>
    public class PlayitAgentService : IDisposable
    {
        private static readonly Regex ClaimUrlRegex = new(
            @"(Visit link to setup |Approve program at )(?<url>https://playit\.gg/claim/[A-Za-z0-9\-]+)",
            RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));

        private static readonly Regex TunnelRunningRegex = new(
            @"tunnel running",
            RegexOptions.Compiled | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        private Process? _agentProcess;
        private readonly JobObject _jobObject;
        private readonly ApplicationState _applicationState;
        private readonly SettingsManager _settingsManager;
        private readonly ILogger<PlayitAgentService> _logger;
        private StreamWriter? _logWriter;
        private bool _disposed;
        private bool _claimUrlAlreadyFired;

        public PlayitAgentState State { get; private set; } = PlayitAgentState.Stopped;

        /// <summary>
        /// Fires when the agent outputs a claim URL for first-time setup.
        /// The string parameter is the full claim URL (e.g., https://playit.gg/claim/ABC123).
        /// </summary>
        public event EventHandler<string>? OnClaimUrlReceived;

        /// <summary>
        /// Fires when the agent has successfully connected ("tunnel running" detected).
        /// </summary>
        public event EventHandler? OnTunnelRunning;

        /// <summary>
        /// Fires when the agent state changes.
        /// </summary>
        public event EventHandler<PlayitAgentState>? OnStateChanged;

        /// <summary>
        /// Fires when the agent process exits unexpectedly.
        /// </summary>
        public event EventHandler<int>? OnAgentExited;

        public PlayitAgentService(
            ApplicationState applicationState,
            SettingsManager settingsManager,
            JobObject jobObject,
            ILogger<PlayitAgentService> logger)
        {
            _applicationState = applicationState;
            _settingsManager = settingsManager;
            _jobObject = jobObject;
            _logger = logger;
        }

        public bool IsBinaryAvailable =>
            _applicationState.IsConfigured && File.Exists(_applicationState.GetPlayitExecutablePath());

        /// <summary>
        /// Starts the playit.exe agent process (NET-02).
        /// </summary>
        public void Start()
        {
            if (_agentProcess != null && !_agentProcess.HasExited)
                return; // Already running

            if (!_applicationState.IsConfigured)
            {
                _logger.LogWarning("Playit agent start was requested before the app root path was configured.");
                SetState(PlayitAgentState.Error);
                return;
            }

            string playitPath = _applicationState.GetPlayitExecutablePath();
            if (!File.Exists(playitPath))
            {
                SetState(PlayitAgentState.Error);
                Log("ERROR: playit.exe not found at " + playitPath);
                return;
            }

            string logFilePath = Path.Combine(_applicationState.GetRequiredAppRootPath(), "tunnel", "playit-agent.log");

            // Ensure log directory exists
            string? logDir = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(logDir))
                Directory.CreateDirectory(logDir);

            _logWriter = new StreamWriter(logFilePath, append: true, encoding: Encoding.UTF8)
            {
                AutoFlush = true
            };

            var psi = new ProcessStartInfo
            {
                FileName = playitPath,
                Arguments = "--stdout",  // Required: without this flag, playit writes to console buffer directly
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            _claimUrlAlreadyFired = false;
            SetState(PlayitAgentState.Starting);

            _agentProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _agentProcess.Exited += OnProcessExited;
            _agentProcess.Start();

            // Assign to Job Object so it terminates when PocketMC crashes (NET-02)
            try { _jobObject.AddProcess(_agentProcess.Handle); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to assign playit.exe to the job object.");
            }

            Log("INFO: playit.exe started (PID: " + _agentProcess.Id + ")");

            // Start reading stdout/stderr on background threads (NET-11)
            Task.Run(() => ReadOutputAsync(_agentProcess.StandardOutput));
            Task.Run(() => ReadErrorAsync(_agentProcess.StandardError));
        }

        /// <summary>
        /// Stops the playit.exe agent gracefully (NET-02).
        /// </summary>
        public void Stop()
        {
            if (_agentProcess == null || _agentProcess.HasExited)
            {
                SetState(PlayitAgentState.Stopped);
                _logWriter?.Dispose();
                _logWriter = null;
                return;
            }

            try
            {
                _agentProcess.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop playit.exe cleanly.");
            }

            SetState(PlayitAgentState.Stopped);
            Log("INFO: playit.exe stopped");
            _logWriter?.Dispose();
            _logWriter = null;
            _agentProcess.Dispose();
            _agentProcess = null;
        }

        private async Task ReadOutputAsync(StreamReader reader)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    string safeLine = LogSanitizer.SanitizePlayitLine(line);
                    Log("STDOUT: " + safeLine);

                    // Auto-reset when playit has an invalid token.
                    // Playit uses crossterm bypassing stdin, so piping 'Y' doesn't work.
                    // Instead, we forcefully delete the secret and restart the process.
                    if (line.Contains("Invalid secret, do you want to reset", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("INFO: Invalid secret detected. Deleting playit.toml and restarting...");
                        
                        try
                        {
                            string tomlPath = _settingsManager.GetPlayitTomlPath(_applicationState.Settings);
                            
                            if (File.Exists(tomlPath))
                                File.Delete(tomlPath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete playit config while recovering from an invalid secret.");
                        }

                        // Schedule a restart (using a fire-and-forget task so we don't block the output reader)
                        _ = Task.Run(async () =>
                        {
                            Stop();
                            await Task.Delay(500);
                            Start();
                        });
                        
                        // Break out of this reader loop immediately so we stop processing output for the dying process
                        break;
                    }

                    // Claim URL detection (NET-03, NET-04)
                    var claimMatch = ClaimUrlRegex.Match(line);
                    if (claimMatch.Success && !_claimUrlAlreadyFired)
                    {
                        _claimUrlAlreadyFired = true;
                        string url = claimMatch.Groups["url"].Value;
                        SetState(PlayitAgentState.WaitingForClaim);
                        Log("INFO: Claim URL detected and forwarded to the setup flow.");
                        OnClaimUrlReceived?.Invoke(this, url);
                    }

                    // Tunnel running detection (NET-04)
                    if (TunnelRunningRegex.IsMatch(line))
                    {
                        SetState(PlayitAgentState.Connected);
                        Log("INFO: Agent connected — tunnel running");
                        OnTunnelRunning?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("Playit stdout reader stopped because the process was disposed.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogDebug(ex, "Playit stdout reader stopped because the process changed state.");
            }
        }

        private async Task ReadErrorAsync(StreamReader reader)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    Log("STDERR: " + LogSanitizer.SanitizePlayitLine(line));
                }
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("Playit stderr reader stopped because the process was disposed.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogDebug(ex, "Playit stderr reader stopped because the process changed state.");
            }
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            int exitCode = _agentProcess?.ExitCode ?? -1;
            Log($"INFO: playit.exe exited with code {exitCode}");

            if (State == PlayitAgentState.Connected || State == PlayitAgentState.Starting)
            {
                // Unexpected exit — attempt one restart (NET-18 in success criteria)
                SetState(PlayitAgentState.Error);
                Log("WARN: Unexpected agent exit — attempting single restart");
                OnAgentExited?.Invoke(this, exitCode);

                try
                {
                    Start();
                }
                catch (Exception ex)
                {
                    SetState(PlayitAgentState.Error);
                    Log("ERROR: Restart failed — agent offline");
                    _logger.LogError(ex, "Failed to restart playit.exe after an unexpected exit.");
                }
            }
            else
            {
                SetState(PlayitAgentState.Stopped);
            }
        }

        private void SetState(PlayitAgentState newState)
        {
            if (State != newState)
            {
                State = newState;
                OnStateChanged?.Invoke(this, newState);
            }
        }

        private void Log(string message)
        {
            string timestamped = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {LogSanitizer.SanitizeConsoleLine(message)}";
            try
            {
                _logWriter?.WriteLine(timestamped);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write to the playit agent log.");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Stop();
                _agentProcess?.Dispose();
            }
        }
    }
}
