using System.Windows;
using System.Windows.Media;
using K2pVpnWindows.Models;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace K2pVpnWindows.Services;

public static class ThemeManager
{
    public static void Apply(LocalAppSettings settings)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        var isLight = settings.ThemeMode switch
        {
            AppThemeMode.Light => true,
            AppThemeMode.System => WindowsIntegrationService.IsWindowsLightTheme(),
            _ => false
        };

        var accent = settings.AccentMode switch
        {
            AppAccentMode.WindowsSystem => WindowsIntegrationService.GetWindowsAccentColor(),
            AppAccentMode.Custom => TryParseColor(settings.CustomAccentHex, WpfColor.FromRgb(0xE1, 0x1D, 0x48)),
            _ => WpfColor.FromRgb(0xE1, 0x1D, 0x48)
        };

        var accentHover = Lighten(accent, 0.16);
        var accentDark = Darken(accent, 0.35);

        if (isLight)
        {
            SetBrush(app, "BgBrush", WpfColor.FromRgb(0xF7, 0xF4, 0xF5));
            SetBrush(app, "CardBrush", WpfColor.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush(app, "CardVariantBrush", WpfColor.FromRgb(0xF0, 0xEA, 0xEC));
            SetBrush(app, "OutlineBrush", WpfColor.FromRgb(0xDE, 0xD2, 0xD6));
            SetBrush(app, "TextPrimaryBrush", WpfColor.FromRgb(0x18, 0x11, 0x13));
            SetBrush(app, "TextSecondaryBrush", WpfColor.FromRgb(0x6B, 0x5B, 0x60));
            SetBrush(app, "SwitchOffTrackBrush", WpfColor.FromRgb(0xD4, 0xC8, 0xCC));
        }
        else
        {
            SetBrush(app, "BgBrush", WpfColor.FromRgb(0x0D, 0x09, 0x0A));
            SetBrush(app, "CardBrush", WpfColor.FromRgb(0x18, 0x11, 0x13));
            SetBrush(app, "CardVariantBrush", WpfColor.FromRgb(0x22, 0x15, 0x19));
            SetBrush(app, "OutlineBrush", WpfColor.FromRgb(0x3B, 0x1E, 0x26));
            SetBrush(app, "TextPrimaryBrush", WpfColor.FromRgb(0xF6, 0xF0, 0xF2));
            SetBrush(app, "TextSecondaryBrush", WpfColor.FromRgb(0xA5, 0x94, 0x98));
            SetBrush(app, "SwitchOffTrackBrush", WpfColor.FromRgb(0x2C, 0x1D, 0x21));
        }

        SetBrush(app, "AccentBrush", accent);
        SetBrush(app, "AccentHoverBrush", accentHover);
        SetBrush(app, "AccentDarkBrush", accentDark);
        app.Resources["AccentColor"] = accent;
    }

    private static void SetBrush(System.Windows.Application app, string key, WpfColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        app.Resources[key] = brush;
    }

    private static WpfColor TryParseColor(string? hex, WpfColor fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            var normalized = hex.StartsWith('#') ? hex : $"#{hex}";
            return (WpfColor)WpfColorConverter.ConvertFromString(normalized);
        }
        catch
        {
            return fallback;
        }
    }

    private static WpfColor Lighten(WpfColor c, double factor) => WpfColor.FromRgb(
        (byte)Math.Min(255, c.R + (255 - c.R) * factor),
        (byte)Math.Min(255, c.G + (255 - c.G) * factor),
        (byte)Math.Min(255, c.B + (255 - c.B) * factor));

    private static WpfColor Darken(WpfColor c, double factor) => WpfColor.FromRgb(
        (byte)(c.R * (1 - factor)),
        (byte)(c.G * (1 - factor)),
        (byte)(c.B * (1 - factor)));
}
