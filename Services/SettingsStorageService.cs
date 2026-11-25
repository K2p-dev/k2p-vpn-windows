using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using K2pVpnWindows.Config;
using K2pVpnWindows.Models;
using Microsoft.Win32;

namespace K2pVpnWindows.Services;

public sealed class SettingsStorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private LocalAppSettings _settings;

    public SettingsStorageService()
    {
        _settings = LoadFromDisk();
        if (string.IsNullOrWhiteSpace(_settings.HardwareId))
        {
            _settings.HardwareId = ComputeStableHardwareId();
            SaveToDisk();
        }
    }

    public LocalAppSettings Current => _settings;

    public string HardwareId => _settings.HardwareId ??= ComputeStableHardwareId();

    public string DeviceName => $"{Environment.MachineName} (Windows)";

    public string? GetToken() => UnprotectString(_settings.EncryptedTokenBase64);

    public void SetToken(string? token)
    {
        lock (_lock)
        {
            _settings.EncryptedTokenBase64 = ProtectString(token);
            SaveToDisk();
        }
    }

    public string? GetSubscriptionUrl() => UnprotectString(_settings.EncryptedSubscriptionUrlBase64);

    public void SetSubscriptionUrl(string? url)
    {
        lock (_lock)
        {
            _settings.EncryptedSubscriptionUrlBase64 = ProtectString(url);
            SaveToDisk();
        }
    }

    public void Update(Action<LocalAppSettings> mutate)
    {
        lock (_lock)
        {
            mutate(_settings);
            SaveToDisk();
        }
    }

    public void ClearAuth()
    {
        lock (_lock)
        {
            _settings.EncryptedTokenBase64 = null;
            _settings.EncryptedSubscriptionUrlBase64 = null;
            _settings.CachedSubscriptionBody = null;
            _settings.CachedTrafficInfo = null;
            SaveToDisk();
        }
    }

    private static string? ProtectString(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return null;
        var rawBytes = Encoding.UTF8.GetBytes(plain.Trim());
        var protectedBytes = ProtectedData.Protect(rawBytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string? UnprotectString(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var protectedBytes = Convert.FromBase64String(base64);
            var rawBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(rawBytes);
        }
        catch
        {
            return null;
        }
    }

    private LocalAppSettings LoadFromDisk()
    {
        try
        {
            if (File.Exists(AppConfig.ConfigFilePath))
            {
                var json = File.ReadAllText(AppConfig.ConfigFilePath, Encoding.UTF8);
                return JsonSerializer.Deserialize<LocalAppSettings>(json, JsonOptions) ?? new LocalAppSettings();
            }
        }
        catch
        {
            // Ignore corrupted config
        }
        return new LocalAppSettings();
    }

    private void SaveToDisk()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.AppDataDir);
            var json = JsonSerializer.Serialize(_settings, JsonOptions);
            File.WriteAllText(AppConfig.ConfigFilePath, json, Encoding.UTF8);
        }
        catch
        {
            // Best effort persistence
        }
    }

    private static string ComputeStableHardwareId()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var machineGuid = key?.GetValue("MachineGuid")?.ToString();
            var seed = !string.IsNullOrWhiteSpace(machineGuid)
                ? $"k2p-win-{machineGuid}"
                : $"k2p-win-{Environment.MachineName}-{Environment.UserName}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return Convert.ToHexString(hash).ToLowerInvariant()[..32];
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
