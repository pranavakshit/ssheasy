using System.IO;
using Renci.SshNet;
using SSHEasyApp.Models;

namespace SSHEasyApp.Services;

/// <summary>
/// Wraps SSH.NET to manage SSH connections and interactive shell streams.
/// </summary>
public class SshService : IDisposable
{
    private SshClient? _client;
    private ShellStream? _shellStream;
    private CancellationTokenSource? _readCts;

    private ForwardedPortLocal? _rdpPort;

    public bool IsConnected => _client?.IsConnected == true;
    public string Host => _client?.ConnectionInfo.Host ?? "";
    public string Username => _client?.ConnectionInfo.Username ?? "";

    /// <summary>
    /// Fired when data is received from the remote shell (raw bytes).
    /// </summary>
    public event Action<byte[]>? DataReceived;

    /// <summary>
    /// Fired when the connection is lost unexpectedly.
    /// </summary>
    public event Action<string>? ConnectionLost;

    /// <summary>
    /// Fired when the server presents its host key for verification.
    /// Return true to accept, false to reject.
    /// </summary>
    public Func<string, string, bool>? HostKeyVerification { get; set; }

    /// <summary>
    /// Connects to the SSH server described by the given profile.
    /// </summary>
    public async Task ConnectAsync(ConnectionProfile profile, string? password, string? passphrase, int cols, int rows)
    {
        await Task.Run(() =>
        {
            // Build authentication method
            AuthenticationMethod authMethod;

            if (profile.AuthMethod == AuthMethod.PrivateKey)
            {
                if (string.IsNullOrEmpty(profile.PrivateKeyPath) || !File.Exists(profile.PrivateKeyPath))
                    throw new FileNotFoundException($"Private key file not found: {profile.PrivateKeyPath}");

                PrivateKeyFile keyFile;
                if (!string.IsNullOrEmpty(passphrase))
                    keyFile = new PrivateKeyFile(profile.PrivateKeyPath, passphrase);
                else
                    keyFile = new PrivateKeyFile(profile.PrivateKeyPath);

                authMethod = new PrivateKeyAuthenticationMethod(profile.Username, keyFile);
            }
            else
            {
                authMethod = new PasswordAuthenticationMethod(profile.Username, password ?? "");
            }

            var connectionInfo = new ConnectionInfo(
                profile.Host,
                profile.Port,
                profile.Username,
                authMethod)
            {
                Timeout = TimeSpan.FromSeconds(15),
                ChannelCloseTimeout = TimeSpan.FromSeconds(5)
            };

            _client = new SshClient(connectionInfo);

            // Host key verification
            _client.HostKeyReceived += (sender, e) =>
            {
                var fingerprint = BitConverter.ToString(e.FingerPrint).Replace("-", ":").ToLowerInvariant();
                var keyType = e.HostKeyName;
                if (HostKeyVerification != null)
                {
                    e.CanTrust = HostKeyVerification(keyType, fingerprint);
                }
                else
                {
                    e.CanTrust = true;
                }
            };

            _client.ErrorOccurred += (sender, e) =>
            {
                ConnectionLost?.Invoke(e.Exception.Message);
            };

            _client.Connect();

            // Create interactive shell
            _shellStream = _client.CreateShellStream(
                "xterm-256color",
                (uint)cols, (uint)rows,
                (uint)(cols * 10), (uint)(rows * 20),
                4096);

            // Start reading output
            _readCts = new CancellationTokenSource();
            StartReading(_readCts.Token);
        });
    }

    private void StartReading(CancellationToken ct)
    {
        Task.Run(async () =>
        {
            var buffer = new byte[8192];
            try
            {
                while (!ct.IsCancellationRequested && _shellStream != null)
                {
                    var bytesRead = await _shellStream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (bytesRead > 0)
                    {
                        var data = new byte[bytesRead];
                        Array.Copy(buffer, data, bytesRead);
                        DataReceived?.Invoke(data);
                    }
                    else
                    {
                        // Stream ended
                        await Task.Delay(50, ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    ConnectionLost?.Invoke(ex.Message);
            }
        }, ct);
    }

    /// <summary>
    /// Sends raw bytes to the remote shell (keyboard input).
    /// </summary>
    public void SendData(byte[] data)
    {
        _shellStream?.Write(data, 0, data.Length);
        _shellStream?.Flush();
    }

    /// <summary>
    /// Sends a text string to the remote shell.
    /// </summary>
    public void SendText(string text)
    {
        if (_shellStream != null)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            _shellStream.Write(bytes, 0, bytes.Length);
            _shellStream.Flush();
        }
    }
    
    public async Task<string> RunCommandAsync(string commandText)
    {
        if (_client == null || !_client.IsConnected) throw new InvalidOperationException("Not connected");
        return await Task.Run(() => 
        {
            using var cmd = _client.CreateCommand(commandText);
            return cmd.Execute();
        });
    }

    /// <summary>
    /// Sends a terminal window resize request.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        try
        {
            // Resizing is not supported directly on ShellStream in this version of SSH.NET
        }
        catch
        {
            // SendWindowChangeRequest may not be available in all SSH.NET versions
        }
    }

    public void StartRdpTunnel(uint localPort, string remoteHost, uint remotePort)
    {
        if (_client == null || !_client.IsConnected) throw new InvalidOperationException("Not connected");
        
        _rdpPort = new ForwardedPortLocal("127.0.0.1", localPort, remoteHost, remotePort);
        _client.AddForwardedPort(_rdpPort);
        _rdpPort.Start();
    }

    public void StopRdpTunnel()
    {
        if (_rdpPort != null)
        {
            if (_rdpPort.IsStarted) _rdpPort.Stop();
            _client?.RemoveForwardedPort(_rdpPort);
            _rdpPort.Dispose();
            _rdpPort = null;
        }
    }

    public void Disconnect()
    {
        _readCts?.Cancel();
        _readCts?.Dispose();
        _readCts = null;

        _shellStream?.Close();
        _shellStream?.Dispose();
        _shellStream = null;

        if (_client?.IsConnected == true)
            _client.Disconnect();

        _client?.Dispose();
        _client = null;
    }

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}
