using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace PocketMC.Desktop.Services;

/// <summary>
/// Simulates a Mica-like background for Windows 10 where the native
/// Mica backdrop is unavailable. Captures the desktop wallpaper, applies
/// heavy Gaussian blur and a dark tint — visually identical to Win11 Mica.
/// </summary>
public static class WallpaperMicaService
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SystemParametersInfo(
        int uAction, int uParam, System.Text.StringBuilder lpvParam, int fuWinIni);

    private const int SPI_GETDESKWALLPAPER = 0x0073;

    public static bool IsWindows11OrLater
        => Environment.OSVersion.Version.Build >= 22000;

    public static string GetWallpaperPath()
    {
        var sb = new System.Text.StringBuilder(260);
        SystemParametersInfo(SPI_GETDESKWALLPAPER, sb.Capacity, sb, 0);
        return sb.ToString();
    }

    /// <summary>
    /// Creates a blurred + tinted bitmap that imitates Mica.
    /// Returns null on any failure so callers can fall back to a solid color.
    /// </summary>
    public static BitmapSource? CreateMicaBackground(
        int targetWidth,
        int targetHeight,
        double blurRadius = 80,
        double tintOpacity = 0.75,
        Color? tintColor = null)
    {
        var tint = tintColor ?? Color.FromRgb(32, 32, 32);

        try
        {
            var wallpaperPath = GetWallpaperPath();
            if (string.IsNullOrEmpty(wallpaperPath) || !File.Exists(wallpaperPath))
                return null;

            // Load wallpaper at reduced resolution for speed
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(wallpaperPath);
            bitmap.DecodePixelWidth = targetWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            // Draw wallpaper + dark tint overlay
            var visual = new DrawingVisual();
            using (var ctx = visual.RenderOpen())
            {
                var rect = new Rect(0, 0, targetWidth, targetHeight);
                ctx.DrawImage(bitmap, rect);
                ctx.DrawRectangle(
                    new SolidColorBrush(Color.FromArgb(
                        (byte)(tintOpacity * 255),
                        tint.R, tint.G, tint.B)),
                    null, rect);
            }

            // Apply Gaussian blur
            visual.Effect = new BlurEffect
            {
                Radius = blurRadius,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Performance
            };

            var rtb = new RenderTargetBitmap(
                targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();

            return rtb;
        }
        catch
        {
            return null;
        }
    }
}
