using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SSHEasyApp.Models;

namespace SSHEasyApp.Services;

/// <summary>
/// Persists connection profiles to %APPDATA%\SSHEasyApp\profiles.json.
/// Passwords and passphrases are encrypted with Windows DPAPI before storage.
/// </summary>
public class ProfileStore
{
    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SSHEasyApp");

    private static readonly string ProfilesPath = Path.Combine(AppDir, "profiles.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private List<ConnectionProfile> _profiles = [];

    public IReadOnlyList<ConnectionProfile> Profiles => _profiles.AsReadOnly();

    public void Load()
    {
        Directory.CreateDirectory(AppDir);

        if (File.Exists(ProfilesPath))
        {
            var json = File.ReadAllText(ProfilesPath);
            _profiles = JsonSerializer.Deserialize<List<ConnectionProfile>>(json, JsonOptions) ?? [];
        }
        else
        {
            _profiles = [];
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDir);
        var json = JsonSerializer.Serialize(_profiles, JsonOptions);
        File.WriteAllText(ProfilesPath, json);
    }

    public void AddProfile(ConnectionProfile profile)
    {
        _profiles.Add(profile);
        Save();
    }

    public void UpdateProfile(ConnectionProfile profile)
    {
        var index = _profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
        {
            _profiles[index] = profile;
            Save();
        }
    }

    public void DeleteProfile(string id)
    {
        _profiles.RemoveAll(p => p.Id == id);
        Save();
    }

    public ConnectionProfile? GetById(string id)
    {
        return _profiles.FirstOrDefault(p => p.Id == id);
    }

    // ─── DPAPI Encryption Helpers ──────────────────────────────

    /// <summary>
    /// Encrypts a plaintext string using DPAPI (CurrentUser scope).
    /// Returns a Base64 string suitable for JSON storage.
    /// </summary>
    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        var data = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Decrypts a Base64 DPAPI-encrypted string back to plaintext.
    /// </summary>
    public static string Decrypt(string encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64)) return "";
        try
        {
            var data = Convert.FromBase64String(encryptedBase64);
            var decrypted = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return "";
        }
    }
}
