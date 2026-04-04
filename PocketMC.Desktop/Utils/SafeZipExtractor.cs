using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace PocketMC.Desktop.Utils
{
    public static class SafeZipExtractor
    {
        public static Task ExtractAsync(string zipPath, string extractPath, Action<long, long>? onProgress = null)
        {
            return Task.Run(() =>
            {
                Directory.CreateDirectory(extractPath);

                string extractRoot = Path.GetFullPath(extractPath);
                if (!extractRoot.EndsWith(Path.DirectorySeparatorChar))
                {
                    extractRoot += Path.DirectorySeparatorChar;
                }

                using var archive = ZipFile.OpenRead(zipPath);
                long totalEntries = archive.Entries.Count;
                long entriesExtracted = 0;

                foreach (var entry in archive.Entries)
                {
                    string destinationPath = Path.GetFullPath(Path.Combine(extractRoot, entry.FullName));
                    if (!destinationPath.StartsWith(extractRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"ZIP entry '{entry.FullName}' would extract outside the destination directory.");
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destinationPath);
                    }
                    else
                    {
                        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(destinationDirectory))
                        {
                            Directory.CreateDirectory(destinationDirectory);
                        }

                        entry.ExtractToFile(destinationPath, overwrite: true);
                    }

                    entriesExtracted++;
                    onProgress?.Invoke(entriesExtracted, totalEntries);
                }
            });
        }
    }
}
