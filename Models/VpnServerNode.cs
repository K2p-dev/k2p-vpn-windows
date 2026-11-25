namespace K2pVpnWindows.Models;

public enum VpnMode
{
    Tun,
    SystemProxy
}

public enum VpnConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting
}

public sealed class VpnServerNode
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string FlagEmoji { get; init; } = "🌐";
    public string CountryName { get; init; } = "Сервер";
    public string ProtocolLabel { get; init; } = "VLESS • REALITY";
    public bool IsLte { get; init; }
    public string RawUri { get; init; } = string.Empty;
    public string Protocol { get; init; } = "vless";
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 443;
    public string UuidOrPassword { get; init; } = string.Empty;

    public string? Flow { get; init; }
    public string? Security { get; init; }
    public string? Network { get; init; }
    public string? Sni { get; init; }
    public string? Pbk { get; init; }
    public string? Sid { get; init; }
    public string? Spx { get; init; }
    public string? Path { get; init; }
    public string? HostHeader { get; init; }
    public string? ServiceName { get; init; }
    public string? XhttpMode { get; init; }
    public string? XhttpExtra { get; init; }
    public string? Fp { get; init; }

    public bool Insecure { get; init; }
    public string? Obfs { get; init; }
    public string? ObfsPassword { get; init; }

    public int? PingMs { get; set; }
}

public sealed class LocalAppSettings
{
    public string? EncryptedTokenBase64 { get; set; }
    public string? SubscriptionUrl { get; set; }
    public string? SelectedServerId { get; set; }
    public string? HardwareId { get; set; }
    public VpnMode VpnMode { get; set; } = VpnMode.Tun;
    public bool BypassRussia { get; set; } = true;
    public bool AutoStartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public DateTime? LastGeodataUpdateUtc { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    public string? CachedSubscriptionBody { get; set; }
    public string? CachedTrafficInfo { get; set; }
}
