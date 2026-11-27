using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using K2pVpnWindows.Config;
using Microsoft.Win32;
using WpfColor = System.Windows.Media.Color;

namespace K2pVpnWindows.Services;

public sealed class WindowsIntegrationService
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "K2pVpnWindows";

    private static readonly string[] TrustedDownloadHosts =
    [
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "codeload.github.com"
    ];

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

    public async Task<(bool UpdateAvailable, string LatestTag, string? UpdateZipUrl, string? Sha256Url, string? Notes)> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        AppLogger.Log("OTA", "Проверка обновлений на GitHub...");
        var release = await _api.GetLatestGithubReleaseAsync(ct);
        _storage.Update(s => s.LastUpdateCheckUtc = DateTime.UtcNow);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
        {
            AppLogger.Log("OTA", "Не удалось получить информацию о последнем релизе.");
            return (false, AppConfig.AppVersion, null, null, null);
        }

        var currentNorm = NormalizeVersion(AppConfig.AppVersion);
        var latestNorm = NormalizeVersion(release.TagName);
        var isNewer = latestNorm.CompareTo(currentNorm) > 0;

        // Strictly look for K2pVpn-Update-*.zip (ignoring K2pVpn-Setup-*.exe)
        var updateAsset = release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("K2pVpn-Update-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        var shaAsset = updateAsset is not null
            ? release.Assets.FirstOrDefault(a =>
                a.Name.Equals(updateAsset.Name + ".sha256", StringComparison.OrdinalIgnoreCase))
            : null;

        AppLogger.Log("OTA", $"Текущая: {currentNorm}, последняя: {latestNorm}, обновление={isNewer}, zip={updateAsset?.Name ?? "нет"}");
        return (isNewer, release.TagName, updateAsset?.BrowserDownloadUrl, shaAsset?.BrowserDownloadUrl, release.Body);
    }

    public static bool IsTrustedGithubDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        return TrustedDownloadHosts.Any(h =>
            uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<(bool Ok, string? Error)> DownloadAndApplyUpdateAsync(
        string updateZipUrl,
        string? sha256Url,
        Action? beforeExit,
        CancellationToken ct = default)
    {
        if (!IsTrustedGithubDownloadUrl(updateZipUrl))
        {
            const string err = "Отклонён небезопасный URL обновления (разрешены только доверенные домены GitHub HTTPS).";
            AppLogger.Log("OTA", err);
            return (false, err);
        }

        var updatesDir = Path.Combine(AppConfig.AppDataDir, "updates");
        var stagingDir = Path.Combine(updatesDir, "staging");
        var zipPath = Path.Combine(updatesDir, "update.zip");
        var scriptPath = Path.Combine(updatesDir, "updater.ps1");

        try
        {
            Directory.CreateDirectory(updatesDir);
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
            Directory.CreateDirectory(stagingDir);

            AppLogger.Log("OTA", $"Скачивание архива обновления в {zipPath}...");
            var ok = await _api.DownloadFileAsync(updateZipUrl, zipPath, ct);
            if (!ok || !File.Exists(zipPath))
            {
                const string err = "Не удалось скачать архив обновления.";
                AppLogger.Log("OTA", err);
                return (false, err);
            }

            // Verify SHA-256 if checksum asset is available
            if (!string.IsNullOrWhiteSpace(sha256Url) && IsTrustedGithubDownloadUrl(sha256Url))
            {
                var shaPath = Path.Combine(updatesDir, "update.zip.sha256");
                if (await _api.DownloadFileAsync(sha256Url, shaPath, ct) && File.Exists(shaPath))
                {
                    var expectedLine = (await File.ReadAllTextAsync(shaPath, ct)).Trim();
                    var expectedHash = expectedLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
                    await using var fs = File.OpenRead(zipPath);
                    var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(expectedHash) && !string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
                    {
                        const string err = "Контрольная сумма SHA-256 архива обновления не совпадает!";
                        AppLogger.Log("OTA", $"{err} expected={expectedHash}, actual={actualHash}");
                        return (false, err);
                    }
                    AppLogger.Log("OTA", $"SHA-256 проверен успешно: {actualHash}");
                }
            }

            // Zip Slip-safe extraction into staging\
            var stagingFull = Path.GetFullPath(stagingDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            AppLogger.Log("OTA", $"Распаковка update.zip в {stagingDir}...");
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    var destPath = Path.GetFullPath(Path.Combine(stagingDir, entry.FullName));
                    if (!destPath.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase))
                    {
                        const string err = "Обнаружена попытка Zip Slip при распаковке архива обновления.";
                        AppLogger.Log("OTA", $"{err} Entry={entry.FullName}");
                        return (false, err);
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destPath);
                    }
                    else
                    {
                        var parent = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                }
            }

            var stagedExe = Path.Combine(stagingDir, "K2P-VPN-Windows.exe");
            if (!File.Exists(stagedExe) || new FileInfo(stagedExe).Length < 1024 * 1024)
            {
                const string err = "Архив обновления повреждён: отсутствует корректный K2P-VPN-Windows.exe.";
                AppLogger.Log("OTA", err);
                return (false, err);
            }

            var installDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var pid = Environment.ProcessId;
            var escStaging = stagingDir.Replace("'", "''");
            var escInstall = installDir.Replace("'", "''");
            var escZip = zipPath.Replace("'", "''");
            var escScript = scriptPath.Replace("'", "''");

            var psScript = $$"""
                $ErrorActionPreference = 'SilentlyContinue'
                $targetPid = {{pid}}
                try {
                    $proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
                    if ($proc) { $proc.WaitForExit(15000) | Out-Null }
                } catch {}

                Stop-Process -Name 'xray','sing-box','hev-socks5-tunnel-win64' -Force -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 500

                Copy-Item -Path '{{escStaging}}\*' -Destination '{{escInstall}}' -Recurse -Force
                Start-Process -FilePath '{{escInstall}}\K2P-VPN-Windows.exe'

                Remove-Item -Path '{{escZip}}' -Force -ErrorAction SilentlyContinue
                Remove-Item -Path '{{escStaging}}' -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -Path '{{escScript}}' -Force -ErrorAction SilentlyContinue
                """;

            await File.WriteAllTextAsync(scriptPath, psScript, new UTF8Encoding(false), ct);
            AppLogger.Log("OTA", $"Запуск отвязанного updater.ps1 (PID={pid}, InstallDir={installDir})...");

            beforeExit?.Invoke();

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            Environment.Exit(0);
            return (true, null);
        }
        catch (Exception ex)
        {
            AppLogger.Log("OTA", $"Ошибка OTA-обновления: {ex.Message}");
            return (false, ex.Message);
        }
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
        return Version.TryParse(digits, out var v) ? v : new Version(0, 3, 2);
    }
}
