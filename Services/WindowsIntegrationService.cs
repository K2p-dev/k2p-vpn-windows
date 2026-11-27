using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using K2pVpnWindows.Config;
using Microsoft.Win32;
using WpfColor = System.Windows.Media.Color;

namespace K2pVpnWindows.Services;

public sealed class WindowsIntegrationService
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "K2pVpnWindows";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(nint hProcess, nint dwMinimumWorkingSetSize, nint dwMaximumWorkingSetSize);

    private readonly K2pApiClient _api;
    private readonly SettingsStorageService _storage;

    public WindowsIntegrationService(K2pApiClient api, SettingsStorageService storage)
    {
        _api = api;
        _storage = storage;
    }

    public static void TrimWorkingSetMemory()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            GC.WaitForPendingFinalizers();
            using var proc = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(proc.Handle, -1, -1);
        }
        catch
        {
            // Ignore working set trim errors
        }
    }

    public static bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("AppsUseLightTheme");
            return val is int i && i == 1;
        }
        catch
        {
            return false;
        }
    }

    public static WpfColor GetWindowsAccentColor()
    {
        try
        {
            var c = SystemParameters.WindowGlassColor;
            return WpfColor.FromRgb(c.R, c.G, c.B);
        }
        catch
        {
            return WpfColor.FromRgb(0xE1, 0x1D, 0x48);
        }
    }

    public static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                    key.SetValue(RunValueName, $"\"{exePath}\" --minimized", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Ignore registry errors
        }
    }

    public async Task<bool> UpdateGeodataAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppConfig.GeodataOverrideDir);
        var geoipDest = Path.Combine(AppConfig.GeodataOverrideDir, "geoip.dat");
        var geositeDest = Path.Combine(AppConfig.GeodataOverrideDir, "geosite.dat");

        var okIp = await _api.DownloadFileAsync(AppConfig.GeodataGeoIpUrl, geoipDest, ct)
                   || await _api.DownloadFileAsync(AppConfig.GeodataGeoIpFallbackUrl, geoipDest, ct);

        var okSite = await _api.DownloadFileAsync(AppConfig.GeodataGeoSiteUrl, geositeDest, ct)
                     || await _api.DownloadFileAsync(AppConfig.GeodataGeoSiteFallbackUrl, geositeDest, ct);

        if (okIp && okSite)
        {
            _storage.Update(s => s.LastGeodataUpdateUtc = DateTime.UtcNow);
            return true;
        }
        return false;
    }

    public string GetGeodataDateDisplay()
    {
        if (_storage.Current.LastGeodataUpdateUtc.HasValue)
            return _storage.Current.LastGeodataUpdateUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

        var coreGeoIp = Path.Combine(AppConfig.CoreDir, "geoip.dat");
        if (File.Exists(coreGeoIp))
            return File.GetLastWriteTime(coreGeoIp).ToString("dd.MM.yyyy");

        return "Встроенная";
    }

    public async Task<(bool UpdateAvailable, string LatestTag, string? DownloadUrl, string? Notes)> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var release = await _api.GetLatestGithubReleaseAsync(ct);
        _storage.Update(s => s.LastUpdateCheckUtc = DateTime.UtcNow);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            return (false, AppConfig.AppVersion, null, null);

        var currentNorm = NormalizeVersion(AppConfig.AppVersion);
        var latestNorm = NormalizeVersion(release.TagName);
        var isNewer = latestNorm.CompareTo(currentNorm) > 0;

        var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        return (isNewer, release.TagName, asset?.BrowserDownloadUrl ?? release.HtmlUrl, release.Body);
    }

    public async Task<bool> DownloadAndApplyUpdateAsync(string downloadUrl, Action? beforeExit, CancellationToken ct = default)
    {
        if (!downloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            OpenUrlInBrowser(downloadUrl);
            return true;
        }

        var updateDir = Path.Combine(AppConfig.AppDataDir, "update");
        Directory.CreateDirectory(updateDir);
        var fileName = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
        var downloadPath = Path.Combine(updateDir, fileName);

        var ok = await _api.DownloadFileAsync(downloadUrl, downloadPath, ct);
        if (!ok) return false;

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe)) return false;

        var appDir = Path.GetDirectoryName(currentExe)!;
        var scriptPath = Path.Combine(updateDir, "apply_update.cmd");

        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extractDir = Path.Combine(updateDir, "extracted");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(downloadPath, extractDir, true);

            var script = $"""
                @echo off
                timeout /t 2 /nobreak >nul
                xcopy /E /Y /I "{extractDir}\*" "{appDir}\" >nul
                start "" "{currentExe}"
                """;
            await File.WriteAllTextAsync(scriptPath, script, ct);
        }
        else
        {
            var script = $"""
                @echo off
                timeout /t 2 /nobreak >nul
                copy /Y "{downloadPath}" "{currentExe}" >nul
                start "" "{currentExe}"
                """;
            await File.WriteAllTextAsync(scriptPath, script, ct);
        }

        beforeExit?.Invoke();
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        return true;
    }

    public static void OpenUrlInBrowser(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch
        {
            // Ignore browser launch errors
        }
    }

    private static Version NormalizeVersion(string raw)
    {
        var digits = new string(raw.TrimStart('v', 'V')
            .TakeWhile(c => char.IsDigit(c) || c == '.')
            .ToArray())
            .TrimEnd('.');
        return Version.TryParse(digits, out var v) ? v : new Version(0, 2, 0);
    }
}
