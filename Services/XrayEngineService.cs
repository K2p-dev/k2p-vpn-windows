using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using K2pVpnWindows.Config;
using K2pVpnWindows.Models;

namespace K2pVpnWindows.Services;

public sealed class XrayEngineService : IDisposable
{
    private readonly SettingsStorageService _storage;
    private Process? _xrayProcess;
    private VpnMode? _activeMode;
    private VpnServerNode? _activeNode;
    private DateTime? _connectedAtUtc;
    private long _lastRxBytes;
    private long _lastTxBytes;
    private DateTime _lastSampleUtc;

    public VpnConnectionState State { get; private set; } = VpnConnectionState.Disconnected;
    public VpnServerNode? ActiveNode => _activeNode;
    public string? LastError { get; private set; }

    public event Action<VpnConnectionState>? StateChanged;

    public XrayEngineService(SettingsStorageService storage)
    {
        _storage = storage;
    }

    public async Task<bool> StartAsync(VpnServerNode node, VpnMode mode, bool bypassRussia)
    {
        await StopAsync();

        var coreFullDir = Path.GetFullPath(AppConfig.CoreDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var xrayExe = Path.GetFullPath(Path.Combine(AppConfig.CoreDir, "xray.exe"));

        // Verify binary resides strictly inside trusted Core directory
        if (!xrayExe.StartsWith(coreFullDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(xrayExe))
        {
            LastError = "Не найден доверенный бинарник Core/xray.exe";
            return false;
        }

        if (mode == VpnMode.Tun)
        {
            var wintunDll = Path.GetFullPath(Path.Combine(AppConfig.CoreDir, "wintun.dll"));
            if (!wintunDll.StartsWith(coreFullDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(wintunDll))
            {
                LastError = "Не найден доверенный Core/wintun.dll для TUN-режима";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(node.Host) || node.Port is < 1 or > 65535)
        {
            LastError = "Некорректные параметры сервера";
            return false;
        }

        State = VpnConnectionState.Connecting;
        StateChanged?.Invoke(State);
        LastError = null;

        try
        {
            Directory.CreateDirectory(AppConfig.AppDataDir);
            var assetDir = ResolveGeodataDirectory();
            var hasGeodata = HasValidGeodata(assetDir);
            var exclusions = _storage.Current.BypassExclusions;
            var configJson = BuildXrayConfig(node, mode, bypassRussia && hasGeodata, exclusions);
            await File.WriteAllTextAsync(AppConfig.XrayRuntimeConfigPath, configJson, Encoding.UTF8);

            // Command-injection-safe ProcessStartInfo using ArgumentList
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
            _xrayProcess.OutputDataReceived += (_, _) => { };
            _xrayProcess.ErrorDataReceived += (_, _) => { };
            _xrayProcess.Exited += (_, _) =>
            {
                if (State is VpnConnectionState.Connected or VpnConnectionState.Connecting)
                {
                    if (_activeMode == VpnMode.SystemProxy)
                        SystemProxyService.DisableSystemProxy();
                    State = VpnConnectionState.Disconnected;
                    _connectedAtUtc = null;
                    StateChanged?.Invoke(State);
                }
            };

            if (!_xrayProcess.Start())
            {
                LastError = "Не удалось запустить xray.exe";
                State = VpnConnectionState.Disconnected;
                StateChanged?.Invoke(State);
                return false;
            }

            try
            {
                _xrayProcess.PriorityClass = ProcessPriorityClass.AboveNormal;
            }
            catch
            {
                // Ignore if OS restricts priority adjustment
            }

            _xrayProcess.BeginOutputReadLine();
            _xrayProcess.BeginErrorReadLine();

            await Task.Delay(700);
            if (_xrayProcess.HasExited)
            {
                LastError = $"Xray завершился с кодом {_xrayProcess.ExitCode}";
                State = VpnConnectionState.Disconnected;
                StateChanged?.Invoke(State);
                return false;
            }

            if (mode == VpnMode.SystemProxy)
            {
                SystemProxyService.EnableSystemProxy("127.0.0.1", 10809);
            }

            _activeMode = mode;
            _activeNode = node;
            _connectedAtUtc = DateTime.UtcNow;
            _lastRxBytes = 0;
            _lastTxBytes = 0;
            _lastSampleUtc = DateTime.UtcNow;

            State = VpnConnectionState.Connected;
            StateChanged?.Invoke(State);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            await StopAsync();
            return false;
        }
    }

    public async Task StopAsync()
    {
        if (State == VpnConnectionState.Disconnected && _xrayProcess is null)
        {
            SystemProxyService.DisableSystemProxy();
            return;
        }

        State = VpnConnectionState.Disconnecting;
        StateChanged?.Invoke(State);

        try
        {
            if (_activeMode == VpnMode.SystemProxy)
            {
                SystemProxyService.DisableSystemProxy();
            }

            if (_xrayProcess is not null && !_xrayProcess.HasExited)
            {
                _xrayProcess.Kill(entireProcessTree: true);
                await _xrayProcess.WaitForExitAsync();
            }
        }
        catch
        {
            // Ignore process kill errors
        }
        finally
        {
            _xrayProcess?.Dispose();
            _xrayProcess = null;
            _activeMode = null;
            _connectedAtUtc = null;
            State = VpnConnectionState.Disconnected;
            StateChanged?.Invoke(State);
        }
    }

    public TimeSpan GetSessionDuration() =>
        _connectedAtUtc.HasValue ? DateTime.UtcNow - _connectedAtUtc.Value : TimeSpan.Zero;

    public (double DownBytesPerSec, double UpBytesPerSec) SampleSpeed()
    {
        if (State != VpnConnectionState.Connected)
            return (0, 0);

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
        catch
        {
            return (0, 0);
        }
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
        catch
        {
            return null;
        }
    }

    public static string ResolveGeodataDirectory()
    {
        var otaDir = AppConfig.GeodataOverrideDir;
        if (HasValidGeodata(otaDir))
            return otaDir;
        return AppConfig.CoreDir;
    }

    public static bool HasValidGeodata(string dir)
    {
        var geoip = Path.Combine(dir, "geoip.dat");
        var geosite = Path.Combine(dir, "geosite.dat");
        return File.Exists(geoip) && new FileInfo(geoip).Length > 50 * 1024 &&
               File.Exists(geosite) && new FileInfo(geosite).Length > 50 * 1024;
    }

    public static string BuildXrayConfig(VpnServerNode node, VpnMode mode, bool bypassRussia, IReadOnlyList<string>? exclusions = null)
    {
        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray("1.1.1.1", "8.8.8.8")
            }
        };

        var inbounds = new JsonArray();
        if (mode == VpnMode.Tun)
        {
            inbounds.Add(new JsonObject
            {
                ["tag"] = "tun-in",
                ["protocol"] = "tun",
                ["settings"] = new JsonObject
                {
                    ["interfaceName"] = "k2p-tun",
                    ["mtu"] = 1500,
                    ["autoRoute"] = true,
                    ["stack"] = "system"
                },
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["destOverride"] = new JsonArray("http", "tls", "quic"),
                    ["routeOnly"] = true
                }
            });
        }
        else
        {
            // Strictly bind to 127.0.0.1 (never 0.0.0.0)
            inbounds.Add(new JsonObject
            {
                ["tag"] = "socks-in",
                ["listen"] = "127.0.0.1",
                ["port"] = 10808,
                ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true },
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["destOverride"] = new JsonArray("http", "tls", "quic")
                }
            });
            inbounds.Add(new JsonObject
            {
                ["tag"] = "http-in",
                ["listen"] = "127.0.0.1",
                ["port"] = 10809,
                ["protocol"] = "http",
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["destOverride"] = new JsonArray("http", "tls", "quic")
                }
            });
        }
        root["inbounds"] = inbounds;

        var proxyOutbound = node.Protocol.Equals("hysteria", StringComparison.OrdinalIgnoreCase)
            ? BuildHysteria2Outbound(node)
            : BuildVlessOutbound(node);

        root["outbounds"] = new JsonArray
        {
            proxyOutbound,
            new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
            new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" }
        };

        var rules = new JsonArray();

        // User-defined exclusions (domains and IPs/CIDRs)
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
            {
                rules.Add(new JsonObject { ["type"] = "field", ["ip"] = customIps, ["outboundTag"] = "direct" });
            }
            if (customDomains.Count > 0)
            {
                rules.Add(new JsonObject { ["type"] = "field", ["domain"] = customDomains, ["outboundTag"] = "direct" });
            }
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

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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

        var network = string.IsNullOrWhiteSpace(node.Network) ? "xhttp" : node.Network.ToLowerInvariant();
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
                    ["path"] = string.IsNullOrWhiteSpace(node.Path) ? "/" : Uri.UnescapeDataString(node.Path)
                };
                if (!string.IsNullOrWhiteSpace(node.HostHeader)) xhttp["host"] = node.HostHeader;
                if (!string.IsNullOrWhiteSpace(node.XhttpMode)) xhttp["mode"] = node.XhttpMode;
                if (!string.IsNullOrWhiteSpace(node.XhttpExtra))
                {
                    try { xhttp["extra"] = JsonNode.Parse(Uri.UnescapeDataString(node.XhttpExtra)); } catch { }
                }
                stream["xhttpSettings"] = xhttp;
                break;
            case "ws":
                var ws = new JsonObject
                {
                    ["path"] = string.IsNullOrWhiteSpace(node.Path) ? "/" : Uri.UnescapeDataString(node.Path)
                };
                if (!string.IsNullOrWhiteSpace(node.HostHeader))
                    ws["headers"] = new JsonObject { ["Host"] = node.HostHeader };
                stream["wsSettings"] = ws;
                break;
            case "grpc":
                stream["grpcSettings"] = new JsonObject
                {
                    ["serviceName"] = node.ServiceName ?? node.Path ?? ""
                };
                break;
        }

        if (security == "reality")
        {
            stream["realitySettings"] = new JsonObject
            {
                ["serverName"] = string.IsNullOrWhiteSpace(node.Sni) ? node.Host : node.Sni,
                ["fingerprint"] = string.IsNullOrWhiteSpace(node.Fp) ? "chrome" : node.Fp,
                ["publicKey"] = node.Pbk ?? "",
                ["shortId"] = node.Sid ?? "",
                ["spiderX"] = string.IsNullOrWhiteSpace(node.Spx) ? "/" : Uri.UnescapeDataString(node.Spx)
            };
        }
        else if (security == "tls")
        {
            stream["tlsSettings"] = new JsonObject
            {
                ["serverName"] = string.IsNullOrWhiteSpace(node.Sni) ? node.Host : node.Sni,
                ["fingerprint"] = string.IsNullOrWhiteSpace(node.Fp) ? "chrome" : node.Fp
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
                ["serverName"] = string.IsNullOrWhiteSpace(node.Sni) ? node.Host : node.Sni,
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
                        ["settings"] = new JsonObject { ["password"] = node.ObfsPassword }
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
