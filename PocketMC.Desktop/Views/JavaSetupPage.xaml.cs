using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PocketMC.Desktop.Services;

namespace PocketMC.Desktop.Views
{
    /// <summary>
    /// View-model for a single Java runtime row in the management page.
    /// </summary>
    public class JavaRuntimeEntry : INotifyPropertyChanged
    {
        public int Version { get; set; }
        public string VersionLabel => Version > 0 ? $"{Version}" : "?";
        public string DisplayName { get; set; } = "";
        public bool IsInstalled { get; set; }
        public bool IsCustom { get; set; }
        public string? Path { get; set; }

        // ── Badge (subtle semi-transparent fills) ──
        public string BadgeText => IsCustom ? "CUSTOM" : IsInstalled ? "READY" : "MISSING";
        public Visibility BadgeVisibility => Visibility.Visible;
        public SolidColorBrush BadgeBackground => IsCustom
            ? new SolidColorBrush(Color.FromArgb(0x30, 0xA0, 0x8C, 0xFF))   // soft violet tint
            : IsInstalled
                ? new SolidColorBrush(Color.FromArgb(0x30, 0x60, 0xCD, 0xFF))  // soft blue tint
                : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0x99, 0x66)); // soft amber tint
        public SolidColorBrush BadgeForeground => IsCustom
            ? new SolidColorBrush(Color.FromRgb(0xC0, 0xB4, 0xFF))  // light violet
            : IsInstalled
                ? new SolidColorBrush(Color.FromRgb(0x78, 0xB8, 0xFF))  // light blue
                : new SolidColorBrush(Color.FromRgb(0xFF, 0xBB, 0x88)); // light amber

        // ── Version tile (left icon) ──
        public SolidColorBrush StatusBackground => IsInstalled
            ? new SolidColorBrush(Color.FromArgb(0x25, 0x60, 0xCD, 0xFF))  // subtle blue glass
            : new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)); // faint white glass

        // ── Status icon (Segoe Fluent glyph) ──
        // \uE73E = Checkmark, \uE711 = DownArrow (needs download)
        public string StatusIcon => IsInstalled ? "\uE73E" : "\uE896";
        public SolidColorBrush StatusIconForeground => IsInstalled
            ? new SolidColorBrush(Color.FromRgb(0x78, 0xB8, 0xFF))  // calm blue
            : new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)); // dim white

        // ── Detail line ──
        private string _detailText = "";
        public string DetailText
        {
            get => _detailText;
            set { _detailText = value; OnPropertyChanged(nameof(DetailText)); }
        }

        // ── Progress (download) ──
        private double _progress;
        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(nameof(Progress)); }
        }

        private Visibility _progressVisibility = Visibility.Collapsed;
        public Visibility ProgressVisibility
        {
            get => _progressVisibility;
            set { _progressVisibility = value; OnPropertyChanged(nameof(ProgressVisibility)); }
        }

        // ── Delete button ──
        public Visibility DeleteVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;

        public void Refresh()
        {
            OnPropertyChanged(nameof(BadgeText));
            OnPropertyChanged(nameof(BadgeBackground));
            OnPropertyChanged(nameof(BadgeForeground));
            OnPropertyChanged(nameof(StatusIcon));
            OnPropertyChanged(nameof(StatusIconForeground));
            OnPropertyChanged(nameof(StatusBackground));
            OnPropertyChanged(nameof(DeleteVisibility));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string prop) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    public partial class JavaSetupPage : Page
    {
        private readonly ApplicationState _applicationState;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<JavaSetupPage> _logger;
        public ObservableCollection<JavaRuntimeEntry> Runtimes { get; } = new();

        public JavaSetupPage(
            ApplicationState applicationState,
            IServiceProvider serviceProvider,
            ILogger<JavaSetupPage> logger)
        {
            InitializeComponent();
            _applicationState = applicationState;
            _serviceProvider = serviceProvider;
            _logger = logger;
            RuntimeList.ItemsSource = Runtimes;
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            ScanRuntimes();

            // Auto-download missing runtimes + playit on first load
            bool anyMissing = Runtimes.Any(r => !r.IsInstalled);
            if (anyMissing)
            {
                await DownloadMissingRuntimesAsync();
            }

            await EnsurePlayitReadyAsync();
        }

        /// <summary>
        /// Scans the runtime directory and builds the card list.
        /// </summary>
        private void ScanRuntimes()
        {
            Runtimes.Clear();
            string appRoot = _applicationState.GetRequiredAppRootPath();
            var requiredVersions = JavaRuntimeResolver.GetBundledJavaVersions();

            foreach (var version in requiredVersions)
            {
                string runtimeDir = System.IO.Path.Combine(appRoot, "runtime", $"java{version}");
                string javaExe = System.IO.Path.Combine(runtimeDir, "bin", "java.exe");
                bool installed = File.Exists(javaExe) && new FileInfo(javaExe).Length > 20000;

                string detail;
                if (installed)
                {
                    double sizeMb = GetDirectorySizeMb(runtimeDir);
                    detail = $"{runtimeDir}  •  {sizeMb:F1} MB";
                }
                else
                {
                    detail = "Not downloaded — click Download Missing to install";
                }

                string mcRange = version switch
                {
                    8 => "MC 1.0 – 1.16.4",
                    11 => "MC 1.16.5 – 1.17.1",
                    17 => "MC 1.18 – 1.20.4",
                    21 => "MC 1.20.5 – 1.21.1",
                    25 => "MC 1.21.2+",
                    _ => ""
                };

                Runtimes.Add(new JavaRuntimeEntry
                {
                    Version = version,
                    DisplayName = $"Java {version} Runtime  ({mcRange})",
                    IsInstalled = installed,
                    IsCustom = false,
                    Path = runtimeDir,
                    DetailText = detail
                });
            }

            // Scan for custom runtimes (folders not matching bundled versions)
            string runtimeRoot = System.IO.Path.Combine(appRoot, "runtime");
            if (Directory.Exists(runtimeRoot))
            {
                foreach (var dir in Directory.GetDirectories(runtimeRoot))
                {
                    string folderName = System.IO.Path.GetFileName(dir);
                    // Skip known bundled folders
                    if (requiredVersions.Any(v => folderName == $"java{v}"))
                        continue;

                    string javaExe = System.IO.Path.Combine(dir, "bin", "java.exe");
                    bool exists = File.Exists(javaExe);

                    Runtimes.Add(new JavaRuntimeEntry
                    {
                        Version = 0,
                        DisplayName = folderName,
                        IsInstalled = exists,
                        IsCustom = true,
                        Path = dir,
                        DetailText = exists
                            ? $"{dir}  •  {GetDirectorySizeMb(dir):F1} MB"
                            : $"{dir}  •  java.exe not found"
                    });
                }
            }

            int installedCount = Runtimes.Count(r => r.IsInstalled);
            int total = Runtimes.Count;
            TxtGlobalStatus.Text = $"{installedCount} of {total} runtimes installed";
        }

        // ──────────────────────────────────────────────
        //  Download Missing
        // ──────────────────────────────────────────────

        private async void BtnDownloadMissing_Click(object sender, RoutedEventArgs e)
        {
            await DownloadMissingRuntimesAsync();
        }

        private async Task DownloadMissingRuntimesAsync()
        {
            var missing = Runtimes.Where(r => !r.IsInstalled && !r.IsCustom).ToList();
            if (missing.Count == 0)
            {
                TxtGlobalStatus.Text = "All runtimes are installed ✓";
                return;
            }

            BtnDownloadMissing.IsEnabled = false;
            TxtGlobalStatus.Text = $"Downloading {missing.Count} runtime(s)...";

            try
            {
                foreach (var entry in missing)
                {
                    entry.ProgressVisibility = Visibility.Visible;
                    entry.DetailText = "Downloading...";
                    entry.Progress = 0;

                    await AcquireJreAsync(entry);

                    entry.IsInstalled = true;
                    entry.ProgressVisibility = Visibility.Collapsed;
                    entry.DetailText = $"{entry.Path}  •  {GetDirectorySizeMb(entry.Path!):F1} MB";
                    entry.Refresh();
                }

                TxtGlobalStatus.Text = "All runtimes are installed ✓";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Runtime download failed.");
                TxtGlobalStatus.Text = $"Download error: {ex.Message}";
                TxtGlobalStatus.Foreground = Brushes.OrangeRed;
            }
            finally
            {
                BtnDownloadMissing.IsEnabled = true;
            }
        }

        private async Task AcquireJreAsync(JavaRuntimeEntry entry)
        {
            using var httpClient = new HttpClient();
            string apiUrl = $"https://api.adoptium.net/v3/assets/latest/{entry.Version}/hotspot?os=windows&architecture=x64&image_type=jre";
            string response = await httpClient.GetStringAsync(apiUrl);

            var array = JsonNode.Parse(response)?.AsArray();
            var link = array?[0]?["binary"]?["package"]?["link"]?.ToString();

            if (string.IsNullOrEmpty(link))
                throw new Exception($"Could not find download link for Java {entry.Version}");

            var downloader = new DownloaderService();
            string appRoot = _applicationState.GetRequiredAppRootPath();
            string tempZipPath = System.IO.Path.Combine(appRoot, "runtime", $"temp_java{entry.Version}.zip");
            string extractPath = System.IO.Path.Combine(appRoot, "runtime", $"java{entry.Version}_ext");
            string finalPath = System.IO.Path.Combine(appRoot, "runtime", $"java{entry.Version}");

            entry.DetailText = "Downloading...";
            var downloadProgress = new Progress<DownloadProgress>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    entry.Progress = p.Percentage;
                    entry.DetailText = $"Downloading — {p.BytesRead / 1024 / 1024} MB / {p.TotalBytes / 1024 / 1024} MB";
                });
            });

            await downloader.DownloadFileAsync(link, tempZipPath, downloadProgress);

            entry.DetailText = "Extracting...";
            entry.Progress = 0;
            var extractProgress = new Progress<DownloadProgress>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    entry.Progress = p.Percentage;
                    entry.DetailText = $"Extracting — {p.BytesRead} / {p.TotalBytes} files";
                });
            });

            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            await downloader.ExtractZipAsync(tempZipPath, extractPath, extractProgress);

            var subDirs = Directory.GetDirectories(extractPath);
            if (subDirs.Length == 1)
            {
                if (Directory.Exists(finalPath)) Directory.Delete(finalPath, true);
                Directory.Move(subDirs[0], finalPath);
            }
            else
            {
                throw new Exception($"Unexpected zip structure for Java {entry.Version}");
            }

            Directory.Delete(extractPath, true);
            File.Delete(tempZipPath);

            entry.Path = finalPath;
        }

        // ──────────────────────────────────────────────
        //  Add Custom Runtime
        // ──────────────────────────────────────────────

        private void BtnAddCustom_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select a Java runtime folder (must contain bin/java.exe)"
            };

            if (dialog.ShowDialog() == true)
            {
                string selectedPath = dialog.FolderName;
                string javaExe = System.IO.Path.Combine(selectedPath, "bin", "java.exe");

                if (!File.Exists(javaExe))
                {
                    System.Windows.MessageBox.Show(
                        "Selected folder does not contain bin/java.exe.\nPlease select the JRE/JDK root folder.",
                        "Invalid Runtime",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Copy to runtime directory with a custom name
                string appRoot = _applicationState.GetRequiredAppRootPath();
                string folderName = System.IO.Path.GetFileName(selectedPath);
                string destPath = System.IO.Path.Combine(appRoot, "runtime", $"custom-{folderName}");

                try
                {
                    if (Directory.Exists(destPath))
                    {
                        System.Windows.MessageBox.Show(
                            $"A runtime named 'custom-{folderName}' already exists.",
                            "Duplicate",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }

                    CopyDirectory(selectedPath, destPath);
                    ScanRuntimes();
                    TxtGlobalStatus.Text = $"Added custom runtime: {folderName}";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to add custom runtime.");
                    System.Windows.MessageBox.Show(
                        $"Failed to add runtime: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        // ──────────────────────────────────────────────
        //  Delete Runtime
        // ──────────────────────────────────────────────

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is JavaRuntimeEntry entry && entry.Path != null)
            {
                var result = System.Windows.MessageBox.Show(
                    $"Delete {entry.DisplayName}?\n\nPath: {entry.Path}\n\nYou can re-download bundled runtimes at any time.",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        if (Directory.Exists(entry.Path))
                            Directory.Delete(entry.Path, true);

                        ScanRuntimes();
                        TxtGlobalStatus.Text = $"Deleted {entry.DisplayName}";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete runtime at {Path}.", entry.Path);
                        System.Windows.MessageBox.Show(
                            $"Failed to delete: {ex.Message}",
                            "Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
            }
        }

        // ──────────────────────────────────────────────
        //  Refresh
        // ──────────────────────────────────────────────

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            ScanRuntimes();
        }

        // ──────────────────────────────────────────────
        //  Helpers
        // ──────────────────────────────────────────────

        private async Task EnsurePlayitReadyAsync()
        {
            try
            {
                var downloader = new DownloaderService();
                await downloader.EnsurePlayitDownloadedAsync(_applicationState.GetRequiredAppRootPath());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Playit download failed but the rest of the app can continue.");
            }
        }

        private static double GetDirectorySizeMb(string path)
        {
            if (!Directory.Exists(path)) return 0;
            try
            {
                long bytes = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);
                return bytes / (1024.0 * 1024.0);
            }
            catch { return 0; }
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destFile = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir);
            }
        }
    }
}
