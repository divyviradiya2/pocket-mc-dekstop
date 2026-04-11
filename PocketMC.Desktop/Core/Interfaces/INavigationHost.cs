using System;
using System.Windows.Controls;

namespace PocketMC.Desktop.Core.Interfaces;

/// <summary>
/// A host that can handle navigation requests from the application navigation service.
/// Usually implemented by the Shell Window.
/// </summary>
public interface INavigationHost
{
    bool NavigateToDashboard();
    bool NavigateToShellPage(Type pageType, object? parameter = null);
    bool NavigateToDetailPage(Page page, string breadcrumbLabel);
    void UpdateBreadcrumb(string? label);
    void SetNavigationLocked(bool isLocked);
}
