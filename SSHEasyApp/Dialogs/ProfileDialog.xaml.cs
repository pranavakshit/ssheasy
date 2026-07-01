using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SSHEasyApp.Models;
using SSHEasyApp.Services;

namespace SSHEasyApp.Dialogs;

public partial class ProfileDialog : Window
{
    public ConnectionProfile? Result { get; private set; }

    private readonly ConnectionProfile? _existingProfile;

    public ProfileDialog(ConnectionProfile? existing = null)
    {
        InitializeComponent();
        _existingProfile = existing;

        if (existing != null)
        {
            Title = "Edit Profile";
            NameBox.Text = existing.Name;
            HostBox.Text = existing.Host;
            PortBox.Text = existing.Port.ToString();
            UsernameBox.Text = existing.Username;

            if (existing.AuthMethod == AuthMethod.Password)
            {
                PasswordRadio.IsChecked = true;
                PasswordBox.Password = ProfileStore.Decrypt(existing.EncryptedPassword);
            }
            else
            {
                KeyRadio.IsChecked = true;
                KeyPathBox.Text = existing.PrivateKeyPath;
                PassphraseBox.Password = ProfileStore.Decrypt(existing.EncryptedPassphrase);
            }
        }

        UpdateAuthVisibility();
    }

    private void AuthMethod_Changed(object sender, RoutedEventArgs e)
    {
        UpdateAuthVisibility();
    }

    private void UpdateAuthVisibility()
    {
        if (PasswordPanel == null || KeyPanel == null || PassphrasePanel == null) return;

        bool isKey = KeyRadio.IsChecked == true;
        PasswordPanel.Visibility = isKey ? Visibility.Collapsed : Visibility.Visible;
        KeyPanel.Visibility = isKey ? Visibility.Visible : Visibility.Collapsed;
        PassphrasePanel.Visibility = isKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Private Key File",
            Filter = "All Files (*.*)|*.*|PEM Files (*.pem)|*.pem",
            InitialDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh")
        };

        if (dialog.ShowDialog() == true)
        {
            KeyPathBox.Text = dialog.FileName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Validation
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show("Please enter a profile name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            MessageBox.Show("Please enter a host address.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            HostBox.Focus();
            return;
        }

        if (!int.TryParse(PortBox.Text, out int port) || port < 1 || port > 65535)
        {
            MessageBox.Show("Please enter a valid port (1-65535).", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            PortBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(UsernameBox.Text))
        {
            MessageBox.Show("Please enter a username.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            UsernameBox.Focus();
            return;
        }

        bool isKey = KeyRadio.IsChecked == true;

        var profile = new ConnectionProfile
        {
            Id = _existingProfile?.Id ?? Guid.NewGuid().ToString(),
            Name = NameBox.Text.Trim(),
            Host = HostBox.Text.Trim(),
            Port = port,
            Username = UsernameBox.Text.Trim(),
            AuthMethod = isKey ? AuthMethod.PrivateKey : AuthMethod.Password,
            LastConnected = _existingProfile?.LastConnected ?? DateTime.MinValue
        };

        if (isKey)
        {
            profile.PrivateKeyPath = KeyPathBox.Text.Trim();
            profile.EncryptedPassphrase = ProfileStore.Encrypt(PassphraseBox.Password);
            profile.EncryptedPassword = "";
        }
        else
        {
            profile.EncryptedPassword = ProfileStore.Encrypt(PasswordBox.Password);
            profile.EncryptedPassphrase = "";
            profile.PrivateKeyPath = "";
        }

        Result = profile;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }
}
