using System;
using System.Windows.Controls;

namespace PocketMC.Desktop.Features.Shell.Interfaces
{
    /// <summary>
    /// Defines the capabilities of the application shell as a host for content navigation.
    /// </summary>
    public interface IShellHost
    {
        bool ShowShellPage(Type pageType, object? parameter = null);
        bool ShowDetailPage(Page page, string breadcrumbLabel);
        bool NavigateBackFromDetail(Type defaultShellPage);
        void SetNavigationLocked(bool isLocked);
        void CloseApp();
    }
}
