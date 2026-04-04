using System.IO;
using PocketMC.Desktop.Models;

namespace PocketMC.Desktop.Services;

public class ApplicationState
{
    public AppSettings Settings { get; private set; } = new();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Settings.AppRootPath);

    public void ApplySettings(AppSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public string GetRequiredAppRootPath()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("PocketMC has not been configured with an application root path yet.");
        }

        return Settings.AppRootPath!;
    }

    public string GetServersDirectory() => Path.Combine(GetRequiredAppRootPath(), "servers");

    public string GetRuntimeDirectory() => Path.Combine(GetRequiredAppRootPath(), "runtime");

    public string GetPlayitExecutablePath() => Path.Combine(GetRequiredAppRootPath(), "tunnel", "playit.exe");
}
