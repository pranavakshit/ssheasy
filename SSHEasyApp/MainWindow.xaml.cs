using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SSHEasyApp.Dialogs;
using SSHEasyApp.Models;
using SSHEasyApp.Services;

namespace SSHEasyApp;

public partial class MainWindow : Window
{
    private readonly ProfileStore _profileStore = new();
    private SshService? _sshService;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        
        Terminal.UserInput += OnTerminalUserInput;
        Terminal.TerminalResized += OnTerminalResized;
        
        ProfilesList.SelectionChanged += ProfilesList_SelectionChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _profileStore.Load();
        
        // Add a default profile if none exist (for the user's specific request)
        if (_profileStore.Profiles.Count == 0)
        {
            // The user mentioned: ssh -i id_rsa ubuntu@141.148.207.232
            var defaultProfile = new ConnectionProfile
            {
                Name = "Ubuntu VM",
                Host = "141.148.207.232",
                Username = "ubuntu",
                AuthMethod = AuthMethod.PrivateKey,
                PrivateKeyPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
                    ".ssh", "id_rsa")
            };
            _profileStore.AddProfile(defaultProfile);
        }
        
        RefreshProfilesList();
    }
    
    private void RefreshProfilesList()
    {
        ProfilesList.ItemsSource = null;
        ProfilesList.ItemsSource = _profileStore.Profiles.OrderByDescending(p => p.LastConnected).ToList();
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfileDialog
        {
            Owner = this
        };
        
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            _profileStore.AddProfile(dialog.Result);
            RefreshProfilesList();
        }
    }

    private void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            var profile = _profileStore.GetById(id);
            if (profile != null)
            {
                var dialog = new ProfileDialog(profile)
                {
                    Owner = this
                };
                
                if (dialog.ShowDialog() == true && dialog.Result != null)
                {
                    _profileStore.UpdateProfile(dialog.Result);
                    RefreshProfilesList();
                }
            }
        }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            var profile = _profileStore.GetById(id);
            if (profile != null)
            {
                var result = MessageBox.Show($"Are you sure you want to delete the profile '{profile.Name}'?",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    _profileStore.DeleteProfile(id);
                    RefreshProfilesList();
                }
            }
        }
    }

    private async void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesList.SelectedItem is ConnectionProfile profile)
        {
            // Clear selection so we can click the same profile again
            ProfilesList.SelectedItem = null;
            
            await ConnectToProfileAsync(profile);
        }
    }

    private async Task ConnectToProfileAsync(ConnectionProfile profile)
    {
        // Disconnect existing
        Disconnect_Click(this, new RoutedEventArgs());
        
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        Terminal.Visibility = Visibility.Visible;
        
        UpdateStatus("Connecting...", Colors.Yellow, $"to {profile.Username}@{profile.Host}:{profile.Port}");
        
        try
        {
            _sshService = new SshService();
            _sshService.DataReceived += OnSshDataReceived;
            _sshService.ConnectionLost += OnSshConnectionLost;
            
            _sshService.HostKeyVerification = (keyType, fingerprint) =>
            {
                // In a real app, we'd prompt the user and save the key hash. 
                // For now, we auto-accept.
                return true; 
            };
            
            string password = "";
            string passphrase = "";
            
            if (profile.AuthMethod == AuthMethod.Password)
                password = ProfileStore.Decrypt(profile.EncryptedPassword);
            else
                passphrase = ProfileStore.Decrypt(profile.EncryptedPassphrase);
                
            var size = Terminal.GetTerminalSize();
            
            await _sshService.ConnectAsync(profile, password, passphrase, size.cols, size.rows);
            
            UpdateStatus("Connected", Colors.LimeGreen, $"to {profile.Username}@{profile.Host}");
            DisconnectButton.Visibility = Visibility.Visible;
            RebootButton.Visibility = Visibility.Visible;
            ShutdownButton.Visibility = Visibility.Visible;
            RdpButton.Visibility = Visibility.Visible;
            
            // Update last connected time
            profile.LastConnected = DateTime.Now;
            _profileStore.UpdateProfile(profile);
            RefreshProfilesList();
            
            Terminal.Focus();
        }
        catch (Exception ex)
        {
            UpdateStatus("Connection Failed", Colors.Red, "");
            MessageBox.Show($"Failed to connect:\n{ex.Message}", "Connection Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
                
            _sshService?.Dispose();
            _sshService = null;
        }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _sshService?.Disconnect();
        UpdateConnectionState(false);
    }

    private void OnSshDataReceived(byte[] data)
    {
        Dispatcher.InvokeAsync(() =>
        {
            Terminal.ProcessData(data);
        });
    }

    private void OnSshConnectionLost(string reason)
    {
        UpdateConnectionState(false);
    }

    private void UpdateConnectionState(bool isConnected)
    {
        Dispatcher.Invoke(() =>
        {
            if (isConnected)
            {
                StatusDot.Fill = (Brush)FindResource("Accent");
                StatusText.Text = "Connected";
                StatusText.Foreground = (Brush)FindResource("FgDefault");
                ConnectionInfoText.Text = $"to {_sshService!.Username}@{_sshService.Host}";
                
                EmptyStatePanel.Visibility = Visibility.Collapsed;
                Terminal.Visibility = Visibility.Visible;
                
                DisconnectButton.Visibility = Visibility.Visible;
                RebootButton.Visibility = Visibility.Visible;
                ShutdownButton.Visibility = Visibility.Visible;
                RdpButton.Visibility = Visibility.Visible;
            }
            else
            {
                StatusDot.Fill = (Brush)FindResource("FgMuted");
                StatusText.Text = "Not Connected";
                StatusText.Foreground = (Brush)FindResource("FgDefault");
                ConnectionInfoText.Text = "";
                
                EmptyStatePanel.Visibility = Visibility.Visible;
                Terminal.Visibility = Visibility.Collapsed;
                
                DisconnectButton.Visibility = Visibility.Collapsed;
                RebootButton.Visibility = Visibility.Collapsed;
                ShutdownButton.Visibility = Visibility.Collapsed;
                RdpButton.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void OnTerminalUserInput(byte[] data)
    {
        if (_sshService?.IsConnected == true)
        {
            _sshService.SendData(data);
        }
    }

    private void OnTerminalResized(int cols, int rows)
    {
        if (_sshService?.IsConnected == true)
        {
            _sshService.Resize(cols, rows);
        }
    }
    
    private void UpdateStatus(string text, Color dotColor, string info)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(dotColor);
        ConnectionInfoText.Text = info;
    }

    private void Reboot_Click(object sender, RoutedEventArgs e)
    {
        if (_sshService?.IsConnected == true)
        {
            var result = MessageBox.Show("Are you sure you want to reboot the server?", "Confirm Reboot", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                _sshService.SendText("sudo reboot\n");
            }
        }
    }

    private void Shutdown_Click(object sender, RoutedEventArgs e)
    {
        if (_sshService?.IsConnected == true)
        {
            var result = MessageBox.Show("Are you sure you want to shutdown the server?", "Confirm Shutdown", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                _sshService.SendText("sudo shutdown -h now\n");
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _sshService?.Dispose();
        base.OnClosed(e);
    }
    

    
    private void RdpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sshService == null || !_sshService.IsConnected) return;

        var rdpWindow = new RdpWindow(_sshService);
        rdpWindow.Owner = this;
        rdpWindow.Show();
    }
}
