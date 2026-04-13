namespace PocketMC.Desktop.Features.Shell.Interfaces
{
    public interface IStartupShellHost
    {
        void ShowRootDirectorySetup();
        void CompleteRootDirectorySetup();
        void RequestMicaUpdate();
        bool NavigateToDashboard();
        bool NavigateToTunnel();
        bool NavigateToPlayitGuide(string claimUrl, bool navigateToDashboardOnCompletion);
        void ShowError(string title, string message);
        void ShutdownApplication();
    }
}
