using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using K2pVpnWindows.Config;
using K2pVpnWindows.Models;
using K2pVpnWindows.Services;
using Forms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace K2pVpnWindows;

public partial class MainWindow : Window
{
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _trayToggleItem;
    private readonly DispatcherTimer _updateCheckTimer;
    private Icon? _baseAppIcon;
    private Icon? _currentTrayIcon;
    private bool _isExplicitExit;
    private bool _hasShownTrayBalloon;
    private bool _isAuthenticated;

    public MainWindow(bool startMinimized = false)
    {
        InitializeComponent();

        ThemeManager.Apply(App.Storage.Current);
        ApplyBackgroundSettings();

        // System Tray Setup (5 menu items + double click)
        _trayToggleItem = new Forms.ToolStripMenuItem("Подключить VPN", null, async (_, _) =>
        {
            await Dispatcher.InvokeAsync(async () => await MainScreen.ToggleConnectionAsync());
        });

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_trayToggleItem);
        menu.Items.Add("Переподключить", null, async (_, _) =>
        {
            await Dispatcher.InvokeAsync(async () => await MainScreen.ReconnectAsync());
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Открыть полный экран", null, (_, _) => Dispatcher.Invoke(() => OpenInMode(AppWindowMode.Full)));
        menu.Items.Add("Открыть компактный режим", null, (_, _) => Dispatcher.Invoke(() => OpenInMode(AppWindowMode.Compact)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        _trayIcon = new Forms.NotifyIcon
        {
            Text = AppConfig.AppName,
            ContextMenuStrip = menu,
            Visible = true
        };

        try
        {
            var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
            if (iconStream is not null) _baseAppIcon = new Icon(iconStream);
        }
        catch
        {
            _baseAppIcon = SystemIcons.Application;
        }
        UpdateTrayVisualState(App.XrayEngine.State);
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);

        App.XrayEngine.StateChanged += state => Dispatcher.Invoke(() => UpdateTrayVisualState(state));

        App.ApiClient.SessionRevoked += msg => Dispatcher.Invoke(async () =>
        {
            await App.XrayEngine.StopAsync();
            ShowAuthScreen();
            System.Windows.MessageBox.Show(msg, AppConfig.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        });

        AuthScreen.Authenticated += async () => await OnAuthenticatedAsync();
        ProfileScreen.LoggedOut += ShowAuthScreen;
        SettingsScreen.AppearanceChanged += () =>
        {
            ThemeManager.Apply(App.Storage.Current);
            ApplyBackgroundSettings();
        };

        // Pause/release video background immediately when minimized, hidden, or deactivated
        StateChanged += (_, _) => SyncVideoPlaybackState();
        IsVisibleChanged += (_, _) => SyncVideoPlaybackState();
        Deactivated += (_, _) => PauseVideoBackground();
        Activated += (_, _) => SyncVideoPlaybackState();

        // 24h Background Update Check
        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _updateCheckTimer.Tick += async (_, _) => await RunBackgroundUpdateCheckAsync();
        _updateCheckTimer.Start();

        Loaded += async (_, _) =>
        {
            ApplyWindowMode(App.Storage.Current.WindowMode);

            if (HasCredentials())
            {
                await OnAuthenticatedAsync();
            }
            else
            {
                ShowAuthScreen();
            }

            if (startMinimized && App.Storage.Current.MinimizeToTray)
            {
                MinimizeToTrayInternal(showTip: false);
            }

            if (!App.Storage.Current.LastUpdateCheckUtc.HasValue ||
                (DateTime.UtcNow - App.Storage.Current.LastUpdateCheckUtc.Value).TotalHours >= 24)
            {
                _ = RunBackgroundUpdateCheckAsync();
            }
        };
    }

    private static bool HasCredentials() =>
        !string.IsNullOrWhiteSpace(App.Storage.GetToken()) ||
        !string.IsNullOrWhiteSpace(App.Storage.GetSubscriptionUrl());

    private async Task OnAuthenticatedAsync()
    {
        _isAuthenticated = true;
        ApplyWindowMode(App.Storage.Current.WindowMode);
        SwitchView(0);

        UserProfileResponse? profile = null;
        if (!string.IsNullOrWhiteSpace(App.Storage.GetToken()))
        {
            var (ok, p, _) = await App.ApiClient.GetProfileAsync();
            if (ok && p is not null)
            {
                profile = p;
                var isPremium = string.Equals(p.Status, "premium", StringComparison.OrdinalIgnoreCase);
                StatusBadgeText.Text = isPremium ? $"Активна • {p.DaysLeft} дн." : "Не активна";
                StatusBadge.Visibility = Visibility.Visible;

                if (!string.IsNullOrWhiteSpace(p.SubLink))
                    App.Storage.SetSubscriptionUrl(p.SubLink);
            }
        }
        else
        {
            StatusBadgeText.Text = "Подписка";
            StatusBadge.Visibility = Visibility.Visible;
        }

        await MainScreen.LoadNodesAsync();
        await TariffsScreen.LoadAsync(profile);
        await ProfileScreen.LoadAsync(profile);
        SettingsScreen.RefreshFromStorage();
    }

    private void ShowAuthScreen()
    {
        _isAuthenticated = false;
        ApplyWindowMode(AppWindowMode.Full);
        NavBar.Visibility = Visibility.Collapsed;
        StatusBadge.Visibility = Visibility.Collapsed;
        AuthScreen.Visibility = Visibility.Visible;
        MainScreen.Visibility = Visibility.Collapsed;
        TariffsScreen.Visibility = Visibility.Collapsed;
        ProfileScreen.Visibility = Visibility.Collapsed;
        SettingsScreen.Visibility = Visibility.Collapsed;
    }

    private void SwitchView(int index)
    {
        AuthScreen.Visibility = Visibility.Collapsed;
        MainScreen.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        TariffsScreen.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        ProfileScreen.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        SettingsScreen.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

        SetNavTabState(NavMainBtn, index == 0);
        SetNavTabState(NavTariffsBtn, index == 1);
        SetNavTabState(NavProfileBtn, index == 2);
        SetNavTabState(NavSettingsBtn, index == 3);
    }

    private static void SetNavTabState(System.Windows.Controls.Button btn, bool active)
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

    private void ApplyWindowMode(AppWindowMode mode)
    {
        var isCompact = mode == AppWindowMode.Compact && _isAuthenticated;
        Width = isCompact ? 360 : 460;
        Height = isCompact ? 460 : 760;

        NavBar.Visibility = (_isAuthenticated && !isCompact) ? Visibility.Visible : Visibility.Collapsed;
        ModeToggleBtn.Content = isCompact ? "◰" : "◱";
        MainScreen.SetCompactMode(isCompact);

        if (isCompact)
        {
            SwitchView(0);
        }
    }

    private void OnToggleWindowModeClick(object sender, RoutedEventArgs e)
    {
        if (!_isAuthenticated) return;
        var next = App.Storage.Current.WindowMode == AppWindowMode.Full
            ? AppWindowMode.Compact
            : AppWindowMode.Full;
        App.Storage.Update(s => s.WindowMode = next);
        ApplyWindowMode(next);
    }

    private void OpenInMode(AppWindowMode mode)
    {
        if (_isAuthenticated)
        {
            App.Storage.Update(s => s.WindowMode = mode);
            ApplyWindowMode(mode);
        }
        RestoreFromTray();
    }

    private void ApplyBackgroundSettings()
    {
        var s = App.Storage.Current;
        BgGradientLayer.Visibility = s.BackgroundType == AppBackgroundType.Gradient ? Visibility.Visible : Visibility.Collapsed;

        if (s.BackgroundType == AppBackgroundType.Image && !string.IsNullOrWhiteSpace(s.BackgroundMediaPath) && File.Exists(s.BackgroundMediaPath))
        {
            StopAndReleaseVideo();
            BgImageLayer.Source = new BitmapImage(new Uri(s.BackgroundMediaPath));
            BgImageLayer.Visibility = Visibility.Visible;
            BgDimOverlay.Opacity = s.BackgroundDimOpacity;
        }
        else if (s.BackgroundType == AppBackgroundType.Video && !string.IsNullOrWhiteSpace(s.BackgroundMediaPath) && File.Exists(s.BackgroundMediaPath))
        {
            BgImageLayer.Source = null;
            BgImageLayer.Visibility = Visibility.Collapsed;
            BgVideoLayer.Visibility = Visibility.Visible;
            BgDimOverlay.Opacity = s.BackgroundDimOpacity;
            SyncVideoPlaybackState();
        }
        else
        {
            BgImageLayer.Source = null;
            BgImageLayer.Visibility = Visibility.Collapsed;
            StopAndReleaseVideo();
            BgDimOverlay.Opacity = 0;
        }
    }

    private void SyncVideoPlaybackState()
    {
        var s = App.Storage.Current;
        if (s.BackgroundType != AppBackgroundType.Video || string.IsNullOrWhiteSpace(s.BackgroundMediaPath) || !File.Exists(s.BackgroundMediaPath))
        {
            StopAndReleaseVideo();
            return;
        }

        var canPlay = IsVisible && WindowState != WindowState.Minimized && IsActive;
        if (canPlay)
        {
            if (BgVideoLayer.Source is null)
                BgVideoLayer.Source = new Uri(s.BackgroundMediaPath);
            BgVideoLayer.Play();
        }
        else if (!IsVisible)
        {
            // Fully release decoder memory when hidden in tray
            StopAndReleaseVideo();
        }
        else
        {
            BgVideoLayer.Pause();
        }
    }

    private void PauseVideoBackground()
    {
        if (BgVideoLayer.Visibility == Visibility.Visible)
        {
            BgVideoLayer.Pause();
        }
    }

    private void StopAndReleaseVideo()
    {
        if (BgVideoLayer.Source is not null)
        {
            BgVideoLayer.Stop();
            BgVideoLayer.Close();
            BgVideoLayer.Source = null;
        }
        BgVideoLayer.Visibility = Visibility.Collapsed;
    }

    private void OnVideoMediaEnded(object sender, RoutedEventArgs e)
    {
        BgVideoLayer.Position = TimeSpan.Zero;
        BgVideoLayer.Play();
    }

    private void UpdateTrayVisualState(VpnConnectionState state)
    {
        var isConnected = state == VpnConnectionState.Connected;
        _trayToggleItem.Text = isConnected ? "Отключить VPN" : "Подключить VPN";
        _trayIcon.Text = isConnected ? $"{AppConfig.AppName} — Подключено" : $"{AppConfig.AppName} — Отключено";

        _trayIcon.Icon = CreateStatusTrayIcon(isConnected);
        _currentTrayIcon?.Dispose();
        _currentTrayIcon = _trayIcon.Icon;
    }

    private Icon CreateStatusTrayIcon(bool isConnected)
    {
        try
        {
            using var bmp = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_baseAppIcon is not null)
            {
                g.DrawIcon(_baseAppIcon, new Rectangle(0, 0, 32, 32));
            }
            var dotColor = isConnected ? Color.FromArgb(34, 197, 94) : Color.FromArgb(225, 29, 72);
            using var dotBrush = new SolidBrush(dotColor);
            using var borderPen = new Pen(Color.FromArgb(13, 9, 10), 2f);
            g.FillEllipse(dotBrush, 19, 19, 11, 11);
            g.DrawEllipse(borderPen, 19, 19, 11, 11);
            return System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }
        catch
        {
            return _baseAppIcon ?? SystemIcons.Application;
        }
    }

    private void OnNavMainClick(object sender, RoutedEventArgs e) => SwitchView(0);

    private async void OnNavTariffsClick(object sender, RoutedEventArgs e)
    {
        SwitchView(1);
        await TariffsScreen.LoadAsync();
    }

    private async void OnNavProfileClick(object sender, RoutedEventArgs e)
    {
        SwitchView(2);
        await ProfileScreen.LoadAsync();
    }

    private void OnNavSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsScreen.RefreshFromStorage();
        SwitchView(3);
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (App.Storage.Current.MinimizeToTray)
            MinimizeToTrayInternal(showTip: true);
        else
            WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // Clicking [X] minimizes to System Tray instead of exiting
        MinimizeToTrayInternal(showTip: true);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            MinimizeToTrayInternal(showTip: true);
            return;
        }
        base.OnClosing(e);
    }

    private void MinimizeToTrayInternal(bool showTip)
    {
        Hide();
        StopAndReleaseVideo();
        WindowsIntegrationService.TrimWorkingSetMemory();

        if (showTip && !_hasShownTrayBalloon)
        {
            _hasShownTrayBalloon = true;
            _trayIcon.ShowBalloonTip(2500, AppConfig.AppName,
                "Приложение свернуто в системный трей и продолжает работу в фоне.",
                Forms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        ApplyBackgroundSettings();
    }

    private void ExitApplication()
    {
        _isExplicitExit = true;
        StopAndReleaseVideo();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _currentTrayIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private async Task RunBackgroundUpdateCheckAsync()
    {
        try
        {
            var (available, latestTag, _, _) = await App.WinIntegration.CheckForUpdatesAsync();
            if (available)
            {
                _trayIcon.ShowBalloonTip(5000, AppConfig.AppName,
                    $"Доступно обновление {latestTag}. Откройте «Настройки» для установки.",
                    Forms.ToolTipIcon.Info);
            }
        }
        catch
        {
            // Ignore background check failures
        }
    }
}
