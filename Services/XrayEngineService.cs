using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using K2pVpnWindows.Config;
using K2pVpnWindows.Models;

namespace K2pVpnWindows.Services;

public sealed class XrayEngineService : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    private readonly SettingsStorageService _storage;
    private readonly object _logLock = new();
    private readonly List<string> _recentLogs = [];

    private Process? _coreProcess;
    private Process? _tunProcess;
    private VpnMode? _activeMode;
    private AppVpnCore? _activeCore;
    private VpnServerNode? _activeNode;
    private string? _bypassServerIp;
    private DateTime? _connectedAtUtc;
    private long _lastRxBytes;
    private long _lastTxBytes;
    private DateTime _lastSampleUtc;

    public VpnConnectionState State { get; private set; } = VpnConnectionState.Disconnected;
    public VpnServerNode? ActiveNode => _activeNode;
    public AppVpnCore? ActiveCore => _activeCore;
    public string? LastError { get; private set; }
    public int CurrentSocksPort { get; private set; }
    public int CurrentHttpPort { get; private set; }

    public event Action<VpnConnectionState>? StateChanged;

    public XrayEngineService(SettingsStorageService storage)
    {
        _storage = storage;
    }

    public static AppVpnCore ResolveEffectiveCore(VpnServerNode node, AppVpnCore preferred)
    {
        var net = node.Network?.ToLowerInvariant();
        if (net is "xhttp" or "splithttp")
            return AppVpnCore.Xray;
        return preferred;
    }

    public Task<bool> StartAsync(VpnServerNode node, VpnMode mode, bool bypassRussia) =>
        StartAsync(node, mode, bypassRussia, _storage.Current.VpnCore);

    public async Task<bool> StartAsync(VpnServerNode node, VpnMode mode, bool bypassRussia, AppVpnCore preferredCore)
    {
        await StopAsync();
        await KillStaleCoreProcessesAsync();
        if (mode == VpnMode.Tun)
        {
            CleanupStaleTunProfiles();
        }

        var core = ResolveEffectiveCore(node, preferredCore);
        if (core != preferredCore)
        {
            AppLogger.Log("APP", $"Узел использует транспорт {node.Network}, автоматически выбрано ядро Xray.");
        }

        AppLogger.Log("APP", $"Запуск подключения: {node.Name} ({node.ProtocolLabel}), ядро={core}, режим={mode}, обходРФ={bypassRussia}");

        var validationError = ValidateNode(node);
        if (validationError is not null)
        {
            LastError = validationError;
            AppLogger.Log("APP", $"Ошибка валидации узла: {validationError}");
            return false;
        }

        State = VpnConnectionState.Connecting;
        StateChanged?.Invoke(State);
        LastError = null;
        lock (_logLock) _recentLogs.Clear();

        try
        {
            Directory.CreateDirectory(AppConfig.AppDataDir);
            var socksPort = GetFreeLoopbackPort();
            var httpPort = GetFreeLoopbackPort();
            if (httpPort == socksPort) httpPort = GetFreeLoopbackPort();
            CurrentSocksPort = socksPort;
            CurrentHttpPort = httpPort;

            bool ok = core == AppVpnCore.SingBox
                ? await StartSingBoxCoreAsync(node, mode, bypassRussia, socksPort, httpPort)
                : await StartXrayCoreAsync(node, mode, bypassRussia, socksPort, httpPort);

            if (!ok)
            {
                await StopAsync();
                return false;
            }

            _activeMode = mode;
            _activeCore = core;
            _activeNode = node;
            _connectedAtUtc = DateTime.UtcNow;
            _lastRxBytes = 0;
            _lastTxBytes = 0;
            _lastSampleUtc = DateTime.UtcNow;

            State = VpnConnectionState.Connected;
            StateChanged?.Invoke(State);
            AppLogger.Log("APP", $"VPN успешно подключён (ядро: {core}).");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLogger.Log("APP", $"Исключение при старте: {ex}");
            await StopAsync();
            return false;
        }
    }

    private async Task<bool> StartSingBoxCoreAsync(VpnServerNode node, VpnMode mode, bool bypassRussia, int socksPort, int httpPort)
    {
        var coreFullDir = Path.GetFullPath(AppConfig.CoreDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var sbExe = Path.GetFullPath(Path.Combine(AppConfig.CoreDir, "sing-box.exe"));
        if (!sbExe.StartsWith(coreFullDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(sbExe))
        {
            LastError = "Не найден доверенный бинарник Core/sing-box.exe";
            AppLogger.Log("SING-BOX", LastError);
            return false;
        }

        if (mode == VpnMode.Tun)
        {
            var wintunDll = Path.GetFullPath(Path.Combine(AppConfig.CoreDir, "wintun.dll"));
            if (!wintunDll.StartsWith(coreFullDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(wintunDll))
            {
                LastError = "Не найден доверенный Core/wintun.dll для TUN-режима";
                AppLogger.Log("SING-BOX", LastError);
                return false;
            }
        }

        var configJson = SingBoxConfigBuilder.Build(node, mode, bypassRussia, socksPort, httpPort, _storage.Current.BypassExclusions);
        await File.WriteAllTextAsync(AppConfig.SingBoxRuntimeConfigPath, configJson, Utf8NoBom);
        AppLogger.Log("SING-BOX", $"sing-box-runtime.json:\n{AppLogger.MaskSecrets(configJson)}");

        var psi = new ProcessStartInfo
        {
            FileName = sbExe,
            WorkingDirectory = AppConfig.CoreDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(AppConfig.SingBoxRuntimeConfigPath);

        _coreProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _coreProcess.OutputDataReceived += (_, e) => AppendLog("SING-BOX", e.Data);
        _coreProcess.ErrorDataReceived += (_, e) => AppendLog("SING-BOX", e.Data);
        _coreProcess.Exited += (_, _) =>
        {
            AppLogger.Log("SING-BOX", $"Процесс sing-box.exe завершился (ExitCode={_coreProcess?.ExitCode})");
            if (State is VpnConnectionState.Connected or VpnConnectionState.Connecting)
            {
                CleanupNetworkState();
                State = VpnConnectionState.Disconnected;
                _connectedAtUtc = null;
                StateChanged?.Invoke(State);
            }
        };

        if (!_coreProcess.Start())
        {
            LastError = "Не удалось запустить sing-box.exe";
            return false;
        }

        try { _coreProcess.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        _coreProcess.BeginOutputReadLine();
        _coreProcess.BeginErrorReadLine();

        if (mode == VpnMode.SystemProxy)
        {
            var listening = await WaitForLocalPortAsync(socksPort, TimeSpan.FromSeconds(4));
            if (_coreProcess.HasExited || !listening)
            {
                var err = GetRecentErrorSummary();
                LastError = string.IsNullOrWhiteSpace(err) ? $"sing-box завершился с кодом {_coreProcess.ExitCode}" : $"sing-box: {err}";
                return false;
            }
            SystemProxyService.EnableSystemProxy("127.0.0.1", httpPort);
            AppLogger.Log("SING-BOX", $"Включён системный прокси 127.0.0.1:{httpPort}");
        }
        else
        {
            await Task.Delay(1200);
            if (_coreProcess.HasExited)
            {
                var err = GetRecentErrorSummary();
                LastError = string.IsNullOrWhiteSpace(err) ? $"sing-box завершился с кодом {_coreProcess.ExitCode}" : $"sing-box: {err}";
                return false;
            }
        }

        return true;
    }

    private async Task<bool> StartXrayCoreAsync(VpnServerNode node, VpnMode mode, bool bypassRussia, int socksPort, int httpPort)
    {
        var coreFullDir = Path.GetFullPath(AppConfig.CoreDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var xrayExe = Path.GetFullPath(Path.Combine(AppConfig.CoreDir, "xray.exe"));
        if (!xrayExe.StartsWith(coreFullDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(xrayExe))
        {
            LastError = "Не найден доверенный бинарник Core/xray.exe";
            AppLogger.Log("XRAY", LastError);
            return false;
        }

        string? hevExe = null;
        if (mode == VpnMode.Tun)
        {
            var wintunDll = Path.GetFullPath(Path.Combine(AppConfig.CoreDir, "wintun.dll"));
            hevExe = Path.GetFullPath(Path.Combine(AppConfig.CoreDir, "hev-socks5-tunnel-win64.exe"));
            if (!wintunDll.StartsWith(coreFullDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(wintunDll))
            {
                LastError = "Не найден доверенный Core/wintun.dll для TUN-режима";
                return false;
            }
            if (!hevExe.StartsWith(coreFullDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(hevExe))
            {
                LastError = "Не найден доверенный Core/hev-socks5-tunnel-win64.exe для TUN-режима";
                return false;
            }
        }

        var assetDir = ResolveGeodataDirectory();
        var hasGeodata = HasValidGeodata(assetDir);
        var physicalNet = GetActivePhysicalInterfaceInfo();
        if (physicalNet is not null)
        {
            AppLogger.Log("ROUTE", $"Физический интерфейс: name='{physicalNet.Name}', ifIndex={physicalNet.IfIndex}, localIp={physicalNet.LocalIp}, gw={physicalNet.GatewayIp}");
        }

        var configJson = BuildXrayConfig(
            node,
            bypassRussia && hasGeodata,
            socksPort,
            httpPort,
            mode == VpnMode.Tun ? physicalNet?.LocalIp : null,
            _storage.Current.BypassExclusions);

        await File.WriteAllTextAsync(AppConfig.XrayRuntimeConfigPath, configJson, Utf8NoBom);
        AppLogger.Log("XRAY", $"xray-runtime.json:\n{AppLogger.MaskSecrets(configJson)}");

        var psi = new ProcessStartInfo
        {
            FileName = xrayExe,
            WorkingDirectory = AppConfig.CoreDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(AppConfig.XrayRuntimeConfigPath);
        psi.Environment["XRAY_LOCATION_ASSET"] = assetDir;

        _coreProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _coreProcess.OutputDataReceived += (_, e) => AppendLog("XRAY", e.Data);
        _coreProcess.ErrorDataReceived += (_, e) => AppendLog("XRAY", e.Data);
        _coreProcess.Exited += (_, _) =>
        {
            AppLogger.Log("XRAY", $"Процесс xray.exe завершился (ExitCode={_coreProcess?.ExitCode})");
            if (State is VpnConnectionState.Connected or VpnConnectionState.Connecting)
            {
                CleanupNetworkState();
                State = VpnConnectionState.Disconnected;
                _connectedAtUtc = null;
                StateChanged?.Invoke(State);
            }
        };

        if (!_coreProcess.Start())
        {
            LastError = "Не удалось запустить xray.exe";
            return false;
        }

        try { _coreProcess.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        _coreProcess.BeginOutputReadLine();
        _coreProcess.BeginErrorReadLine();

        var listening = await WaitForLocalPortAsync(socksPort, TimeSpan.FromSeconds(4));
        if (_coreProcess.HasExited)
        {
            var errDetails = GetRecentErrorSummary();
            LastError = string.IsNullOrWhiteSpace(errDetails)
                ? $"Xray завершился с кодом {_coreProcess.ExitCode}"
                : $"Xray ({_coreProcess.ExitCode}): {errDetails}";
            return false;
        }
        if (!listening)
        {
            LastError = $"Xray не открыл локальный порт {socksPort}.";
            return false;
        }

        if (mode == VpnMode.SystemProxy)
        {
            SystemProxyService.EnableSystemProxy("127.0.0.1", httpPort);
            AppLogger.Log("XRAY", $"Включён системный прокси 127.0.0.1:{httpPort}");
        }
        else if (hevExe is not null)
        {
            return await StartTunBridgeAsync(hevExe, node.Host, socksPort, physicalNet);
        }

        return true;
    }

    private async Task<bool> StartTunBridgeAsync(string hevExe, string serverHost, int socksPort, PhysicalNetInfo? physicalNet)
    {
        var serverIp = await ResolveIpv4Async(serverHost);
        AppLogger.Log("ROUTE", $"Резолв сервера '{serverHost}' -> {serverIp ?? "не удалось"}");

        if (!string.IsNullOrWhiteSpace(serverIp) && physicalNet is not null)
        {
            _bypassServerIp = serverIp;
            await RunNetToolLoggedAsync("route", [
                "add", serverIp, "mask", "255.255.255.255",
                physicalNet.GatewayIp, "metric", "1", "if", physicalNet.IfIndex.ToString()
            ]);
        }

        var yaml = $"""
            tunnel:
              name: k2p-tun
              mtu: 1500
              multi-queue: false
              ipv4: 198.18.0.1
            socks5:
              port: {socksPort}
              address: 127.0.0.1
              udp: 'udp'
            """;
        await File.WriteAllTextAsync(AppConfig.HevTunConfigPath, yaml, Utf8NoBom);

        var tunPsi = new ProcessStartInfo
        {
            FileName = hevExe,
            WorkingDirectory = AppConfig.CoreDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        tunPsi.ArgumentList.Add(AppConfig.HevTunConfigPath);

        _tunProcess = new Process { StartInfo = tunPsi, EnableRaisingEvents = true };
        _tunProcess.OutputDataReceived += (_, e) => AppendLog("TUN", e.Data);
        _tunProcess.ErrorDataReceived += (_, e) => AppendLog("TUN", e.Data);

        if (!_tunProcess.Start())
        {
            LastError = "Не удалось запустить hev-socks5-tunnel";
            return false;
        }

        try { _tunProcess.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        _tunProcess.BeginOutputReadLine();
        _tunProcess.BeginErrorReadLine();

        await Task.Delay(900);
        if (_tunProcess.HasExited)
        {
            var err = GetRecentErrorSummary();
            LastError = string.IsNullOrWhiteSpace(err)
                ? $"TUN-мост завершился с кодом {_tunProcess.ExitCode}"
                : $"TUN-мост ({_tunProcess.ExitCode}): {err}";
            return false;
        }

        await RunNetToolLoggedAsync("netsh", ["interface", "ip", "set", "address", "name=k2p-tun", "static", "198.18.0.1", "255.254.0.0"]);
        await RunNetToolLoggedAsync("netsh", ["interface", "ip", "set", "dns", "name=k2p-tun", "static", "1.1.1.1"]);
        await RunNetToolLoggedAsync("netsh", ["interface", "ip", "add", "dns", "name=k2p-tun", "8.8.8.8", "index=2"]);

        int? tunIfIndex = null;
        for (var i = 0; i < 6; i++)
        {
            tunIfIndex = GetInterfaceIndex("k2p-tun");
            if (tunIfIndex.HasValue) break;
            await Task.Delay(250);
        }

        if (tunIfIndex.HasValue)
        {
            var idxStr = tunIfIndex.Value.ToString();
            await RunNetToolLoggedAsync("route", ["add", "0.0.0.0", "mask", "128.0.0.0", "198.18.0.2", "if", idxStr, "metric", "5"]);
            await RunNetToolLoggedAsync("route", ["add", "128.0.0.0", "mask", "128.0.0.0", "198.18.0.2", "if", idxStr, "metric", "5"]);
        }

        return true;
    }

    public async Task StopAsync()
    {
        if (State == VpnConnectionState.Disconnected && _coreProcess is null && _tunProcess is null)
        {
            CleanupNetworkState();
            return;
        }

        AppLogger.Log("APP", "Остановка VPN...");
        State = VpnConnectionState.Disconnecting;
        StateChanged?.Invoke(State);

        try
        {
            CleanupNetworkState();

            if (_tunProcess is not null)
            {
                if (!_tunProcess.HasExited)
                {
                    _tunProcess.Kill(entireProcessTree: true);
                    await _tunProcess.WaitForExitAsync();
                }
                await Task.Delay(250);
            }

            if (_coreProcess is not null && !_coreProcess.HasExited)
            {
                _coreProcess.Kill(entireProcessTree: true);
                await _coreProcess.WaitForExitAsync();
            }
        }
        catch
        {
            // Ignore process kill errors
        }
        finally
        {
            _tunProcess?.Dispose();
            _tunProcess = null;
            _coreProcess?.Dispose();
            _coreProcess = null;
            _activeMode = null;
            _activeCore = null;
            _connectedAtUtc = null;
            CurrentSocksPort = 0;
            CurrentHttpPort = 0;
            CleanupStaleTunProfiles();
            State = VpnConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            AppLogger.Log("APP", "VPN остановлен.");
        }
    }

    private void CleanupNetworkState()
    {
        SystemProxyService.DisableSystemProxy();

        if (_activeCore == AppVpnCore.Xray && (_activeMode == VpnMode.Tun || !string.IsNullOrWhiteSpace(_bypassServerIp)))
        {
            try
            {
                RunNetToolSync("route", ["delete", "0.0.0.0", "mask", "128.0.0.0", "198.18.0.2"]);
                RunNetToolSync("route", ["delete", "128.0.0.0", "mask", "128.0.0.0", "198.18.0.2"]);
                if (!string.IsNullOrWhiteSpace(_bypassServerIp))
                {
                    RunNetToolSync("route", ["delete", _bypassServerIp]);
                    _bypassServerIp = null;
                }
            }
            catch
            {
                // Best effort route cleanup
            }
        }
    }

    private static void CleanupStaleTunProfiles()
    {
        try
        {
            const string profilesPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles";
            using var profilesKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(profilesPath, writable: true);
            if (profilesKey is null) return;

            foreach (var subKeyName in profilesKey.GetSubKeyNames())
            {
                using var sub = profilesKey.OpenSubKey(subKeyName, writable: false);
                var name = sub?.GetValue("ProfileName")?.ToString();
                if (!string.IsNullOrWhiteSpace(name) &&
                    name.StartsWith("k2p-tun", StringComparison.OrdinalIgnoreCase))
                {
                    profilesKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                }
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private static async Task KillStaleCoreProcessesAsync()
    {
        var coreFullDir = Path.GetFullPath(AppConfig.CoreDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var killedTun = false;
        foreach (var name in new[] { "hev-socks5-tunnel-win64", "sing-box", "xray" })
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        var path = proc.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path) &&
                            path.StartsWith(coreFullDir, StringComparison.OrdinalIgnoreCase))
                        {
                            AppLogger.Log("APP", $"Завершён зависший процесс {name} (PID={proc.Id})");
                            proc.Kill(entireProcessTree: true);
                            proc.WaitForExit(2000);
                            if (name is "hev-socks5-tunnel-win64" or "sing-box") killedTun = true;
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
            }
            catch { }
        }
        if (killedTun) await Task.Delay(300);
    }

    public static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> WaitForLocalPortAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return true;
            }
            catch { await Task.Delay(120); }
        }
        return false;
    }

    private sealed record PhysicalNetInfo(string Name, string LocalIp, string GatewayIp, int IfIndex);

    private static PhysicalNetInfo? GetActivePhysicalInterfaceInfo()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (nic.Name.Contains("k2p-tun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)) continue;

                var ipProps = nic.GetIPProperties();
                var gw = ipProps.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address))
                    ?.Address.ToString();
                if (string.IsNullOrWhiteSpace(gw)) continue;

                var localIp = ipProps.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address))
                    ?.Address.ToString();
                var ifIndex = ipProps.GetIPv4Properties()?.Index;

                if (!string.IsNullOrWhiteSpace(localIp) && ifIndex.HasValue)
                    return new PhysicalNetInfo(nic.Name, localIp, gw, ifIndex.Value);
            }
        }
        catch { }
        return null;
    }

    private static string? ValidateNode(VpnServerNode node)
    {
        if (string.IsNullOrWhiteSpace(node.Host) || node.Host.Any(char.IsControl))
            return "Некорректный адрес сервера в подписке.";
        if (node.Port is < 1 or > 65535)
            return $"Некорректный порт сервера: {node.Port}.";
        if (string.IsNullOrWhiteSpace(node.UuidOrPassword))
            return "Отсутствует UUID / пароль в конфигурации сервера.";

        if (node.Protocol.Equals("vless", StringComparison.OrdinalIgnoreCase))
        {
            var sec = node.Security?.ToLowerInvariant() ?? (!string.IsNullOrWhiteSpace(node.Pbk) ? "reality" : "tls");
            if (sec == "reality" && string.IsNullOrWhiteSpace(node.Pbk))
                return "Некорректная VLESS Reality ссылка: отсутствует параметр pbk.";
        }
        return null;
    }

    private void AppendLog(string category, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var trimmed = line.Trim();
        AppLogger.Log(category, trimmed);
        lock (_logLock)
        {
            _recentLogs.Add(trimmed);
            if (_recentLogs.Count > 30) _recentLogs.RemoveAt(0);
        }
    }

    private string GetRecentErrorSummary()
    {
        lock (_logLock)
        {
            var errLine = _recentLogs.LastOrDefault(l =>
                l.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("fatal", StringComparison.OrdinalIgnoreCase));
            return errLine ?? _recentLogs.LastOrDefault() ?? string.Empty;
        }
    }

    private static async Task<string?> ResolveIpv4Async(string host)
    {
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            return ip.ToString();
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host);
            return addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch { return null; }
    }

    private static int? GetInterfaceIndex(string interfaceName)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase));
            return nic?.GetIPProperties().GetIPv4Properties()?.Index;
        }
        catch { return null; }
    }

    private static async Task RunNetToolLoggedAsync(string exe, IEnumerable<string> args)
    {
        var argsList = args.ToList();
        var cmdDisplay = $"{exe} {string.Join(' ', argsList)}";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in argsList) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return;
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var output = $"{stdout} {stderr}".Trim();
            AppLogger.Log("ROUTE", $"{cmdDisplay} -> Exit={p.ExitCode}{(string.IsNullOrWhiteSpace(output) ? "" : $" | {output}")}");
        }
        catch (Exception ex)
        {
            AppLogger.Log("ROUTE", $"{cmdDisplay} -> Error: {ex.Message}");
        }
    }

    private static void RunNetToolSync(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        p?.WaitForExit(1500);
    }

    public TimeSpan GetSessionDuration() =>
        _connectedAtUtc.HasValue ? DateTime.UtcNow - _connectedAtUtc.Value : TimeSpan.Zero;

    public (double DownBytesPerSec, double UpBytesPerSec) SampleSpeed()
    {
        if (State != VpnConnectionState.Connected) return (0, 0);

        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            var target = _activeMode == VpnMode.Tun
                ? nics.FirstOrDefault(n => n.Name.Contains("k2p-tun", StringComparison.OrdinalIgnoreCase) && n.OperationalStatus == OperationalStatus.Up)
                : nics.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            if (target is null) return (0, 0);

            var stats = target.GetIPStatistics();
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastSampleUtc).TotalSeconds;
            if (elapsed <= 0.1 || _lastSampleUtc == default)
            {
                _lastRxBytes = stats.BytesReceived;
                _lastTxBytes = stats.BytesSent;
                _lastSampleUtc = now;
                return (0, 0);
            }

            var down = Math.Max(0, stats.BytesReceived - _lastRxBytes) / elapsed;
            var up = Math.Max(0, stats.BytesSent - _lastTxBytes) / elapsed;

            _lastRxBytes = stats.BytesReceived;
            _lastTxBytes = stats.BytesSent;
            _lastSampleUtc = now;
            return (down, up);
        }
        catch { return (0, 0); }
    }

    public static async Task<int?> MeasureTcpPingAsync(string host, int port, int timeoutMs = 2500)
    {
        try
        {
            using var tcp = new TcpClient();
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(timeoutMs);
            await tcp.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch { return null; }
    }

    public static string ResolveGeodataDirectory()
    {
        var otaDir = AppConfig.GeodataOverrideDir;
        return HasValidGeodata(otaDir) ? otaDir : AppConfig.CoreDir;
    }

    public static bool HasValidGeodata(string dir)
    {
        var geoip = Path.Combine(dir, "geoip.dat");
        var geosite = Path.Combine(dir, "geosite.dat");
        return File.Exists(geoip) && new FileInfo(geoip).Length > 50 * 1024 &&
               File.Exists(geosite) && new FileInfo(geosite).Length > 50 * 1024;
    }

    public static string BuildXrayConfig(
        VpnServerNode node,
        bool bypassRussia,
        int socksPort,
        int httpPort,
        string? physicalLocalIp = null,
        IReadOnlyList<string>? exclusions = null)
    {
        var dnsServers = new JsonArray();
        if (bypassRussia)
        {
            dnsServers.Add(new JsonObject
            {
                ["address"] = "77.88.8.8",
                ["domains"] = new JsonArray(
                    "geosite:ru-available-only-inside",
                    "domain:ru",
                    "domain:su",
                    "domain:xn--p1ai"
                ),
                ["skipFallback"] = true
            });
        }
        dnsServers.Add("tcp://1.1.1.1");
        dnsServers.Add("tcp://8.8.8.8");

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "info" },
            ["dns"] = new JsonObject
            {
                ["tag"] = "dns-module",
                ["servers"] = dnsServers
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["tag"] = "socks-in",
                    ["listen"] = "127.0.0.1",
                    ["port"] = socksPort,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true },
                    ["sniffing"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new JsonArray("http", "tls", "quic")
                    }
                },
                new JsonObject
                {
                    ["tag"] = "http-in",
                    ["listen"] = "127.0.0.1",
                    ["port"] = httpPort,
                    ["protocol"] = "http",
                    ["sniffing"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new JsonArray("http", "tls", "quic")
                    }
                }
            }
        };

        var proxyOutbound = node.Protocol.Equals("hysteria", StringComparison.OrdinalIgnoreCase) ||
                            node.Protocol.Equals("hysteria2", StringComparison.OrdinalIgnoreCase) ||
                            node.Protocol.Equals("hy2", StringComparison.OrdinalIgnoreCase)
            ? BuildHysteria2Outbound(node)
            : BuildVlessOutbound(node);

        var directOutbound = new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" };
        var dnsOutbound = new JsonObject { ["tag"] = "dns-out", ["protocol"] = "dns" };

        if (!string.IsNullOrWhiteSpace(physicalLocalIp))
        {
            proxyOutbound["sendThrough"] = physicalLocalIp;
            directOutbound["sendThrough"] = physicalLocalIp;
        }

        root["outbounds"] = new JsonArray
        {
            proxyOutbound,
            directOutbound,
            dnsOutbound,
            new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" }
        };

        var rules = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "field",
                ["ip"] = new JsonArray("::/0"),
                ["outboundTag"] = "block"
            }
        };

        if (bypassRussia)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray("dns-module"),
                ["ip"] = new JsonArray("77.88.8.8/32"),
                ["outboundTag"] = "direct"
            });
        }

        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["inboundTag"] = new JsonArray("dns-module"),
            ["outboundTag"] = "proxy"
        });

        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["port"] = 53,
            ["network"] = "udp,tcp",
            ["outboundTag"] = "dns-out"
        });

        if (exclusions is { Count: > 0 })
        {
            var customIps = new JsonArray();
            var customDomains = new JsonArray();
            foreach (var raw in exclusions)
            {
                var item = raw.Trim();
                if (string.IsNullOrWhiteSpace(item)) continue;
                var ipPart = item.Split('/')[0];
                if (IPAddress.TryParse(ipPart, out _))
                    customIps.Add(item);
                else
                    customDomains.Add(item.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) ? item : $"domain:{item}");
            }
            if (customIps.Count > 0)
                rules.Add(new JsonObject { ["type"] = "field", ["ip"] = customIps, ["outboundTag"] = "direct" });
            if (customDomains.Count > 0)
                rules.Add(new JsonObject { ["type"] = "field", ["domain"] = customDomains, ["outboundTag"] = "direct" });
        }

        if (bypassRussia)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["ip"] = new JsonArray("geoip:private", "geoip:ru"),
                ["outboundTag"] = "direct"
            });
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["domain"] = new JsonArray(
                    "geosite:ru-available-only-inside",
                    "domain:ru",
                    "domain:su",
                    "domain:xn--p1ai"
                ),
                ["outboundTag"] = "direct"
            });
        }
        else
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["ip"] = new JsonArray("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8"),
                ["outboundTag"] = "direct"
            });
        }

        root["routing"] = new JsonObject
        {
            ["domainStrategy"] = "IPIfNonMatch",
            ["rules"] = rules
        };

        return root.ToJsonString(JsonOutputOptions);
    }

    private static JsonObject BuildVlessOutbound(VpnServerNode node)
    {
        var user = new JsonObject
        {
            ["id"] = node.UuidOrPassword,
            ["encryption"] = "none"
        };
        if (!string.IsNullOrWhiteSpace(node.Flow))
            user["flow"] = node.Flow;

        var rawNet = node.Network?.ToLowerInvariant();
        var network = string.IsNullOrWhiteSpace(rawNet) || rawNet == "splithttp" ? "xhttp" : rawNet;
        var security = string.IsNullOrWhiteSpace(node.Security)
            ? (!string.IsNullOrWhiteSpace(node.Pbk) ? "reality" : "tls")
            : node.Security.ToLowerInvariant();

        var stream = new JsonObject
        {
            ["network"] = network,
            ["security"] = security
        };

        switch (network)
        {
            case "xhttp":
                var xhttp = new JsonObject
                {
                    ["path"] = string.IsNullOrWhiteSpace(node.Path) ? "/" : SafeUnescape(node.Path),
                    ["host"] = string.IsNullOrWhiteSpace(node.HostHeader) ? "" : SafeUnescape(node.HostHeader),
                    ["mode"] = string.IsNullOrWhiteSpace(node.XhttpMode) ? "auto" : SafeUnescape(node.XhttpMode)
                };
                if (!string.IsNullOrWhiteSpace(node.XhttpExtra))
                {
                    try
                    {
                        var parsedExtra = JsonNode.Parse(SafeUnescape(node.XhttpExtra));
                        if (parsedExtra is not null) xhttp["extra"] = parsedExtra;
                    }
                    catch { }
                }
                stream["xhttpSettings"] = xhttp;
                break;
            case "ws":
                var ws = new JsonObject
                {
                    ["path"] = string.IsNullOrWhiteSpace(node.Path) ? "/" : SafeUnescape(node.Path)
                };
                if (!string.IsNullOrWhiteSpace(node.HostHeader))
                    ws["headers"] = new JsonObject { ["Host"] = SafeUnescape(node.HostHeader) };
                stream["wsSettings"] = ws;
                break;
            case "grpc":
                stream["grpcSettings"] = new JsonObject
                {
                    ["serviceName"] = SafeUnescape(node.ServiceName ?? node.Path ?? "")
                };
                break;
        }

        if (security == "reality")
        {
            stream["realitySettings"] = new JsonObject
            {
                ["serverName"] = string.IsNullOrWhiteSpace(node.Sni) ? node.Host : SafeUnescape(node.Sni),
                ["fingerprint"] = string.IsNullOrWhiteSpace(node.Fp) ? "chrome" : SafeUnescape(node.Fp),
                ["publicKey"] = SafeUnescape(node.Pbk ?? ""),
                ["shortId"] = SafeUnescape(node.Sid ?? ""),
                ["spiderX"] = string.IsNullOrWhiteSpace(node.Spx) ? "/" : SafeUnescape(node.Spx)
            };
        }
        else if (security == "tls")
        {
            stream["tlsSettings"] = new JsonObject
            {
                ["serverName"] = string.IsNullOrWhiteSpace(node.Sni) ? node.Host : SafeUnescape(node.Sni),
                ["fingerprint"] = string.IsNullOrWhiteSpace(node.Fp) ? "chrome" : SafeUnescape(node.Fp)
            };
        }

        return new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "vless",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = node.Host,
                        ["port"] = node.Port,
                        ["users"] = new JsonArray { user }
                    }
                }
            },
            ["streamSettings"] = stream
        };
    }

    private static string SafeUnescape(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch { return value; }
    }

    private static JsonObject BuildHysteria2Outbound(VpnServerNode node)
    {
        var stream = new JsonObject
        {
            ["network"] = "hysteria",
            ["hysteriaSettings"] = new JsonObject
            {
                ["version"] = 2,
                ["auth"] = node.UuidOrPassword
            },
            ["security"] = "tls",
            ["tlsSettings"] = new JsonObject
            {
                ["serverName"] = string.IsNullOrWhiteSpace(node.Sni) ? node.Host : SafeUnescape(node.Sni),
                ["allowInsecure"] = node.Insecure
            }
        };

        if (string.Equals(node.Obfs, "salamander", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(node.ObfsPassword))
        {
            stream["finalmask"] = new JsonObject
            {
                ["udp"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "salamander",
                        ["settings"] = new JsonObject { ["password"] = SafeUnescape(node.ObfsPassword) }
                    }
                }
            };
        }

        return new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "hysteria",
            ["settings"] = new JsonObject
            {
                ["version"] = 2,
                ["address"] = node.Host,
                ["port"] = node.Port
            },
            ["streamSettings"] = stream
        };
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
