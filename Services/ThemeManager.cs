using System.Windows;
using System.Windows.Media;
using K2pVpnWindows.Models;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfPoint = System.Windows.Point;

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
            SetBrush(app, "BgBrush", WpfColor.FromRgb(0xF8, 0xF5, 0xF6));
            SetBrush(app, "CardBrush", WpfColor.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush(app, "CardVariantBrush", WpfColor.FromRgb(0xF1, 0xEC, 0xEE));
            SetBrush(app, "OutlineBrush", WpfColor.FromRgb(0xDF, 0xD3, 0xD7));
            SetBrush(app, "TextPrimaryBrush", WpfColor.FromRgb(0x18, 0x11, 0x13));
            SetBrush(app, "TextSecondaryBrush", WpfColor.FromRgb(0x5E, 0x4E, 0x53));
            SetBrush(app, "SwitchOffTrackBrush", WpfColor.FromRgb(0xD4, 0xC8, 0xCC));
            SetBrush(app, "BadgeBgBrush", WpfColor.FromRgb(0xFF, 0xE4, 0xE8));
            SetBrush(app, "BadgeTextBrush", WpfColor.FromRgb(0xBE, 0x12, 0x3C));
            SetBrush(app, "ErrorBgBrush", WpfColor.FromRgb(0xFE, 0xE2, 0xE2));
            SetBrush(app, "ErrorTextBrush", WpfColor.FromRgb(0xB9, 0x1C, 0x1C));
            SetBrush(app, "DangerButtonBrush", WpfColor.FromRgb(0xDC, 0x26, 0x26));

            var lightGlow = new RadialGradientBrush
            {
                GradientOrigin = new WpfPoint(0.5, 0.15),
                Center = new WpfPoint(0.5, 0.15),
                RadiusX = 0.9,
                RadiusY = 0.7
            };
            lightGlow.GradientStops.Add(new GradientStop(WpfColor.FromArgb(0x14, accent.R, accent.G, accent.B), 0));
            lightGlow.GradientStops.Add(new GradientStop(WpfColor.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
            lightGlow.Freeze();
            app.Resources["AmbientGlowBrush"] = lightGlow;
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
            SetBrush(app, "BadgeBgBrush", WpfColor.FromRgb(0x3B, 0x11, 0x1C));
            SetBrush(app, "BadgeTextBrush", WpfColor.FromRgb(0xFB, 0x71, 0x85));
            SetBrush(app, "ErrorBgBrush", WpfColor.FromRgb(0x3B, 0x11, 0x1C));
            SetBrush(app, "ErrorTextBrush", WpfColor.FromRgb(0xFC, 0xA5, 0xA5));
            SetBrush(app, "DangerButtonBrush", WpfColor.FromRgb(0x3B, 0x11, 0x1C));

            var darkGlow = new RadialGradientBrush
            {
                GradientOrigin = new WpfPoint(0.5, 0.15),
                Center = new WpfPoint(0.5, 0.15),
                RadiusX = 0.9,
                RadiusY = 0.7
            };
            darkGlow.GradientStops.Add(new GradientStop(WpfColor.FromArgb(0x26, accent.R, accent.G, accent.B), 0));
            darkGlow.GradientStops.Add(new GradientStop(WpfColor.FromArgb(0x00, 0x00, 0x00, 0x00), 1));
            darkGlow.Freeze();
            app.Resources["AmbientGlowBrush"] = darkGlow;
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
