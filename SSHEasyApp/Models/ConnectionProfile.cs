namespace SSHEasyApp.Models;

public enum AuthMethod
{
    Password,
    PrivateKey
}

public class ConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;

    /// <summary>
    /// DPAPI-encrypted password, stored as Base64 string.
    /// </summary>
    public string EncryptedPassword { get; set; } = "";

    /// <summary>
    /// Absolute path to the private key file.
    /// </summary>
    public string PrivateKeyPath { get; set; } = "";

    /// <summary>
    /// DPAPI-encrypted passphrase for the private key, stored as Base64 string.
    /// </summary>
    public string EncryptedPassphrase { get; set; } = "";

    public DateTime LastConnected { get; set; }
}
