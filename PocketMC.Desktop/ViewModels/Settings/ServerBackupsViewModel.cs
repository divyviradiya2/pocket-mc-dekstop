using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using PocketMC.Desktop.Core.Interfaces;
using PocketMC.Desktop.Core.Mvvm;
using PocketMC.Desktop.Models;
using PocketMC.Desktop.Services;

namespace PocketMC.Desktop.ViewModels.Settings
{
    public class ServerBackupsViewModel : ViewModelBase
    {
        private readonly InstanceMetadata _metadata;
        private readonly string _serverDir;
        private readonly BackupService _backupService;
        private readonly InstanceManager _instanceManager;
        private readonly IDialogService _dialogService;
        private readonly IAppDispatcher _dispatcher;
        private readonly Func<bool> _isRunningCheck;
        private readonly Action _onBackupChanged;

        private int _backupIntervalHours;
        public int BackupIntervalHours { get => _backupIntervalHours; set { if (SetProperty(ref _backupIntervalHours, value)) SaveBackupSettings(); } }

        private int _maxBackupsToKeep;
        public int MaxBackupsToKeep { get => _maxBackupsToKeep; set { if (SetProperty(ref _maxBackupsToKeep, value)) SaveBackupSettings(); } }

        private string _backupProgressText = "";
        public string BackupProgressText { get => _backupProgressText; set => SetProperty(ref _backupProgressText, value); }
        
        private bool _showBackupProgress;
        public bool ShowBackupProgress { get => _showBackupProgress; set => SetProperty(ref _showBackupProgress, value); }

        public ObservableCollection<BackupItemViewModel> Backups { get; } = new();

        public ICommand CreateBackupCommand { get; }
        public ICommand RestoreBackupCommand { get; }
        public ICommand DeleteBackupCommand { get; }

        public ServerBackupsViewModel(
            InstanceMetadata metadata,
            string serverDir,
            BackupService backupService,
            InstanceManager instanceManager,
            IDialogService dialogService,
            IAppDispatcher dispatcher,
            Func<bool> isRunningCheck,
            Action onBackupChanged)
        {
            _metadata = metadata;
            _serverDir = serverDir;
            _backupService = backupService;
            _instanceManager = instanceManager;
            _dialogService = dialogService;
            _dispatcher = dispatcher;
            _isRunningCheck = isRunningCheck;
            _onBackupChanged = onBackupChanged;

            _backupIntervalHours = _metadata.BackupIntervalHours;
            _maxBackupsToKeep = _metadata.MaxBackupsToKeep;

            CreateBackupCommand = new RelayCommand(async _ => await CreateBackupAsync());
            RestoreBackupCommand = new RelayCommand(async p => await RestoreBackupAsync(p as string), _ => !_isRunningCheck());
            DeleteBackupCommand = new RelayCommand(async p => await DeleteBackupAsync(p as string));
        }

        public void LoadBackups()
        {
            Backups.Clear();
            var dir = Path.Combine(_serverDir, "backups");
            if (!Directory.Exists(dir)) return;
            foreach (var file in new DirectoryInfo(dir).GetFiles("world-*.zip").OrderByDescending(f => f.CreationTime))
            {
                Backups.Add(new BackupItemViewModel { Name = file.Name, Path = file.FullName, SizeMb = file.Length / (1024.0 * 1024.0), Created = file.CreationTime });
            }
        }

        private async Task CreateBackupAsync()
        {
            ShowBackupProgress = true;
            try
            {
                await _backupService.RunBackupAsync(_metadata, _serverDir, p => _dispatcher.Invoke(() => BackupProgressText = p));
                LoadBackups();
            }
            catch (Exception ex) { _dialogService.ShowMessage("Error", ex.Message, DialogType.Error); }
            finally { ShowBackupProgress = false; }
        }

        private async Task RestoreBackupAsync(string? path)
        {
            if (path != null && await _dialogService.ShowDialogAsync("Confirm Restore", "Restore this backup? Current world will be REPLACED.", DialogType.Warning) == DialogResult.Yes)
            {
                ShowBackupProgress = true;
                try
                {
                    await _backupService.RestoreBackupAsync(path, _serverDir, p => _dispatcher.Invoke(() => BackupProgressText = p));
                    _dialogService.ShowMessage("Success", "World restored.");
                    _onBackupChanged();
                }
                catch (Exception ex) { _dialogService.ShowMessage("Error", ex.Message, DialogType.Error); }
                finally { ShowBackupProgress = false; }
            }
        }

        private async Task DeleteBackupAsync(string? path)
        {
            if (path != null && await _dialogService.ShowDialogAsync("Confirm", $"Delete backup {Path.GetFileName(path)}?", DialogType.Question) == DialogResult.Yes)
            {
                File.Delete(path);
                LoadBackups();
            }
        }

        private void SaveBackupSettings()
        {
            _metadata.BackupIntervalHours = BackupIntervalHours;
            _metadata.MaxBackupsToKeep = MaxBackupsToKeep;
            _instanceManager.SaveMetadata(_metadata, _serverDir);
        }
    }

    public class BackupItemViewModel
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public double SizeMb { get; set; }
        public DateTime Created { get; set; }
    }
}
