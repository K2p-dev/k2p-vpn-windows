using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfPoint = System.Windows.Point;

namespace K2pVpnWindows.Controls;

public partial class ColorPickerControl : System.Windows.Controls.UserControl
{
    private double _hue = 347;
    private double _sat = 0.87;
    private double _val = 0.88;
    private bool _isDragging;
    private bool _suppressEvents;

    public event Action<string>? ColorChanged;

    public ColorPickerControl()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateThumbPosition();
    }

    public void SetHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        try
        {
            var c = (WpfColor)WpfColorConverter.ConvertFromString(hex.StartsWith('#') ? hex : $"#{hex}");
            _suppressEvents = true;
            RgbToHsv(c, out _hue, out _sat, out _val);
            HueSlider.Value = _hue;
            UpdateVisuals(emit: false);
        }
        catch
        {
            // Ignore invalid hex
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void OnSvMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        SvGrid.CaptureMouse();
        UpdateSvFromPoint(e.GetPosition(SvGrid));
    }

    private void OnSvMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging) return;
        UpdateSvFromPoint(e.GetPosition(SvGrid));
    }

    private void OnSvMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        SvGrid.ReleaseMouseCapture();
    }

    private void UpdateSvFromPoint(WpfPoint p)
    {
        if (SvGrid.ActualWidth <= 0 || SvGrid.ActualHeight <= 0) return;
        _sat = Math.Clamp(p.X / SvGrid.ActualWidth, 0, 1);
        _val = Math.Clamp(1.0 - (p.Y / SvGrid.ActualHeight), 0, 1);
        UpdateVisuals(emit: true);
    }

    private void OnHueSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || !IsLoaded) return;
        _hue = e.NewValue;
        UpdateVisuals(emit: true);
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex })
        {
            SetHex(hex);
            ColorChanged?.Invoke(hex);
        }
    }

    private void UpdateVisuals(bool emit)
    {
        var pureHue = HsvToRgb(_hue, 1.0, 1.0);
        HueBaseLayer.Background = new SolidColorBrush(pureHue);

        var selected = HsvToRgb(_hue, _sat, _val);
        var hex = $"#{selected.R:X2}{selected.G:X2}{selected.B:X2}";
        PreviewCircle.Fill = new SolidColorBrush(selected);
        HexLabel.Text = hex;
        UpdateThumbPosition();

        if (emit && !_suppressEvents)
        {
            ColorChanged?.Invoke(hex);
        }
    }

    private void UpdateThumbPosition()
    {
        if (SvGrid.ActualWidth <= 0 || SvGrid.ActualHeight <= 0) return;
        var x = _sat * SvGrid.ActualWidth - 7;
        var y = (1.0 - _val) * SvGrid.ActualHeight - 7;
        Canvas.SetLeft(SvThumb, x);
        Canvas.SetTop(SvThumb, y);
    }

    private static WpfColor HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        var m = v - c;
        double r = 0, g = 0, b = 0;

        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }

        return WpfColor.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static void RgbToHsv(WpfColor color, out double h, out double s, out double v)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;

        h = 0;
        if (d > 0.0001)
        {
            if (Math.Abs(max - r) < 0.0001) h = 60 * (((g - b) / d) % 6);
            else if (Math.Abs(max - g) < 0.0001) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
            if (h < 0) h += 360;
        }

        s = max <= 0.0001 ? 0 : d / max;
        v = max;
    }
}
