using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using K2pVpnWindows.Models;
using K2pVpnWindows.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace K2pVpnWindows.Views;

public sealed class ServerDisplayItem
{
    public required VpnServerNode Node { get; init; }
    public string FlagKey => $"{Node.FlagEmoji} {Node.CountryName} {Node.Name}";
    public string Name => Node.Name;
    public string ProtocolLabel => Node.ProtocolLabel;
    public string PingDisplay => Node.PingMs.HasValue ? $"{Node.PingMs} ms" : "— ms";
}

public partial class MainView : System.Windows.Controls.UserControl
{
    private readonly DispatcherTimer _uiTimer;
    private List<VpnServerNode> _nodes = [];
    private VpnServerNode? _selectedNode;
    private Storyboard? _pulseStoryboard;

    public MainView()
    {
        InitializeComponent();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += OnUiTimerTick;

        // Pause/stop UI timer when hidden in tray to save CPU
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                OnUiTimerTick(null, EventArgs.Empty);
                _uiTimer.Start();
            }
            else
            {
                _uiTimer.Stop();
            }
        };

        App.XrayEngine.StateChanged += state => Dispatcher.Invoke(() => UpdateConnectionVisuals(state));
        UpdateModeButtons();
        UpdateConnectionVisuals(App.XrayEngine.State);
    }

    public void SetCompactMode(bool isCompact)
    {
        PowerContainer.Height = isCompact ? 156 : 210;
        ConnectBtn.Width = isCompact ? 132 : 160;
        ConnectBtn.Height = isCompact ? 132 : 160;
        PulseRing.Width = isCompact ? 154 : 188;
        PulseRing.Height = isCompact ? 154 : 188;

        ModeSelectorCard.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        TrafficInfoText.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        CompactBypassCard.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;
        CompactBypassSwitch.IsChecked = App.Storage.Current.BypassRussia;
    }

    public async Task LoadNodesAsync(string? subUrl = null)
    {
        var url = subUrl ?? App.Storage.GetSubscriptionUrl();
        if (!string.IsNullOrWhiteSpace(url))
        {
            var fetched = await App.ApiClient.FetchSubscriptionAsync(url);
            if (fetched is not null)
            {
                App.Storage.SetSubscriptionUrl(url);
                App.Storage.Update(s =>
                {
                    s.CachedSubscriptionBody = fetched.Value.Body;
                    s.CachedTrafficInfo = fetched.Value.TrafficInfo;
                });
            }
        }

        var cachedBody = App.Storage.Current.CachedSubscriptionBody;
        if (!string.IsNullOrWhiteSpace(cachedBody))
        {
            _nodes = SubscriptionParser.ParseNodes(cachedBody);
        }

        var traffic = App.Storage.Current.CachedTrafficInfo;
        TrafficInfoText.Text = string.IsNullOrWhiteSpace(traffic) ? "Трафик: —" : $"Трафик: {traffic}";

        if (_nodes.Count == 0)
        {
            SelectedFlagImage.Source = FindResource("Flag_DEFAULT") as ImageSource;
            SelectedNameText.Text = "Нет доступных серверов";
            SelectedProtoText.Text = "Проверьте подписку";
            SelectedPingText.Text = "— ms";
            ServersItemsControl.ItemsSource = null;
            return;
        }

        var savedId = App.Storage.Current.SelectedServerId;
        _selectedNode = _nodes.FirstOrDefault(n => n.Id == savedId) ?? _nodes[0];
        ApplySelectedNodeUI();
        RefreshServerListItems();
        _ = PingAllNodesAsync();
    }

    public Task ToggleConnectionAsync() => HandleConnectToggleAsync();

    public async Task ReconnectAsync()
    {
        if (_selectedNode is null) return;
        var s = App.Storage.Current;
        await App.XrayEngine.StartAsync(_selectedNode, s.VpnMode, s.BypassRussia);
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e) => await HandleConnectToggleAsync();

    private async Task HandleConnectToggleAsync()
    {
        StatusMessageText.Visibility = Visibility.Collapsed;

        if (App.XrayEngine.State is VpnConnectionState.Connected or VpnConnectionState.Connecting)
        {
            await App.XrayEngine.StopAsync();
            return;
        }

        if (_selectedNode is null)
        {
            ShowStatus("Сначала выберите сервер или обновите подписку.");
            return;
        }

        ConnectBtn.IsEnabled = false;
        try
        {
            var s = App.Storage.Current;
            var ok = await App.XrayEngine.StartAsync(_selectedNode, s.VpnMode, s.BypassRussia);
            if (!ok)
            {
                ShowStatus(App.XrayEngine.LastError ?? "Ошибка подключения Xray.");
            }
        }
        finally
        {
            ConnectBtn.IsEnabled = true;
        }
    }

    private async void OnCompactBypassToggle(object sender, RoutedEventArgs e)
    {
        var enabled = CompactBypassSwitch.IsChecked == true;
        App.Storage.Update(s => s.BypassRussia = enabled);
        if (App.XrayEngine.State == VpnConnectionState.Connected && _selectedNode is not null)
        {
            await App.XrayEngine.StartAsync(_selectedNode, App.Storage.Current.VpnMode, enabled);
        }
    }

    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        if (App.XrayEngine.State == VpnConnectionState.Connected)
        {
            var dur = App.XrayEngine.GetSessionDuration();
            TimerText.Text = dur.ToString(@"hh\:mm\:ss");

            var (down, up) = App.XrayEngine.SampleSpeed();
            DownSpeedText.Text = $"↓ {FormatSpeed(down)}";
            UpSpeedText.Text = $"↑ {FormatSpeed(up)}";
        }
        else
        {
            TimerText.Text = "00:00:00";
            DownSpeedText.Text = "↓ 0.0 KB/s";
            UpSpeedText.Text = "↑ 0.0 KB/s";
        }
    }

    private void UpdateConnectionVisuals(VpnConnectionState state)
    {
        ConnectBtn.ApplyTemplate();
        var outer = ConnectBtn.Template?.FindName("OuterCircle", ConnectBtn) as Ellipse;
        var label = ConnectBtn.Template?.FindName("PowerLabel", ConnectBtn) as TextBlock;
        var icon = ConnectBtn.Template?.FindName("PowerIcon", ConnectBtn) as TextBlock;
        var glow = ConnectBtn.Template?.FindName("GlowEffect", ConnectBtn) as DropShadowEffect;

        switch (state)
        {
            case VpnConnectionState.Connected:
                if (outer is not null)
                {
                    outer.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
                    outer.SetResourceReference(Shape.FillProperty, "CardVariantBrush");
                }
                icon?.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextOnPageBrush");
                if (label is not null)
                {
                    label.Text = "ПОДКЛЮЧЕНО";
                    label.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextOnPageBrush");
                }
                if (glow is not null) { glow.BlurRadius = 28; glow.Opacity = 0.75; }
                StartPulseAnimation();
                break;

            case VpnConnectionState.Connecting:
                outer?.SetResourceReference(Shape.StrokeProperty, "WarningBrush");
                if (label is not null)
                {
                    label.Text = "ПОДКЛЮЧЕНИЕ...";
                    label.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                }
                StopPulseAnimation();
                break;

            default:
                if (outer is not null)
                {
                    outer.SetResourceReference(Shape.StrokeProperty, "OutlineBrush");
                    outer.SetResourceReference(Shape.FillProperty, "CardBrush");
                }
                icon?.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                if (label is not null)
                {
                    label.Text = "ПОДКЛЮЧИТЬ";
                    label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                }
                if (glow is not null) { glow.BlurRadius = 0; glow.Opacity = 0; }
                StopPulseAnimation();
                break;
        }
    }

    private void StartPulseAnimation()
    {
        if (_pulseStoryboard is not null) return;
        PulseRing.Opacity = 0.6;
        _pulseStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        var scaleX = new DoubleAnimation(1.0, 1.24, TimeSpan.FromSeconds(1.6)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var scaleY = new DoubleAnimation(1.0, 1.24, TimeSpan.FromSeconds(1.6)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var opacity = new DoubleAnimation(0.65, 0.0, TimeSpan.FromSeconds(1.6));

        Storyboard.SetTarget(scaleX, PulseRing);
        Storyboard.SetTargetProperty(scaleX, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
        Storyboard.SetTarget(scaleY, PulseRing);
        Storyboard.SetTargetProperty(scaleY, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
        Storyboard.SetTarget(opacity, PulseRing);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(OpacityProperty));

        _pulseStoryboard.Children.Add(scaleX);
        _pulseStoryboard.Children.Add(scaleY);
        _pulseStoryboard.Children.Add(opacity);
        _pulseStoryboard.Begin();
    }

    private void StopPulseAnimation()
    {
        _pulseStoryboard?.Stop();
        _pulseStoryboard = null;
        PulseRing.Opacity = 0;
        PulseScale.ScaleX = 1;
        PulseScale.ScaleY = 1;
    }

    private async void OnTunModeClick(object sender, RoutedEventArgs e)
    {
        if (App.Storage.Current.VpnMode == VpnMode.Tun) return;
        App.Storage.Update(s => s.VpnMode = VpnMode.Tun);
        UpdateModeButtons();
        if (App.XrayEngine.State == VpnConnectionState.Connected && _selectedNode is not null)
            await App.XrayEngine.StartAsync(_selectedNode, VpnMode.Tun, App.Storage.Current.BypassRussia);
    }

    private async void OnProxyModeClick(object sender, RoutedEventArgs e)
    {
        if (App.Storage.Current.VpnMode == VpnMode.SystemProxy) return;
        App.Storage.Update(s => s.VpnMode = VpnMode.SystemProxy);
        UpdateModeButtons();
        if (App.XrayEngine.State == VpnConnectionState.Connected && _selectedNode is not null)
            await App.XrayEngine.StartAsync(_selectedNode, VpnMode.SystemProxy, App.Storage.Current.BypassRussia);
    }

    private void UpdateModeButtons()
    {
        var isTun = App.Storage.Current.VpnMode == VpnMode.Tun;
        SetModeBtnState(TunModeBtn, isTun);
        SetModeBtnState(ProxyModeBtn, !isTun);
    }

    private static void SetModeBtnState(System.Windows.Controls.Button btn, bool active)
    {
        if (active)
        {
            btn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AccentBrush");
            btn.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AccentForegroundBrush");
            btn.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "AccentButtonBorderBrush");
            btn.BorderThickness = new Thickness(1);
        }
        else
        {
            btn.Background = WpfBrushes.Transparent;
            btn.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextSecondaryBrush");
            btn.BorderThickness = new Thickness(0);
        }
    }

    private void OnToggleServerListClick(object sender, MouseButtonEventArgs e)
    {
        var expanded = ServerListPanel.Visibility == Visibility.Visible;
        ServerListPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        ChevronText.Text = expanded ? "▼" : "▲";
    }

    private async void OnServerItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ServerDisplayItem item })
        {
            _selectedNode = item.Node;
            App.Storage.Update(s => s.SelectedServerId = _selectedNode.Id);
            ApplySelectedNodeUI();
            ServerListPanel.Visibility = Visibility.Collapsed;
            ChevronText.Text = "▼";

            if (App.XrayEngine.State == VpnConnectionState.Connected)
            {
                await App.XrayEngine.StartAsync(_selectedNode, App.Storage.Current.VpnMode, App.Storage.Current.BypassRussia);
            }
        }
    }

    private async void OnPingAllClick(object sender, RoutedEventArgs e) => await PingAllNodesAsync();

    private async void OnRefreshSubClick(object sender, RoutedEventArgs e) => await LoadNodesAsync();

    private async Task PingAllNodesAsync()
    {
        var tasks = _nodes.Select(async node =>
        {
            node.PingMs = await XrayEngineService.MeasureTcpPingAsync(node.Host, node.Port);
        });
        await Task.WhenAll(tasks);
        ApplySelectedNodeUI();
        RefreshServerListItems();
    }

    private void ApplySelectedNodeUI()
    {
        if (_selectedNode is null) return;
        var iso = Converters.CountryCodeToFlagConverter.ResolveIsoCode($"{_selectedNode.FlagEmoji} {_selectedNode.CountryName} {_selectedNode.Name}");
        SelectedFlagImage.Source = System.Windows.Application.Current.TryFindResource($"Flag_{iso}") as ImageSource
                                   ?? System.Windows.Application.Current.TryFindResource("Flag_DEFAULT") as ImageSource;
        SelectedNameText.Text = _selectedNode.Name;
        SelectedProtoText.Text = _selectedNode.ProtocolLabel;
        SelectedPingText.Text = _selectedNode.PingMs.HasValue ? $"{_selectedNode.PingMs} ms" : "— ms";
    }

    private void RefreshServerListItems()
    {
        ServersItemsControl.ItemsSource = _nodes.Select(n => new ServerDisplayItem { Node = n }).ToList();
    }

    private void ShowStatus(string message)
    {
        StatusMessageText.Text = message;
        StatusMessageText.Visibility = Visibility.Visible;
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1024) return $"{bytesPerSec:0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024.0:0.0} KB/s";
        return $"{bytesPerSec / (1024.0 * 1024.0):0.00} MB/s";
    }
}
