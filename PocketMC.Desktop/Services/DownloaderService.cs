using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using PocketMC.Desktop.Utils;

namespace PocketMC.Desktop.Services
{
    public class DownloaderService
    {
        private readonly HttpClient _httpClient;

        public DownloaderService()
        {
            _httpClient = new HttpClient();
            // Standard headers that some secure APIs require
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "PocketMC-Desktop");
        }

        public async Task DownloadFileAsync(string url, string destinationPath, IProgress<DownloadProgress>? progress = null)
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                totalRead += bytesRead;

                progress?.Report(new DownloadProgress
                {
                    BytesRead = totalRead,
                    TotalBytes = totalBytes
                });
            }
        }

        /// <summary>
        /// Downloads playit.exe into <appRoot>/tunnel/playit.exe if not already present.
        /// Called during app startup alongside JRE downloads (NET-01).
        /// </summary>
        public async Task EnsurePlayitDownloadedAsync(string appRootPath, IProgress<DownloadProgress>? progress = null)
        {
            const string PlayitDownloadUrl = "https://github.com/playit-cloud/playit-agent/releases/latest/download/playit-windows-x86_64.exe";

            string tunnelDir = Path.Combine(appRootPath, "tunnel");
            string playitPath = Path.Combine(tunnelDir, "playit.exe");

            if (File.Exists(playitPath))
                return; // Already downloaded — skip (NET-01: version check on every startup)

            Directory.CreateDirectory(tunnelDir);
            await DownloadFileAsync(PlayitDownloadUrl, playitPath, progress);
        }

        public Task ExtractZipAsync(string zipPath, string extractPath, IProgress<DownloadProgress>? progress = null)
        {
            return SafeZipExtractor.ExtractAsync(
                zipPath,
                extractPath,
                (entriesExtracted, totalEntries) =>
                {
                    progress?.Report(new DownloadProgress
                    {
                        BytesRead = entriesExtracted,
                        TotalBytes = totalEntries
                    });
                });
        }
    }
}
