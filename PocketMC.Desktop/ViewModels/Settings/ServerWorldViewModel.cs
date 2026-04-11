using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using PocketMC.Desktop.Core.Interfaces;
using PocketMC.Desktop.Core.Mvvm;
using PocketMC.Desktop.Services;

namespace PocketMC.Desktop.ViewModels.Settings
{
    public class ServerWorldViewModel : ViewModelBase
    {
        private readonly string _serverDir;
        private readonly WorldManager _worldManager;
        private readonly IDialogService _dialogService;
        private readonly IAppDispatcher _dispatcher;
        private readonly Func<bool> _isRunningCheck;

        private string _worldStatusText = "Checking world...";
        public string WorldStatusText { get => _worldStatusText; set => SetProperty(ref _worldStatusText, value); }

        private string _worldSizeText = "";
        public string WorldSizeText { get => _worldSizeText; set => SetProperty(ref _worldSizeText, value); }

        private string _worldProgressText = "";
        public string WorldProgressText { get => _worldProgressText; set => SetProperty(ref _worldProgressText, value); }

        private bool _showWorldProgress;
        public bool ShowWorldProgress { get => _showWorldProgress; set => SetProperty(ref _showWorldProgress, value); }

        public ICommand UploadWorldCommand { get; }
        public ICommand DeleteWorldCommand { get; }

        public ServerWorldViewModel(
            string serverDir,
            WorldManager worldManager,
            IDialogService dialogService,
            IAppDispatcher dispatcher,
            Func<bool> isRunningCheck)
        {
            _serverDir = serverDir;
            _worldManager = worldManager;
            _dialogService = dialogService;
            _dispatcher = dispatcher;
            _isRunningCheck = isRunningCheck;

            UploadWorldCommand = new RelayCommand(async _ => await UploadWorldAsync(), _ => !_isRunningCheck());
            DeleteWorldCommand = new RelayCommand(async _ => await DeleteWorldAsync(), _ => !_isRunningCheck());
        }

        public void LoadWorldTab()
        {
            var worldDir = Path.Combine(_serverDir, "world");
            if (Directory.Exists(worldDir))
            {
                WorldStatusText = "✅ World folder exists";
                WorldSizeText = $"Size: {PocketMC.Desktop.Utils.FileUtils.GetDirectorySizeMb(worldDir)} MB";
            }
            else
            {
                WorldStatusText = "No world folder found (will be generated)";
                WorldSizeText = "";
            }
        }

        private async Task UploadWorldAsync()
        {
            var file = await _dialogService.OpenFileDialogAsync("Select World ZIP", "ZIP Files (*.zip)|*.zip");
            if (file != null)
            {
                ShowWorldProgress = true;
                try
                {
                    await _worldManager.ImportWorldZipAsync(file, Path.Combine(_serverDir, "world"), p => _dispatcher.Invoke(() => WorldProgressText = p));
                    LoadWorldTab();
                }
                catch (Exception ex) { _dialogService.ShowMessage("Error", ex.Message, DialogType.Error); }
                finally { ShowWorldProgress = false; }
            }
        }

        private async Task DeleteWorldAsync()
        {
            var worldDir = Path.Combine(_serverDir, "world");
            if (!Directory.Exists(worldDir)) return;
            if (await _dialogService.ShowDialogAsync("Confirm", "Delete current world? Cannot be undone.", DialogType.Warning) == DialogResult.Yes)
            {
                try { await PocketMC.Desktop.Utils.FileUtils.CleanDirectoryAsync(worldDir); LoadWorldTab(); }
                catch (Exception ex) { _dialogService.ShowMessage("Error", ex.Message, DialogType.Error); }
            }
        }
    }
}
