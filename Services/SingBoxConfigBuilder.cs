using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using K2pVpnWindows.Models;

namespace K2pVpnWindows.Services;

public static class SingBoxConfigBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    public static string Build(
        VpnServerNode node,
        VpnMode mode,
        bool bypassRussia,
        int socksPort,
        int httpPort,
        IReadOnlyList<string>? exclusions = null,
        (int SocksPort, string Username, string Password)? upstreamSocks = null)
    {
        var dnsServers = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "dns-remote",
                ["type"] = "tcp",
                ["server"] = "1.1.1.1",
                ["detour"] = "proxy"
            }
        };

        if (bypassRussia)
        {
            dnsServers.Add(new JsonObject
            {
                ["tag"] = "dns-local",
                ["type"] = "udp",
                ["server"] = "77.88.8.8"
            });
        }

        var dnsRules = new JsonArray();
        if (bypassRussia)
        {
            dnsRules.Add(new JsonObject
            {
                ["domain_suffix"] = new JsonArray(".ru", ".su", ".xn--p1ai"),
                ["server"] = "dns-local"
            });
        }

        var inbounds = new JsonArray();
        if (mode == VpnMode.Tun)
        {
            inbounds.Add(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = "singbox-tun",
                ["address"] = new JsonArray("172.19.0.1/30"),
                ["mtu"] = 1500,
                ["auto_route"] = true,
                ["strict_route"] = true,
                ["stack"] = "system"
            });
        }
        else
        {
            // Strictly bind to 127.0.0.1 (never 0.0.0.0)
            inbounds.Add(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = "socks-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = socksPort
            });
            inbounds.Add(new JsonObject
            {
                ["type"] = "http",
                ["tag"] = "http-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = httpPort
            });
        }

        JsonObject proxyOutbound;
        if (upstreamSocks.HasValue)
        {
            // Cascade mode: singbox-tun -> local authenticated Xray SOCKS5 bridge
            proxyOutbound = new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = "proxy",
                ["server"] = "127.0.0.1",
                ["server_port"] = upstreamSocks.Value.SocksPort,
                ["username"] = upstreamSocks.Value.Username,
                ["password"] = upstreamSocks.Value.Password,
                ["version"] = "5"
            };
        }
        else
        {
            proxyOutbound = node.Protocol.Equals("hysteria", StringComparison.OrdinalIgnoreCase) ||
                            node.Protocol.Equals("hysteria2", StringComparison.OrdinalIgnoreCase) ||
                            node.Protocol.Equals("hy2", StringComparison.OrdinalIgnoreCase)
                ? BuildHysteria2Outbound(node)
                : BuildVlessOutbound(node);
        }

        var outbounds = new JsonArray
        {
            proxyOutbound,
            new JsonObject { ["type"] = "direct", ["tag"] = "direct" }
        };

        var routeRules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" }
        };

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
                    customIps.Add(item.Contains('/') ? item : $"{item}/32");
                else
                    customDomains.Add(item.TrimStart('.'));
            }
            if (customIps.Count > 0)
                routeRules.Add(new JsonObject { ["ip_cidr"] = customIps, ["outbound"] = "direct" });
            if (customDomains.Count > 0)
                routeRules.Add(new JsonObject { ["domain_suffix"] = customDomains, ["outbound"] = "direct" });
        }

        if (bypassRussia)
        {
            routeRules.Add(new JsonObject
            {
                ["domain_suffix"] = new JsonArray(".ru", ".su", ".xn--p1ai"),
                ["outbound"] = "direct"
            });
        }

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = dnsServers,
                ["rules"] = dnsRules,
                ["final"] = "dns-remote",
                ["strategy"] = "ipv4_only"
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = bypassRussia ? "dns-local" : "dns-remote",
                ["rules"] = routeRules,
                ["final"] = "proxy"
            }
        };

        return root.ToJsonString(JsonOptions);
    }

    public static string BuildForPing(VpnServerNode node, int socksPort)
    {
        var proxyOutbound = node.Protocol.Equals("hysteria", StringComparison.OrdinalIgnoreCase) ||
                            node.Protocol.Equals("hysteria2", StringComparison.OrdinalIgnoreCase) ||
                            node.Protocol.Equals("hy2", StringComparison.OrdinalIgnoreCase)
            ? BuildHysteria2Outbound(node)
            : BuildVlessOutbound(node);

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "error" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "socks",
                    ["tag"] = "socks-in",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = socksPort
                }
            },
            ["outbounds"] = new JsonArray
            {
                proxyOutbound,
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" }
            },
            ["route"] = new JsonObject
            {
                ["final"] = "proxy"
            }
        };

        return root.ToJsonString(JsonOptions);
    }

    private static JsonObject BuildVlessOutbound(VpnServerNode node)
    {
        var outbound = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = "proxy",
            ["server"] = node.Host,
            ["server_port"] = node.Port,
            ["uuid"] = node.UuidOrPassword
        };

        if (!string.IsNullOrWhiteSpace(node.Flow))
            outbound["flow"] = node.Flow;

        var sec = node.Security?.ToLowerInvariant() ?? (!string.IsNullOrWhiteSpace(node.Pbk) ? "reality" : "tls");
        var tls = new JsonObject
        {
            ["enabled"] = true,
            ["server_name"] = string.IsNullOrWhiteSpace(node.Sni) ? node.Host : SafeUnescape(node.Sni),
            ["utls"] = new JsonObject
            {
                ["enabled"] = true,
                ["fingerprint"] = string.IsNullOrWhiteSpace(node.Fp) ? "chrome" : SafeUnescape(node.Fp)
            }
        };

        if (sec == "reality")
        {
            tls["reality"] = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = SafeUnescape(node.Pbk ?? ""),
                ["short_id"] = SafeUnescape(node.Sid ?? "")
            };
        }
        outbound["tls"] = tls;

        var net = node.Network?.ToLowerInvariant() ?? "tcp";
        if (net == "ws")
        {
            var ws = new JsonObject
            {
                ["type"] = "ws",
                ["path"] = string.IsNullOrWhiteSpace(node.Path) ? "/" : SafeUnescape(node.Path)
            };
            if (!string.IsNullOrWhiteSpace(node.HostHeader))
                ws["headers"] = new JsonObject { ["Host"] = SafeUnescape(node.HostHeader) };
            outbound["transport"] = ws;
        }
        else if (net == "grpc")
        {
            outbound["transport"] = new JsonObject
            {
                ["type"] = "grpc",
                ["service_name"] = SafeUnescape(node.ServiceName ?? node.Path ?? "")
            };
        }

        return outbound;
    }

    private static JsonObject BuildHysteria2Outbound(VpnServerNode node)
    {
        var outbound = new JsonObject
        {
            ["type"] = "hysteria2",
            ["tag"] = "proxy",
            ["server"] = node.Host,
            ["server_port"] = node.Port,
            ["password"] = node.UuidOrPassword,
            ["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = string.IsNullOrWhiteSpace(node.Sni) ? node.Host : SafeUnescape(node.Sni),
                ["insecure"] = node.Insecure
            }
        };

        if (string.Equals(node.Obfs, "salamander", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(node.ObfsPassword))
        {
            outbound["obfs"] = new JsonObject
            {
                ["type"] = "salamander",
                ["password"] = SafeUnescape(node.ObfsPassword)
            };
        }

        return outbound;
    }

    private static string SafeUnescape(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch { return value; }
    }
}
