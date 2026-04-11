using System.Windows;
using System.Windows.Media;
using PocketMC.Desktop.Core.Mvvm;

namespace PocketMC.Desktop.ViewModels
{
    public class ShellViewModel : ViewModelBase
    {
        private string? _breadcrumbCurrentText;
        private bool _isBreadcrumbVisible;
        private string? _titleBarTitle;
        private string? _titleBarStatusText;
        private Brush? _titleBarStatusBrush;
        private bool _isTitleBarContextVisible;
        private string? _globalHealthStatusText;
        private Brush? _globalHealthStatusBrush;
        private bool _isNavigationLocked;
        private bool _isPaneVisible = true;
        private bool _isPaneToggleVisible = true;

        public string? BreadcrumbCurrentText
        {
            get => _breadcrumbCurrentText;
            set => SetProperty(ref _breadcrumbCurrentText, value);
        }

        public bool IsBreadcrumbVisible
        {
            get => _isBreadcrumbVisible;
            set => SetProperty(ref _isBreadcrumbVisible, value);
        }

        public string? TitleBarTitle
        {
            get => _titleBarTitle;
            set => SetProperty(ref _titleBarTitle, value);
        }

        public string? TitleBarStatusText
        {
            get => _titleBarStatusText;
            set => SetProperty(ref _titleBarStatusText, value);
        }

        public Brush? TitleBarStatusBrush
        {
            get => _titleBarStatusBrush;
            set => SetProperty(ref _titleBarStatusBrush, value);
        }

        public bool IsTitleBarContextVisible
        {
            get => _isTitleBarContextVisible;
            set => SetProperty(ref _isTitleBarContextVisible, value);
        }

        public string? GlobalHealthStatusText
        {
            get => _globalHealthStatusText;
            set => SetProperty(ref _globalHealthStatusText, value);
        }

        public Brush? GlobalHealthStatusBrush
        {
            get => _globalHealthStatusBrush;
            set => SetProperty(ref _globalHealthStatusBrush, value);
        }

        public bool IsNavigationLocked
        {
            get => _isNavigationLocked;
            set => SetProperty(ref _isNavigationLocked, value);
        }

        public bool IsPaneVisible
        {
            get => _isPaneVisible;
            set => SetProperty(ref _isPaneVisible, value);
        }

        public bool IsPaneToggleVisible
        {
            get => _isPaneToggleVisible;
            set => SetProperty(ref _isPaneToggleVisible, value);
        }

        public Visibility BreadcrumbVisibility => IsBreadcrumbVisible ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TitleBarContextVisibility => IsTitleBarContextVisible ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GlobalHealthVisibility => !IsNavigationLocked ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NavigationVisibility => IsPaneVisible ? Visibility.Visible : Visibility.Collapsed;

        // Helper to notify all visibility properties when related booleans change
        protected override void OnPropertyChanged(string? propertyName = null)
        {
            base.OnPropertyChanged(propertyName);

            if (propertyName == nameof(IsBreadcrumbVisible))
                OnPropertyChanged(nameof(BreadcrumbVisibility));
            if (propertyName == nameof(IsTitleBarContextVisible))
                OnPropertyChanged(nameof(TitleBarContextVisibility));
            if (propertyName == nameof(IsNavigationLocked))
                OnPropertyChanged(nameof(GlobalHealthVisibility));
            if (propertyName == nameof(IsPaneVisible))
                OnPropertyChanged(nameof(NavigationVisibility));
        }
    }
}
