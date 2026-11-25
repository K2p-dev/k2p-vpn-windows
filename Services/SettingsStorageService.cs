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

    public string? GetToken()
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(_settings.EncryptedTokenBase64))
                return null;

            try
            {
                var protectedBytes = Convert.FromBase64String(_settings.EncryptedTokenBase64);
                var rawBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(rawBytes);
            }
            catch
            {
                return null;
            }
        }
    }

    public void SetToken(string? token)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                _settings.EncryptedTokenBase64 = null;
            }
            else
            {
                var rawBytes = Encoding.UTF8.GetBytes(token.Trim());
                var protectedBytes = ProtectedData.Protect(rawBytes, null, DataProtectionScope.CurrentUser);
                _settings.EncryptedTokenBase64 = Convert.ToBase64String(protectedBytes);
            }
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
            _settings.SubscriptionUrl = null;
            _settings.CachedSubscriptionBody = null;
            _settings.CachedTrafficInfo = null;
            SaveToDisk();
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
            // Ignore corrupted config and start fresh
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
