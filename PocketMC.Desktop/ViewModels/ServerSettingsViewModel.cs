using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PocketMC.Desktop.Core.Interfaces;
using PocketMC.Desktop.Core.Mvvm;
using PocketMC.Desktop.Models;
using PocketMC.Desktop.Services;
using PocketMC.Desktop.Utils;
using PocketMC.Desktop.ViewModels.Settings;

namespace PocketMC.Desktop.ViewModels
{
    public class ServerSettingsViewModel : ViewModelBase, IDisposable
    {
        private readonly InstanceManager _instanceManager;
        private readonly ServerConfigurationService _serverConfigurationService;
        private readonly ServerProcessManager _serverProcessManager;
        private readonly IDialogService _dialogService;
        private readonly IAppNavigationService _navigationService;
        private readonly IAppDispatcher _dispatcher;
        private readonly ILogger<ServerSettingsViewModel> _logger;
        private readonly Action<Guid, ServerState> _instanceStateChangedHandler;

        public InstanceMetadata Metadata { get; }
        public string ServerDir { get; }

        public ServerAddonsViewModel Addons { get; }
        public ServerBackupsViewModel Backups { get; }
        public ServerWorldViewModel World { get; }

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }

        private bool _isRunning;
        public bool IsRunning { get => _isRunning; set => SetProperty(ref _isRunning, value); }

        private bool _hasUnsavedChanges;
        public bool HasUnsavedChanges { get => _hasUnsavedChanges; set => SetProperty(ref _hasUnsavedChanges, value); }

        // General Properties
        private string? _motd;
        public string? Motd { get => _motd; set { if (SetProperty(ref _motd, value)) MarkChanged(); } }

        private string? _javaPath;
        public string? JavaPath { get => _javaPath; set { if (SetProperty(ref _javaPath, value)) MarkChanged(); } }

        private string? _advancedJvmArgs;
        public string? AdvancedJvmArgs { get => _advancedJvmArgs; set { if (SetProperty(ref _advancedJvmArgs, value)) MarkChanged(); } }

        private BitmapImage? _serverIcon;
        public BitmapImage? ServerIcon { get => _serverIcon; set => SetProperty(ref _serverIcon, value); }

        private double _minRam = 1024;
        public double MinRam { get => _minRam; set { if (SetProperty(ref _minRam, value)) { MarkChanged(); OnPropertyChanged(nameof(MinRamDisplay)); CheckRamWarning(); } } }
        public string MinRamDisplay => $"{MinRam:N0} MB";

        private double _maxRam = 4096;
        public double MaxRam { get => _maxRam; set { if (SetProperty(ref _maxRam, value)) { MarkChanged(); OnPropertyChanged(nameof(MaxRamDisplay)); CheckRamWarning(); } } }
        public string MaxRamDisplay => $"{MaxRam:N0} MB";

        private bool _showRamWarning;
        public bool ShowRamWarning { get => _showRamWarning; set => SetProperty(ref _showRamWarning, value); }
        private readonly double _totalRamMb;
        public double TotalRamMb => _totalRamMb;

        // Core Server Props
        private string? _seed;
        public string? Seed { get => _seed; set { if (SetProperty(ref _seed, value)) MarkChanged(); } }
        private string _levelType = "minecraft:normal";
        public string LevelType { get => _levelType; set { if (SetProperty(ref _levelType, value)) MarkChanged(); } }
        public string[] LevelTypes { get; } = { "minecraft:normal", "minecraft:flat", "minecraft:large_biomes", "minecraft:amplified", "minecraft:single_biome_surface" };
        private string _spawnProtection = "16";
        public string SpawnProtection { get => _spawnProtection; set { if (SetProperty(ref _spawnProtection, value)) MarkChanged(); } }
        private string _maxPlayers = "20";
        public string MaxPlayers { get => _maxPlayers; set { if (SetProperty(ref _maxPlayers, value)) MarkChanged(); } }
        private bool _onlineMode = true;
        public bool OnlineMode { get => _onlineMode; set { if (SetProperty(ref _onlineMode, value)) MarkChanged(); } }
        private bool _pvp = true;
        public bool Pvp { get => _pvp; set { if (SetProperty(ref _pvp, value)) MarkChanged(); } }
        private bool _whiteList = false;
        public bool WhiteList { get => _whiteList; set { if (SetProperty(ref _whiteList, value)) MarkChanged(); } }
        private string _serverPort = "25565";
        public string ServerPort { get => _serverPort; set { if (SetProperty(ref _serverPort, value)) MarkChanged(); } }
        private string? _serverIp;
        public string? ServerIp { get => _serverIp; set { if (SetProperty(ref _serverIp, value)) MarkChanged(); } }
        private string _gamemode = "survival";
        public string Gamemode { get => _gamemode; set { if (SetProperty(ref _gamemode, value)) MarkChanged(); } }
        public string[] Gamemodes { get; } = { "survival", "creative", "adventure", "spectator" };
        private string _difficulty = "easy";
        public string Difficulty { get => _difficulty; set { if (SetProperty(ref _difficulty, value)) MarkChanged(); } }
        public string[] Difficulties { get; } = { "peaceful", "easy", "normal", "hard" };
        private bool _allowBlock = false;
        public bool AllowBlock { get => _allowBlock; set { if (SetProperty(ref _allowBlock, value)) MarkChanged(); } }
        private bool _allowFlight = false;
        public bool AllowFlight { get => _allowFlight; set { if (SetProperty(ref _allowFlight, value)) MarkChanged(); } }
        private bool _allowNether = true;
        public bool AllowNether { get => _allowNether; set { if (SetProperty(ref _allowNether, value)) MarkChanged(); } }

        public ObservableCollection<PropertyItem> AdvancedProperties { get; } = new();

        private string _rawServerProperties = "";
        private bool _isLoadingRawServerProperties;
        private bool _isRawServerPropertiesDirty;
        public string RawServerProperties
        {
            get => _rawServerProperties;
            set
            {
                if (SetProperty(ref _rawServerProperties, value))
                {
                    if (!_isLoadingRawServerProperties) _isRawServerPropertiesDirty = true;
                    MarkChanged();
                }
            }
        }

        private PropertyItem? _selectedAdvancedProperty;
        public PropertyItem? SelectedAdvancedProperty { get => _selectedAdvancedProperty; set { if (SetProperty(ref _selectedAdvancedProperty, value)) CommandManager.InvalidateRequerySuggested(); } }

        // Crash/Restart
        private bool _enableAutoRestart;
        public bool EnableAutoRestart { get => _enableAutoRestart; set { if (SetProperty(ref _enableAutoRestart, value)) MarkChanged(); } }
        private string _maxAutoRestarts = "3";
        public string MaxAutoRestarts { get => _maxAutoRestarts; set { if (SetProperty(ref _maxAutoRestarts, value)) MarkChanged(); } }
        private string _autoRestartDelay = "10";
        public string AutoRestartDelay { get => _autoRestartDelay; set { if (SetProperty(ref _autoRestartDelay, value)) MarkChanged(); } }

        // Network status (simplified)
        private string _playitAddress = "Resolving tunnel...";
        public string PlayitAddress { get => _playitAddress; set => SetProperty(ref _playitAddress, value); }

        // Commands
        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand BrowseIconCommand { get; }
        public ICommand BrowseJavaCommand { get; }
        public ICommand ResolvePlayitCommand { get; }
        public ICommand OpenPlayitDashboardCommand { get; }
        public ICommand AddAdvancedPropertyCommand { get; }
        public ICommand DeleteAdvancedPropertyCommand { get; }

        public ServerSettingsViewModel(
            InstanceMetadata metadata,
            InstanceManager instanceManager,
            ServerConfigurationService serverConfigurationService,
            ServerProcessManager serverProcessManager,
            WorldManager worldManager,
            BackupService backupService,
            PlayitAgentService playitAgentService,
            PlayitApiClient playitApiClient,
            ModpackService modpackService,
            IDialogService dialogService,
            IAppNavigationService navigationService,
            IAppDispatcher dispatcher,
            IServiceProvider serviceProvider,
            ILogger<ServerSettingsViewModel> logger)
        {
            Metadata = metadata;
            _instanceManager = instanceManager;
            _serverConfigurationService = serverConfigurationService;
            _serverProcessManager = serverProcessManager;
            _dialogService = dialogService;
            _navigationService = navigationService;
            _dispatcher = dispatcher;
            _logger = logger;
            ServerDir = _instanceManager.GetInstancePath(metadata.Id) ?? throw new InvalidOperationException();
            _totalRamMb = MemoryHelper.GetTotalPhysicalMemoryMb();
            _instanceStateChangedHandler = (id, state) => { if (id == Metadata.Id) _dispatcher.Invoke(UpdateRunningState); };
            _serverProcessManager.OnInstanceStateChanged += _instanceStateChangedHandler;

            Addons = new ServerAddonsViewModel(metadata, ServerDir, modpackService, dialogService, navigationService, serviceProvider, () => IsRunning, MarkChanged);
            Backups = new ServerBackupsViewModel(metadata, ServerDir, backupService, instanceManager, dialogService, dispatcher, () => IsRunning, MarkChanged);
            World = new ServerWorldViewModel(ServerDir, worldManager, dialogService, dispatcher, () => IsRunning);

            SaveCommand = new RelayCommand(_ => SaveConfigurations(), _ => !IsTransientState);
            CancelCommand = new RelayCommand(async _ => await CancelAsync());
            BrowseIconCommand = new RelayCommand(async _ => await BrowseIconAsync());
            BrowseJavaCommand = new RelayCommand(async _ => await BrowseJavaAsync());
            ResolvePlayitCommand = new RelayCommand(_ => _ = ResolveTunnelAddressAsync(playitApiClient, playitAgentService));
            OpenPlayitDashboardCommand = new RelayCommand(_ => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://playit.gg/account/tunnels", UseShellExecute = true }));

            AddAdvancedPropertyCommand = new RelayCommand(_ => AddAdvancedProperty());
            DeleteAdvancedPropertyCommand = new RelayCommand(_ => DeleteSelectedAdvancedProperty(), _ => SelectedAdvancedProperty != null);

            LoadAll(playitApiClient, playitAgentService);
        }

        public void LoadAll(PlayitApiClient playitApiClient, PlayitAgentService playitAgentService)
        {
            IsLoading = true;
            UpdateRunningState();

            var configuration = _serverConfigurationService.Load(Metadata, ServerDir);
            MinRam = configuration.MinRamMb;
            MaxRam = configuration.MaxRamMb;
            JavaPath = configuration.CustomJavaPath;
            AdvancedJvmArgs = configuration.AdvancedJvmArgs;
            EnableAutoRestart = configuration.EnableAutoRestart;
            MaxAutoRestarts = configuration.MaxAutoRestarts.ToString();
            AutoRestartDelay = configuration.AutoRestartDelaySeconds.ToString();

            Motd = configuration.Motd;
            Seed = configuration.Seed;
            SpawnProtection = configuration.SpawnProtection;
            MaxPlayers = configuration.MaxPlayers;
            ServerPort = configuration.ServerPort;
            ServerIp = configuration.ServerIp;
            LevelType = configuration.LevelType;
            OnlineMode = configuration.OnlineMode;
            Pvp = configuration.Pvp;
            WhiteList = configuration.WhiteList;
            Gamemode = configuration.Gamemode;
            Difficulty = configuration.Difficulty;
            AllowBlock = configuration.AllowCommandBlock;
            AllowFlight = configuration.AllowFlight;
            AllowNether = configuration.AllowNether;
            LoadRawServerProperties();

            AdvancedProperties.Clear();
            foreach (var kvp in configuration.AllProperties) AdvancedProperties.Add(CreateAdvancedProperty(kvp.Key, kvp.Value));
            AdvancedProperties.CollectionChanged -= OnAdvancedPropertiesChanged;
            AdvancedProperties.CollectionChanged += OnAdvancedPropertiesChanged;

            LoadIcon();
            Addons.LoadAddons();
            Backups.LoadBackups();
            World.LoadWorldTab();

            _ = ResolveTunnelAddressAsync(playitApiClient, playitAgentService);

            IsLoading = false;
            HasUnsavedChanges = false;
        }

        private bool _isTransientState;
        public bool IsTransientState { get => _isTransientState; set => SetProperty(ref _isTransientState, value); }

        private void UpdateRunningState()
        {
            var proc = _serverProcessManager.GetProcess(Metadata.Id);
            IsRunning = _serverProcessManager.IsRunning(Metadata.Id);
            IsTransientState = proc != null && (proc.State == ServerState.Starting || proc.State == ServerState.Stopping);
            CommandManager.InvalidateRequerySuggested();
        }

        private void MarkChanged() { if (!IsLoading) HasUnsavedChanges = true; }

        private void CheckRamWarning() { if (_totalRamMb > 0) ShowRamWarning = MaxRam > (_totalRamMb * 0.8); }

        private void LoadIcon()
        {
            var iconPath = Path.Combine(ServerDir, "server-icon.png");
            if (File.Exists(iconPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(iconPath);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    ServerIcon = bmp;
                }
                catch { ServerIcon = null; }
            }
            else ServerIcon = null;
        }

        private async Task BrowseIconAsync()
        {
            var file = await _dialogService.OpenFileDialogAsync("Select Server Icon (64x64 PNG)", "PNG Files (*.png)|*.png");
            if (file != null)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit(); bmp.UriSource = new Uri(file); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit();
                    if (bmp.PixelWidth != 64 || bmp.PixelHeight != 64) { _dialogService.ShowMessage("Invalid Size", "Icon must be exactly 64x64 pixels.", DialogType.Warning); return; }
                    File.Copy(file, Path.Combine(ServerDir, "server-icon.png"), true);
                    ServerIcon = bmp;
                }
                catch (Exception ex) { _dialogService.ShowMessage("Error", ex.Message, DialogType.Error); }
            }
        }

        private async Task BrowseJavaAsync()
        {
            var file = await _dialogService.OpenFileDialogAsync("Select Java Executable", "java.exe|java.exe|Executables (*.exe)|*.exe");
            if (file != null) JavaPath = file;
        }

        private async Task ResolveTunnelAddressAsync(PlayitApiClient client, PlayitAgentService agent)
        {
            if (!int.TryParse(ServerPort, out int port)) { PlayitAddress = "Invalid port."; return; }
            PlayitAddress = "Resolving...";
            try
            {
                var result = await client.GetTunnelsAsync();
                if (!result.Success) { PlayitAddress = "API Error."; return; }
                var match = PlayitApiClient.FindTunnelForPort(result.Tunnels, port);
                PlayitAddress = match != null ? match.PublicAddress : "No tunnel found.";
            }
            catch { PlayitAddress = "Failed."; }
        }

        private void SaveConfigurations()
        {
            var configuration = new ServerConfiguration
            {
                MinRamMb = (int)MinRam, MaxRamMb = (int)MaxRam,
                CustomJavaPath = JavaPath, AdvancedJvmArgs = AdvancedJvmArgs,
                EnableAutoRestart = EnableAutoRestart,
                MaxAutoRestarts = int.TryParse(MaxAutoRestarts, out int mr) ? mr : Metadata.MaxAutoRestarts,
                AutoRestartDelaySeconds = int.TryParse(AutoRestartDelay, out int rd) ? rd : Metadata.AutoRestartDelaySeconds,
                BackupIntervalHours = Backups.BackupIntervalHours,
                MaxBackupsToKeep = Backups.MaxBackupsToKeep,
                Motd = Motd ?? "", Seed = Seed ?? "", SpawnProtection = SpawnProtection, MaxPlayers = MaxPlayers,
                ServerPort = ServerPort, ServerIp = ServerIp ?? "", LevelType = LevelType,
                OnlineMode = OnlineMode, Pvp = Pvp, WhiteList = WhiteList, Gamemode = Gamemode, Difficulty = Difficulty,
                AllowCommandBlock = AllowBlock, AllowFlight = AllowFlight, AllowNether = AllowNether
            };

            foreach (var item in AdvancedProperties)
            {
                if (!string.IsNullOrWhiteSpace(item.Key) && (!ServerConfigurationService.IsCoreProperty(item.Key) || item.IsDirty))
                    configuration.AdvancedProperties[item.Key] = item.Value;
            }

            _serverConfigurationService.Save(Metadata, ServerDir, configuration);
            if (_isRawServerPropertiesDirty)
            {
                _serverConfigurationService.SaveRawProperties(ServerDir, RawServerProperties);
                _isRawServerPropertiesDirty = false;
            }

            HasUnsavedChanges = false;
            _dialogService.ShowMessage("Saved", "Configurations saved successfully.");
        }

        private void LoadRawServerProperties()
        {
            _isLoadingRawServerProperties = true;
            RawServerProperties = _serverConfigurationService.LoadRawProperties(ServerDir);
            _isRawServerPropertiesDirty = false;
            _isLoadingRawServerProperties = false;
        }

        private void AddAdvancedProperty()
        {
            var property = new PropertyItem();
            AdvancedProperties.Add(property);
            SelectedAdvancedProperty = property;
            MarkChanged();
        }

        private void DeleteSelectedAdvancedProperty()
        {
            if (SelectedAdvancedProperty == null) return;
            AdvancedProperties.Remove(SelectedAdvancedProperty);
            SelectedAdvancedProperty = null;
            MarkChanged();
            CommandManager.InvalidateRequerySuggested();
        }

        private PropertyItem CreateAdvancedProperty(string key, string value)
        {
            var item = PropertyItem.CreateLoaded(key, value, ServerConfigurationService.IsCoreProperty(key));
            item.PropertyChanged += (s, e) => MarkChanged();
            return item;
        }

        private void OnAdvancedPropertiesChanged(object? sender, NotifyCollectionChangedEventArgs e) => MarkChanged();

        private async Task CancelAsync()
        {
            if (HasUnsavedChanges)
            {
                if (await _dialogService.ShowDialogAsync("Discard Changes", "You have unsaved changes. Discard them?", DialogType.Warning, false) != DialogResult.Yes) return;
            }
            if (!_navigationService.NavigateBack()) _navigationService.NavigateToDashboard();
        }

        public void Dispose() => _serverProcessManager.OnInstanceStateChanged -= _instanceStateChangedHandler;
    }
}
