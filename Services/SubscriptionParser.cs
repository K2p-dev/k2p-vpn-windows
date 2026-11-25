using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using K2pVpnWindows.Models;

namespace K2pVpnWindows.Services;

public static partial class SubscriptionParser
{
    [GeneratedRegex(@"^[\uD83C][\uDDE6-\uDDFF][\uD83C][\uDDE6-\uDDFF]")]
    private static partial Regex RegionalFlagRegex();

    [GeneratedRegex(@"[-–—]?\s*LTE\b", RegexOptions.IgnoreCase)]
    private static partial Regex LteSuffixRegex();

    public static string ParseTrafficInfo(IDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("subscription-userinfo", out var userInfo) || string.IsNullOrWhiteSpace(userInfo))
            return "Безлимит";

        long upload = 0, download = 0, total = 0;
        foreach (var part in userInfo.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim().ToLowerInvariant();
            if (!long.TryParse(kv[1].Trim(), out var val)) continue;

            switch (key)
            {
                case "upload": upload = val; break;
                case "download": download = val; break;
                case "total": total = val; break;
            }
        }

        var usedGb = FormatGb(upload + download);
        return total <= 0 ? $"{usedGb} / ∞" : $"{usedGb} / {FormatGb(total)}";
    }

    public static List<VpnServerNode> ParseNodes(string rawBody)
    {
        var decoded = DecodeBase64Safe(rawBody);
        var lines = decoded.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var nodes = new List<VpnServerNode>();
        foreach (var line in lines)
        {
            var node = ParseUri(line);
            if (node is not null)
                nodes.Add(node);
        }
        return nodes;
    }

    private static string DecodeBase64Safe(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        try
        {
            var clean = trimmed
                .Replace("\r", "")
                .Replace("\n", "")
                .Replace(" ", "")
                .Replace("\t", "")
                .Replace("-", "+")
                .Replace("_", "/");
            var rem = clean.Length % 4;
            if (rem != 0) clean += new string('=', 4 - rem);
            var bytes = Convert.FromBase64String(clean);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return trimmed;
        }
    }

    private static VpnServerNode? ParseUri(string rawUri)
    {
        try
        {
            if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri))
                return null;

            var scheme = uri.Scheme.ToLowerInvariant();
            return scheme switch
            {
                "vless" => ParseVless(rawUri, uri),
                "hysteria2" or "hy2" => ParseHysteria2(rawUri, uri),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static VpnServerNode ParseVless(string rawUri, Uri uri)
    {
        var uuid = Uri.UnescapeDataString(uri.UserInfo);
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 443;

        var hashIdx = rawUri.IndexOf('#');
        var rawFragment = hashIdx >= 0 ? rawUri[(hashIdx + 1)..] : string.Empty;
        var decodedName = DecodeSafeUrl(rawFragment);
        if (string.IsNullOrWhiteSpace(decodedName)) decodedName = $"{host}:{port}";

        var query = HttpUtility.ParseQueryString(uri.Query);
        var network = query["type"]?.ToLowerInvariant();
        var security = query["security"]?.ToLowerInvariant();
        var flow = query["flow"];
        var sni = query["sni"];
        var pbk = query["pbk"];
        var sid = query["sid"];
        var spx = query["spx"];
        var path = query["path"];
        var hostHeader = query["host"];
        var serviceName = query["serviceName"];
        var xhttpMode = query["mode"];
        var xhttpExtra = query["extra"];
        var fp = query["fp"];

        var resolvedNet = string.IsNullOrWhiteSpace(network) || network == "splithttp" ? "xhttp" : network;
        var resolvedSec = string.IsNullOrWhiteSpace(security) ? (!string.IsNullOrWhiteSpace(pbk) ? "reality" : "tls") : security;
        var label = $"VLESS • {resolvedNet.ToUpperInvariant()} • {resolvedSec.ToUpperInvariant()}";

        var (finalName, flag, country, isLte) = ProcessName(decodedName, host);

        return new VpnServerNode
        {
            Id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(rawUri)))[..12],
            Name = finalName,
            FlagEmoji = flag,
            CountryName = country,
            ProtocolLabel = label,
            IsLte = isLte,
            RawUri = rawUri,
            Protocol = "vless",
            Host = host,
            Port = port,
            UuidOrPassword = uuid,
            Network = resolvedNet,
            Security = resolvedSec,
            Flow = flow,
            Sni = sni,
            Pbk = pbk,
            Sid = sid,
            Spx = spx,
            Path = path,
            HostHeader = hostHeader,
            ServiceName = serviceName,
            XhttpMode = xhttpMode,
            XhttpExtra = xhttpExtra,
            Fp = fp
        };
    }

    private static VpnServerNode ParseHysteria2(string rawUri, Uri uri)
    {
        var password = Uri.UnescapeDataString(uri.UserInfo);
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 443;

        var hashIdx = rawUri.IndexOf('#');
        var rawFragment = hashIdx >= 0 ? rawUri[(hashIdx + 1)..] : string.Empty;
        var decodedName = DecodeSafeUrl(rawFragment);
        if (string.IsNullOrWhiteSpace(decodedName)) decodedName = $"{host}:{port}";

        var query = HttpUtility.ParseQueryString(uri.Query);
        var sni = query["sni"];
        var insecureStr = query["insecure"];
        var insecure = insecureStr == "1" || string.Equals(insecureStr, "true", StringComparison.OrdinalIgnoreCase);
        var obfs = query["obfs"];
        var obfsPassword = query["obfs-password"] ?? query["obfs_password"];

        var (finalName, flag, country, isLte) = ProcessName(decodedName, host);

        return new VpnServerNode
        {
            Id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(rawUri)))[..12],
            Name = finalName,
            FlagEmoji = flag,
            CountryName = country,
            ProtocolLabel = "Hysteria2 • TLS",
            IsLte = isLte,
            RawUri = rawUri,
            Protocol = "hysteria",
            Host = host,
            Port = port,
            UuidOrPassword = password,
            Sni = sni,
            Insecure = insecure,
            Obfs = obfs,
            ObfsPassword = obfsPassword
        };
    }

    private static (string FinalName, string Flag, string Country, bool IsLte) ProcessName(string rawName, string host)
    {
        var text = rawName.Trim();
        var flagMatch = RegionalFlagRegex().Match(text);
        string? leadingFlag = flagMatch.Success ? flagMatch.Value : null;
        if (leadingFlag is not null)
        {
            text = text[leadingFlag.Length..].Trim().TrimStart('-').Trim();
        }

        var isLte = text.Contains("LTE", StringComparison.OrdinalIgnoreCase);
        if (isLte)
        {
            text = LteSuffixRegex().Replace(text, "").Trim().TrimEnd('-', '–', '—').Trim();
        }

        var (detectedFlag, country) = DetectCountry(rawName, host);
        return (string.IsNullOrWhiteSpace(text) ? rawName.Trim() : text, leadingFlag ?? detectedFlag, country, isLte);
    }

    private static (string Flag, string Country) DetectCountry(string name, string host)
    {
        var combined = $"{name} {host}".ToLowerInvariant();
        if (combined.Contains("хельсинки") || combined.Contains("helsinki") || combined.Contains("финлянд") || combined.Contains("finland") || combined.Contains(".fi"))
            return ("🇫🇮", "Финляндия");
        if (combined.Contains("сша") || combined.Contains("usa") || combined.Contains("united states") || combined.Contains("америк") || combined.Contains(" us"))
            return ("🇺🇸", "США");
        if (combined.Contains("нидерланд") || combined.Contains("netherlands") || combined.Contains("амстердам") || combined.Contains("amsterdam") || combined.Contains(".nl"))
            return ("🇳🇱", "Нидерланды");
        if (combined.Contains("росси") || combined.Contains("russia") || combined.Contains("москв") || combined.Contains("moscow") || combined.Contains("спб") || combined.Contains(".ru"))
            return ("🇷🇺", "Россия");
        if (combined.Contains("герман") || combined.Contains("germany") || combined.Contains("франкфурт") || combined.Contains("frankfurt") || combined.Contains("берлин") || combined.Contains("berlin") || combined.Contains(".de"))
            return ("🇩🇪", "Германия");
        if (combined.Contains("великобритан") || combined.Contains("london") || combined.Contains("лондон") || combined.Contains(".uk"))
            return ("🇬🇧", "Великобритания");
        if (combined.Contains("турци") || combined.Contains("turkey") || combined.Contains("стамбул") || combined.Contains("istanbul") || combined.Contains(".tr"))
            return ("🇹🇷", "Турция");
        if (combined.Contains("казахстан") || combined.Contains("kazakhstan") || combined.Contains("алматы") || combined.Contains(".kz"))
            return ("🇰🇿", "Казахстан");
        if (combined.Contains("швеци") || combined.Contains("sweden") || combined.Contains("стокгольм") || combined.Contains(".se"))
            return ("🇸🇪", "Швеция");
        return ("🌐", "Сервер");
    }

    private static string DecodeSafeUrl(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace("+", "%2B"));
        }
        catch
        {
            return value;
        }
    }

    private static string FormatGb(long bytes) =>
        $"{(bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)} GB";
}
