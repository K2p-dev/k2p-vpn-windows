using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace K2pVpnWindows.Converters;

public sealed class CountryCodeToFlagConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var input = value?.ToString() ?? string.Empty;
        var code = ResolveIsoCode(input);
        var key = $"Flag_{code}";

        var app = System.Windows.Application.Current;
        if (app is null) return null;

        return app.TryFindResource(key) as ImageSource
               ?? app.TryFindResource("Flag_DEFAULT") as ImageSource;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public static string ResolveIsoCode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "DEFAULT";
        var s = raw.Trim();
        var lower = s.ToLowerInvariant();

        if (s.Contains("🇳🇱") || lower.Contains("нидерланд") || lower.Contains("netherlands") || lower.Contains("амстердам") || lower.Contains("amsterdam") || HasCode(lower, "nl")) return "NL";
        if (s.Contains("🇩🇪") || lower.Contains("герман") || lower.Contains("germany") || lower.Contains("франкфурт") || lower.Contains("frankfurt") || lower.Contains("берлин") || lower.Contains("berlin") || HasCode(lower, "de")) return "DE";
        if (s.Contains("🇫🇮") || lower.Contains("финлянд") || lower.Contains("finland") || lower.Contains("хельсинки") || lower.Contains("helsinki") || HasCode(lower, "fi")) return "FI";
        if (s.Contains("🇸🇪") || lower.Contains("швеци") || lower.Contains("sweden") || lower.Contains("стокгольм") || lower.Contains("stockholm") || HasCode(lower, "se")) return "SE";
        if (s.Contains("🇺🇸") || lower.Contains("сша") || lower.Contains("usa") || lower.Contains("united states") || lower.Contains("америк") || HasCode(lower, "us")) return "US";
        if (s.Contains("🇬🇧") || lower.Contains("великобритан") || lower.Contains("london") || lower.Contains("лондон") || HasCode(lower, "gb") || HasCode(lower, "uk")) return "GB";
        if (s.Contains("🇫🇷") || lower.Contains("франц") || lower.Contains("france") || lower.Contains("париж") || lower.Contains("paris") || HasCode(lower, "fr")) return "FR";
        if (s.Contains("🇵🇱") || lower.Contains("польш") || lower.Contains("poland") || lower.Contains("варшав") || lower.Contains("warsaw") || HasCode(lower, "pl")) return "PL";
        if (s.Contains("🇹🇷") || lower.Contains("турц") || lower.Contains("turkey") || lower.Contains("стамбул") || lower.Contains("istanbul") || HasCode(lower, "tr")) return "TR";
        if (s.Contains("🇰🇿") || lower.Contains("казахстан") || lower.Contains("kazakhstan") || lower.Contains("алматы") || HasCode(lower, "kz")) return "KZ";
        if (s.Contains("🇷🇺") || lower.Contains("росси") || lower.Contains("russia") || lower.Contains("москв") || lower.Contains("moscow") || HasCode(lower, "ru")) return "RU";
        if (s.Contains("🇯🇵") || lower.Contains("япон") || lower.Contains("japan") || lower.Contains("токио") || lower.Contains("tokyo") || HasCode(lower, "jp")) return "JP";
        if (s.Contains("🇸🇬") || lower.Contains("сингапур") || lower.Contains("singapore") || HasCode(lower, "sg")) return "SG";
        if (s.Contains("🇨🇭") || lower.Contains("швейцар") || lower.Contains("switzerland") || lower.Contains("цюрих") || lower.Contains("zurich") || HasCode(lower, "ch")) return "CH";
        if (s.Contains("🇦🇹") || lower.Contains("австр") || lower.Contains("austria") || lower.Contains("вена") || lower.Contains("vienna") || HasCode(lower, "at")) return "AT";
        if (s.Contains("🇨🇿") || lower.Contains("чех") || lower.Contains("czech") || lower.Contains("праг") || lower.Contains("prague") || HasCode(lower, "cz")) return "CZ";
        if (s.Contains("🇱🇻") || lower.Contains("латв") || lower.Contains("latvia") || lower.Contains("рига") || lower.Contains("riga") || HasCode(lower, "lv")) return "LV";
        if (s.Contains("🇪🇪") || lower.Contains("эстон") || lower.Contains("estonia") || lower.Contains("таллин") || lower.Contains("tallinn") || HasCode(lower, "ee")) return "EE";
        if (s.Contains("🇺🇦") || lower.Contains("украин") || lower.Contains("ukraine") || lower.Contains("киев") || HasCode(lower, "ua")) return "UA";
        if (s.Contains("🇧🇾") || lower.Contains("беларус") || lower.Contains("belarus") || lower.Contains("минск") || HasCode(lower, "by")) return "BY";

        return "DEFAULT";
    }

    private static bool HasCode(string lower, string code) =>
        lower == code ||
        lower.StartsWith(code + " ") ||
        lower.StartsWith(code + "-") ||
        lower.Contains("." + code) ||
        lower.Contains(" " + code + " ") ||
        lower.EndsWith(" " + code);
}
