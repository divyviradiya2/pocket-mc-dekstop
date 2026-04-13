using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PocketMC.Desktop.Core.Interfaces;
using PocketMC.Desktop.Features.Shell.Interfaces;

namespace PocketMC.Desktop.Features.Tunnel
{
    public partial class PlayitGuidePage : Page
    {
        private readonly IAppNavigationService _navigationService;
        private readonly PlayitAgentService _agentService;
        private readonly bool _navigateToDashboardOnCompletion;

        public PlayitGuidePage(
            IAppNavigationService navigationService,
            PlayitAgentService agentService,
            string claimUrl,
            bool navigateToDashboardOnCompletion)
        {
            InitializeComponent();
            _navigationService = navigationService;
            _agentService = agentService;
            _navigateToDashboardOnCompletion = navigateToDashboardOnCompletion;

            // Open the claim URL in the user's default browser (NET-03)
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = claimUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not open browser: {ex.Message}";
            }

            // Subscribe to the tunnel-running event to auto-close (NET-04)
            _agentService.OnTunnelRunning += OnTunnelRunning;

            Unloaded += PlayitGuidePage_Unloaded;
        }

        private void OnTunnelRunning(object? sender, EventArgs e)
        {
            // Must dispatch to UI thread
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = "✓ Agent connected!";
                _agentService.OnTunnelRunning -= OnTunnelRunning;

                if (_navigateToDashboardOnCompletion)
                {
                    _navigationService.NavigateToDashboard();
                    return;
                }

                _navigationService.NavigateToTunnel();
            });
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            _agentService.OnTunnelRunning -= OnTunnelRunning;
            if (!_navigationService.NavigateBack())
            {
                _navigationService.NavigateToTunnel();
            }
        }

        private void PlayitGuidePage_Unloaded(object sender, RoutedEventArgs e)
        {
            _agentService.OnTunnelRunning -= OnTunnelRunning;
        }
    }
}
