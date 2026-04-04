using System;
using System.IO;
using System.Threading.Tasks;

namespace PocketMC.Desktop.Utils
{
    /// <summary>
    /// Production-grade recursive file operations that safely handle
    /// cross-drive moves, read-only attributes, and async offloading.
    /// </summary>
    public static class FileUtils
    {
        /// <summary>
        /// Recursively copies a directory tree. Safe across drive boundaries
        /// (unlike Directory.Move which fails cross-volume).
        /// </summary>
        public static async Task CopyDirectoryAsync(string sourceDir, string destDir)
        {
            await Task.Run(() =>
            {
                CopyDirectoryRecursive(sourceDir, destDir);
            });
        }

        private static void CopyDirectoryRecursive(string source, string dest)
        {
            Directory.CreateDirectory(dest);

            foreach (var file in Directory.GetFiles(source))
            {
                var destFile = Path.Combine(dest, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                var destSubDir = Path.Combine(dest, Path.GetFileName(dir));
                CopyDirectoryRecursive(dir, destSubDir);
            }
        }

        public static async Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite = true)
        {
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            var fileMode = overwrite ? FileMode.Create : FileMode.CreateNew;
            await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var destinationStream = new FileStream(destinationPath, fileMode, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await sourceStream.CopyToAsync(destinationStream);
        }

        public static Task DeleteFileAsync(string filePath)
        {
            return Task.Run(() =>
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            });
        }

        /// <summary>
        /// Forcefully deletes a directory, stripping read-only attributes first
        /// to avoid UnauthorizedAccessException on protected files.
        /// </summary>
        public static async Task CleanDirectoryAsync(string dirPath)
        {
            if (!Directory.Exists(dirPath)) return;

            await Task.Run(() =>
            {
                StripReadOnly(dirPath);
                Directory.Delete(dirPath, recursive: true);
            });
        }

        private static void StripReadOnly(string dirPath)
        {
            foreach (var file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
        }

        /// <summary>
        /// Returns folder size in MB for display purposes.
        /// </summary>
        public static double GetDirectorySizeMb(string dirPath)
        {
            if (!Directory.Exists(dirPath)) return 0;
            long totalBytes = 0;
            foreach (var file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories))
            {
                try { totalBytes += new FileInfo(file).Length; }
                catch { /* skip locked files */ }
            }
            return Math.Round(totalBytes / (1024.0 * 1024.0), 1);
        }
    }
}
