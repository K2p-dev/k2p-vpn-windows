using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using K2pVpnWindows.Config;
using K2pVpnWindows.Models;
using Forms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace K2pVpnWindows;

public partial class MainWindow : Window
{
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _trayToggleItem;
    private readonly DispatcherTimer _updateCheckTimer;
    private bool _isExplicitExit;

    public MainWindow(bool startMinimized = false)
    {
        InitializeComponent();

        // System Tray Setup
        _trayToggleItem = new Forms.ToolStripMenuItem("Подключить", null, async (_, _) =>
        {
            await Dispatcher.InvokeAsync(async () => await MainScreen.ToggleConnectionAsync());
        });

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_trayToggleItem);
        menu.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
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
            if (iconStream is not null) _trayIcon.Icon = new Icon(iconStream);
        }
        catch
        {
            _trayIcon.Icon = SystemIcons.Application;
        }
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);

        App.XrayEngine.StateChanged += state => Dispatcher.Invoke(() =>
        {
            _trayToggleItem.Text = state == VpnConnectionState.Connected ? "Отключить" : "Подключить";
        });

        App.ApiClient.SessionRevoked += msg => Dispatcher.Invoke(async () =>
        {
            await App.XrayEngine.StopAsync();
            ShowAuthScreen();
            System.Windows.MessageBox.Show(msg, AppConfig.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        });

        AuthScreen.Authenticated += async () => await OnAuthenticatedAsync();
        SettingsScreen.LoggedOut += ShowAuthScreen;

        // 24h Background Update Check
        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _updateCheckTimer.Tick += async (_, _) => await RunBackgroundUpdateCheckAsync();
        _updateCheckTimer.Start();

        Loaded += async (_, _) =>
        {
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
                Hide();
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
        !string.IsNullOrWhiteSpace(App.Storage.Current.SubscriptionUrl);

    private async Task OnAuthenticatedAsync()
    {
        NavBar.Visibility = Visibility.Visible;
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
                    App.Storage.Update(s => s.SubscriptionUrl = p.SubLink);
            }
        }
        else
        {
            StatusBadgeText.Text = "Подписка";
            StatusBadge.Visibility = Visibility.Visible;
        }

        await MainScreen.LoadNodesAsync();
        await TariffsScreen.LoadAsync(profile);
        SettingsScreen.RefreshFromStorage();
    }

    private void ShowAuthScreen()
    {
        NavBar.Visibility = Visibility.Collapsed;
        StatusBadge.Visibility = Visibility.Collapsed;
        AuthScreen.Visibility = Visibility.Visible;
        MainScreen.Visibility = Visibility.Collapsed;
        TariffsScreen.Visibility = Visibility.Collapsed;
        SettingsScreen.Visibility = Visibility.Collapsed;
    }

    private void SwitchView(int index)
    {
        AuthScreen.Visibility = Visibility.Collapsed;
        MainScreen.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        TariffsScreen.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsScreen.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        var accent = (WpfBrush)FindResource("AccentBrush");
        var textSec = (WpfBrush)FindResource("TextSecondaryBrush");

        NavMainBtn.Background = index == 0 ? accent : WpfBrushes.Transparent;
        NavMainBtn.Foreground = index == 0 ? WpfBrushes.White : textSec;
        NavTariffsBtn.Background = index == 1 ? accent : WpfBrushes.Transparent;
        NavTariffsBtn.Foreground = index == 1 ? WpfBrushes.White : textSec;
        NavSettingsBtn.Background = index == 2 ? accent : WpfBrushes.Transparent;
        NavSettingsBtn.Foreground = index == 2 ? WpfBrushes.White : textSec;
    }

    private void OnNavMainClick(object sender, RoutedEventArgs e) => SwitchView(0);

    private async void OnNavTariffsClick(object sender, RoutedEventArgs e)
    {
        SwitchView(1);
        await TariffsScreen.LoadAsync();
    }

    private void OnNavSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsScreen.RefreshFromStorage();
        SwitchView(2);
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (App.Storage.Current.MinimizeToTray)
            Hide();
        else
            WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (App.Storage.Current.MinimizeToTray)
            Hide();
        else
            ExitApplication();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit && App.Storage.Current.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _isExplicitExit = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
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
