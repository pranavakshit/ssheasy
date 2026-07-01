using System;
using System.Threading.Tasks;
using System.Windows;
using SSHEasyApp.Services;

namespace SSHEasyApp.Dialogs;

public partial class RdpWindow : Window
{
    private readonly SshService _sshService;
    private readonly uint _localPort = 33890;

    public RdpWindow(SshService sshService)
    {
        InitializeComponent();
        _sshService = sshService;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 1. Ensure XRDP is started on the VM
            LoadingText.Text = "Checking XRDP service...";
            string status = "";
            try { status = await _sshService.RunCommandAsync("systemctl is-active xrdp"); } catch { }
            
            if (!status.Trim().Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                LoadingText.Text = "Installing/Starting XRDP...\nPlease check the main terminal if a sudo password is required.";
                _sshService.SendText("sudo apt-get update && sudo DEBIAN_FRONTEND=noninteractive apt-get install -y xrdp && sudo systemctl start xrdp\n");
                
                // Wait until the service becomes active
                int retries = 60; // Up to 3 minutes
                while (retries > 0)
                {
                    await Task.Delay(3000);
                    try 
                    { 
                        string check = await _sshService.RunCommandAsync("systemctl is-active xrdp"); 
                        if (check.Trim().Equals("active", StringComparison.OrdinalIgnoreCase)) break;
                    } 
                    catch { }
                    retries--;
                }
                
                if (retries == 0)
                {
                    throw new Exception("XRDP failed to start within the expected time limit.");
                }
            }
            
            // 2. Establish SSH Port Forwarding
            LoadingText.Text = "Establishing secure SSH tunnel...";
            _sshService.StartRdpTunnel(_localPort, "127.0.0.1", 3389);
            
            // Give the tunnel a moment to settle
            await Task.Delay(1000);
            
            // 3. Switch UI and launch MSTSC
            LoadingPanel.Visibility = Visibility.Collapsed;
            RdpHost.Visibility = Visibility.Visible;
            
            RdpHost.ProcessExited += () =>
            {
                // Auto-close window if MSTSC closes
                Close();
            };
            
            RdpHost.StartProcess($"/v:127.0.0.1:{_localPort}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to initialize Remote Desktop:\n{ex.Message}", "RDP Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void StopRdp_Click(object sender, RoutedEventArgs e)
    {
        Close(); // This will trigger OnClosed which cleans up
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // 1. Stop local MSTSC process
        RdpHost.StopProcess();
        
        // 2. Close SSH tunnel
        _sshService.StopRdpTunnel();
        
        // 3. Stop XRDP service on VM
        if (_sshService.IsConnected)
        {
            _sshService.SendText("sudo systemctl stop xrdp\n");
        }
    }
}
