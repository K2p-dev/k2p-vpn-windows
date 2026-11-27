using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
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

    private Process? _singBoxProcess;
    private Process? _xrayProcess;
    private VpnMode? _activeMode;
    private AppVpnCore? _activeCore;
    private VpnServerNode? _activeNode;
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

        var effectiveCore = ResolveEffectiveCore(node, preferredCore);
        var isCascadeTun = mode == VpnMode.Tun && effectiveCore == AppVpnCore.Xray;

        AppLogger.Log("APP", $"Запуск подключения: {node.Name} ({node.ProtocolLabel}), ядро={effectiveCore}, режим={mode}{(isCascadeTun ? " [Cascade singbox-tun -> Xray]" : "")}, обходРФ={bypassRussia}");

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

            bool ok;
            if (mode == VpnMode.Tun)
            {
                if (isCascadeTun)
                {
                    // Cascade: 1) Xray upstream SOCKS5 with random auth -> 2) sing-box native TUN (singbox-tun)
                    var bridgeUser = "k2p_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
                    var bridgePass = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

                    ok = await StartXrayCoreAsync(node, VpnMode.SystemProxy, bypassRussia, socksPort, httpPort, (bridgeUser, bridgePass), enableSystemProxy: false);
                    if (ok)
                    {
                        ok = await StartSingBoxCoreAsync(node, VpnMode.Tun, bypassRussia, 0, 0, (socksPort, bridgeUser, bridgePass));
                    }
                }
                else
                {
                    // Direct sing-box native TUN (singbox-tun)
                    ok = await StartSingBoxCoreAsync(node, VpnMode.Tun, bypassRussia, 0, 0);
                }
            }
            else
            {
                // System Proxy mode
                ok = effectiveCore == AppVpnCore.SingBox
                    ? await StartSingBoxCoreAsync(node, VpnMode.SystemProxy, bypassRussia, socksPort, httpPort)
                    : await StartXrayCoreAsync(node, VpnMode.SystemProxy, bypassRussia, socksPort, httpPort, upstreamAuth: null, enableSystemProxy: true);
            }

            if (!ok)
            {
                await StopAsync();
                return false;
            }

            _activeMode = mode;
            _activeCore = effectiveCore;
            _activeNode = node;
            _connectedAtUtc = DateTime.UtcNow;
            _lastRxBytes = 0;
            _lastTxBytes = 0;
            _lastSampleUtc = DateTime.UtcNow;

            State = VpnConnectionState.Connected;
            StateChanged?.Invoke(State);
            AppLogger.Log("APP", $"VPN успешно подключён (режим: {mode}, ядро: {effectiveCore}).");
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

    private async Task<bool> StartSingBoxCoreAsync(
        VpnServerNode node,
        VpnMode mode,
        bool bypassRussia,
        int socksPort,
        int httpPort,
        (int SocksPort, string Username, string Password)? upstreamSocks = null)
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

        var configJson = SingBoxConfigBuilder.Build(node, mode, bypassRussia, socksPort, httpPort, _storage.Current.BypassExclusions, upstreamSocks);
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

        _singBoxProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _singBoxProcess.OutputDataReceived += (_, e) => AppendLog("SING-BOX", e.Data);
        _singBoxProcess.ErrorDataReceived += (_, e) => AppendLog("SING-BOX", e.Data);
        _singBoxProcess.Exited += (_, _) =>
        {
            AppLogger.Log("SING-BOX", $"Процесс sing-box.exe завершился (ExitCode={_singBoxProcess?.ExitCode})");
            if (State is VpnConnectionState.Connected or VpnConnectionState.Connecting)
            {
                SystemProxyService.DisableSystemProxy();
                State = VpnConnectionState.Disconnected;
                _connectedAtUtc = null;
                StateChanged?.Invoke(State);
            }
        };

        if (!_singBoxProcess.Start())
        {
            LastError = "Не удалось запустить sing-box.exe";
            return false;
        }

        try { _singBoxProcess.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        _singBoxProcess.BeginOutputReadLine();
        _singBoxProcess.BeginErrorReadLine();

        if (mode == VpnMode.SystemProxy)
        {
            var listening = await WaitForLocalPortAsync(socksPort, TimeSpan.FromSeconds(4));
            if (_singBoxProcess.HasExited || !listening)
            {
                var err = GetRecentErrorSummary();
                LastError = string.IsNullOrWhiteSpace(err) ? $"sing-box завершился с кодом {_singBoxProcess.ExitCode}" : $"sing-box: {err}";
                return false;
            }
            SystemProxyService.EnableSystemProxy("127.0.0.1", httpPort);
            AppLogger.Log("SING-BOX", $"Включён системный прокси 127.0.0.1:{httpPort}");
        }
        else
        {
            await Task.Delay(1200);
            if (_singBoxProcess.HasExited)
            {
                var err = GetRecentErrorSummary();
                LastError = string.IsNullOrWhiteSpace(err) ? $"sing-box завершился с кодом {_singBoxProcess.ExitCode}" : $"sing-box: {err}";
                return false;
            }
        }

        return true;
    }

    private async Task<bool> StartXrayCoreAsync(
        VpnServerNode node,
        VpnMode mode,
        bool bypassRussia,
        int socksPort,
        int httpPort,
        (string Username, string Password)? upstreamAuth,
        bool enableSystemProxy)
    {
        var coreFullDir = Path.GetFullPath(AppConfig.CoreDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var xrayExe = Path.GetFullPath(Path.Combine(AppConfig.CoreDir, "xray.exe"));
        if (!xrayExe.StartsWith(coreFullDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(xrayExe))
        {
            LastError = "Не найден доверенный бинарник Core/xray.exe";
            AppLogger.Log("XRAY", LastError);
            return false;
        }

        var assetDir = ResolveGeodataDirectory();
        var hasGeodata = HasValidGeodata(assetDir);
        var physicalLocalIp = upstreamAuth.HasValue ? GetActivePhysicalInterfaceIp() : null;
        if (!string.IsNullOrWhiteSpace(physicalLocalIp))
        {
            AppLogger.Log("XRAY", $"Физический IP для sendThrough: {physicalLocalIp}");
        }

        var configJson = BuildXrayConfig(
            node,
            bypassRussia && hasGeodata,
            socksPort,
            enableSystemProxy ? httpPort : 0,
            upstreamAuth,
            _storage.Current.BypassExclusions,
            physicalLocalIp);

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

        _xrayProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _xrayProcess.OutputDataReceived += (_, e) => AppendLog("XRAY", e.Data);
        _xrayProcess.ErrorDataReceived += (_, e) => AppendLog("XRAY", e.Data);
        _xrayProcess.Exited += (_, _) =>
        {
            AppLogger.Log("XRAY", $"Процесс xray.exe завершился (ExitCode={_xrayProcess?.ExitCode})");
            if (State is VpnConnectionState.Connected or VpnConnectionState.Connecting)
            {
                SystemProxyService.DisableSystemProxy();
                State = VpnConnectionState.Disconnected;
                _connectedAtUtc = null;
                StateChanged?.Invoke(State);
            }
        };

        if (!_xrayProcess.Start())
        {
            LastError = "Не удалось запустить xray.exe";
            return false;
        }

        try { _xrayProcess.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        _xrayProcess.BeginOutputReadLine();
        _xrayProcess.BeginErrorReadLine();

        var listening = await WaitForLocalPortAsync(socksPort, TimeSpan.FromSeconds(4));
        if (_xrayProcess.HasExited)
        {
            var errDetails = GetRecentErrorSummary();
            LastError = string.IsNullOrWhiteSpace(errDetails)
                ? $"Xray завершился с кодом {_xrayProcess.ExitCode}"
                : $"Xray ({_xrayProcess.ExitCode}): {errDetails}";
            return false;
        }
        if (!listening)
        {
            LastError = $"Xray не открыл локальный порт {socksPort}.";
            return false;
        }

        if (enableSystemProxy)
        {
            SystemProxyService.EnableSystemProxy("127.0.0.1", httpPort);
            AppLogger.Log("XRAY", $"Включён системный прокси 127.0.0.1:{httpPort}");
        }

        return true;
    }

    public async Task StopAsync()
    {
        if (State == VpnConnectionState.Disconnected && _singBoxProcess is null && _xrayProcess is null)
        {
            SystemProxyService.DisableSystemProxy();
            return;
        }

        AppLogger.Log("APP", "Остановка VPN...");
        State = VpnConnectionState.Disconnecting;
        StateChanged?.Invoke(State);

        try
        {
            SystemProxyService.DisableSystemProxy();

            if (_singBoxProcess is not null)
            {
                if (!_singBoxProcess.HasExited)
                {
                    _singBoxProcess.Kill(entireProcessTree: true);
                    await _singBoxProcess.WaitForExitAsync();
                }
            }

            if (_xrayProcess is not null)
            {
                if (!_xrayProcess.HasExited)
                {
                    _xrayProcess.Kill(entireProcessTree: true);
                    await _xrayProcess.WaitForExitAsync();
                }
            }
        }
        catch
        {
            // Ignore process kill errors
        }
        finally
        {
            _singBoxProcess?.Dispose();
            _singBoxProcess = null;
            _xrayProcess?.Dispose();
            _xrayProcess = null;
            _activeMode = null;
            _activeCore = null;
            _connectedAtUtc = null;
            CurrentSocksPort = 0;
            CurrentHttpPort = 0;
            State = VpnConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            AppLogger.Log("APP", "VPN остановлен.");
        }
    }

    public static async Task<int?> MeasureNodePingAsync(VpnServerNode node)
    {
        var proto = node.Protocol.ToLowerInvariant();
        if (proto is "hysteria" or "hysteria2" or "hy2")
        {
            return await MeasureHysteria2RealDelayAsync(node);
        }
        return await MeasureTcpPingAsync(node.Host, node.Port);
    }

    private static async Task<int?> MeasureHysteria2RealDelayAsync(VpnServerNode node)
    {
        var coreFullDir = Path.GetFullPath(AppConfig.CoreDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var sbExe = Path.GetFullPath(Path.Combine(AppConfig.CoreDir, "sing-box.exe"));
        if (!sbExe.StartsWith(coreFullDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(sbExe))
            return null;

        var pingPort = GetFreeLoopbackPort();
        var tempCfg = Path.Combine(AppConfig.AppDataDir, $"ping-{Guid.NewGuid():N}.json");
        Process? proc = null;

        try
        {
            Directory.CreateDirectory(AppConfig.AppDataDir);
            var json = SingBoxConfigBuilder.BuildForPing(node, pingPort);
            await File.WriteAllTextAsync(tempCfg, json, Utf8NoBom);

            var psi = new ProcessStartInfo
            {
                FileName = sbExe,
                WorkingDirectory = AppConfig.CoreDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(tempCfg);

            proc = Process.Start(psi);
            if (proc is null) return null;

            var ready = await WaitForLocalPortAsync(pingPort, TimeSpan.FromMilliseconds(1800));
            if (!ready || proc.HasExited) return null;

            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{pingPort}"),
                UseProxy = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(3500) };

            var sw = Stopwatch.StartNew();
            using var resp = await http.GetAsync("http://www.gstatic.com/generate_204");
            sw.Stop();

            var ms = (int)sw.ElapsedMilliseconds;
            AppLogger.Log("PING", $"Hysteria2 '{node.Name}' -> {ms} ms (HTTP {(int)resp.StatusCode})");
            return ms;
        }
        catch (Exception ex)
        {
            AppLogger.Log("PING", $"Hysteria2 '{node.Name}' -> timeout/err: {ex.Message}");
            return null;
        }
        finally
        {
            try
            {
                if (proc is not null && !proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(1000);
                }
                proc?.Dispose();
            }
            catch { }

            try { if (File.Exists(tempCfg)) File.Delete(tempCfg); } catch { }
        }
    }

    private static async Task KillStaleCoreProcessesAsync()
    {
        var coreFullDir = Path.GetFullPath(AppConfig.CoreDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var killedTun = false;
        foreach (var name in new[] { "sing-box", "xray", "hev-socks5-tunnel-win64" })
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
                            if (name is "sing-box" or "hev-socks5-tunnel-win64") killedTun = true;
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
            catch { await Task.Delay(100); }
        }
        return false;
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

    public TimeSpan GetSessionDuration() =>
        _connectedAtUtc.HasValue ? DateTime.UtcNow - _connectedAtUtc.Value : TimeSpan.Zero;

    public (double DownBytesPerSec, double UpBytesPerSec) SampleSpeed()
    {
        if (State != VpnConnectionState.Connected) return (0, 0);

        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            var target = _activeMode == VpnMode.Tun
                ? nics.FirstOrDefault(n => (n.Name.Contains("singbox-tun", StringComparison.OrdinalIgnoreCase) || n.Name.Contains("k2p-tun", StringComparison.OrdinalIgnoreCase)) && n.OperationalStatus == OperationalStatus.Up)
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

    private static string? GetActivePhysicalInterfaceIp()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (nic.Name.Contains("singbox-tun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Name.Contains("k2p-tun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)) continue;

                var ipProps = nic.GetIPProperties();
                var hasGw = ipProps.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address));
                if (!hasGw) continue;

                var localIp = ipProps.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address))
                    ?.Address.ToString();
                if (!string.IsNullOrWhiteSpace(localIp)) return localIp;
            }
        }
        catch { }
        return null;
    }

    public static string BuildXrayConfig(
        VpnServerNode node,
        bool bypassRussia,
        int socksPort,
        int httpPort,
        (string Username, string Password)? upstreamAuth = null,
        IReadOnlyList<string>? exclusions = null,
        string? physicalLocalIp = null)
    {
        var socksSettings = new JsonObject
        {
            ["udp"] = true
        };
        if (upstreamAuth.HasValue)
        {
            socksSettings["auth"] = "password";
            socksSettings["accounts"] = new JsonArray
            {
                new JsonObject
                {
                    ["user"] = upstreamAuth.Value.Username,
                    ["pass"] = upstreamAuth.Value.Password
                }
            };
        }
        else
        {
            socksSettings["auth"] = "noauth";
        }

        var inbounds = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "socks-in",
                ["listen"] = "127.0.0.1",
                ["port"] = socksPort,
                ["protocol"] = "socks",
                ["settings"] = socksSettings,
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["destOverride"] = new JsonArray("http", "tls", "quic")
                }
            }
        };

        if (httpPort > 0)
        {
            inbounds.Add(new JsonObject
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
            });
        }

        var proxyOutbound = node.Protocol.Equals("hysteria", StringComparison.OrdinalIgnoreCase) ||
                            node.Protocol.Equals("hysteria2", StringComparison.OrdinalIgnoreCase) ||
                            node.Protocol.Equals("hy2", StringComparison.OrdinalIgnoreCase)
            ? BuildHysteria2Outbound(node)
            : BuildVlessOutbound(node);

        var directOutbound = new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" };
        if (!string.IsNullOrWhiteSpace(physicalLocalIp))
        {
            proxyOutbound["sendThrough"] = physicalLocalIp;
            directOutbound["sendThrough"] = physicalLocalIp;
        }

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "info" },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray("77.88.8.8", "1.1.1.1")
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = new JsonArray
            {
                proxyOutbound,
                directOutbound,
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" }
            }
        };

        // When httpPort == 0 (cascade upstream), sing-box handles DNS/routing; Xray only forwards proxy traffic
        if (httpPort > 0)
        {
            var rules = new JsonArray();
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

            root["routing"] = new JsonObject
            {
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = rules
            };
        }

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
