using System.Windows;
using System.Windows.Controls;
using K2pVpnWindows.Config;
using K2pVpnWindows.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace K2pVpnWindows.Views;

public partial class AuthView : System.Windows.Controls.UserControl
{
    private CancellationTokenSource? _tgPollCts;

    public event Action? Authenticated;

    public AuthView()
    {
        InitializeComponent();
    }

    private void OnTabTgClick(object sender, RoutedEventArgs e) => SwitchTab(0);
    private void OnTabTokenClick(object sender, RoutedEventArgs e) => SwitchTab(1);
    private void OnTabEmailClick(object sender, RoutedEventArgs e) => SwitchTab(2);

    private void SwitchTab(int index)
    {
        _tgPollCts?.Cancel();
        TgPollingText.Visibility = Visibility.Collapsed;
        ErrorBanner.Visibility = Visibility.Collapsed;

        TgPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        TokenPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        EmailPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        var accent = (WpfBrush)FindResource("AccentBrush");
        var textSec = (WpfBrush)FindResource("TextSecondaryBrush");

        TabTgBtn.Background = index == 0 ? accent : WpfBrushes.Transparent;
        TabTgBtn.Foreground = index == 0 ? WpfBrushes.White : textSec;
        TabTokenBtn.Background = index == 1 ? accent : WpfBrushes.Transparent;
        TabTokenBtn.Foreground = index == 1 ? WpfBrushes.White : textSec;
        TabEmailBtn.Background = index == 2 ? accent : WpfBrushes.Transparent;
        TabEmailBtn.Foreground = index == 2 ? WpfBrushes.White : textSec;
    }

    private async void OnTgLoginClick(object sender, RoutedEventArgs e)
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
        TgLoginBtn.IsEnabled = false;
        _tgPollCts?.Cancel();
        _tgPollCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            var (ok, init, err) = await App.ApiClient.InitTelegramAuthAsync(_tgPollCts.Token);
            if (!ok || init is null || string.IsNullOrWhiteSpace(init.Token))
            {
                ShowError(err ?? "Не удалось инициализировать вход через Telegram.");
                return;
            }

            var url = string.IsNullOrWhiteSpace(init.BotUrl) ? AppConfig.BotUrl : init.BotUrl;
            WindowsIntegrationService.OpenUrlInBrowser(url);
            TgPollingText.Visibility = Visibility.Visible;

            while (!_tgPollCts.Token.IsCancellationRequested)
            {
                await Task.Delay(2000, _tgPollCts.Token);
                var (chkOk, chk, chkErr) = await App.ApiClient.CheckTelegramAuthAsync(init.Token, _tgPollCts.Token);
                if (chkOk && chk is not null && !string.IsNullOrWhiteSpace(chk.Token))
                {
                    App.Storage.SetToken(chk.Token);
                    Authenticated?.Invoke();
                    return;
                }
                if (chk?.Code is "DEVICE_LIMIT_EXCEEDED")
                {
                    ShowError("Достигнут лимит устройств (4 из 4). Отвяжите старое устройство в боте или веб-кабинете.");
                    return;
                }
                if (!chkOk && chkErr is not null && !chkErr.Contains("pending", StringComparison.OrdinalIgnoreCase) && chk?.Status != "pending")
                {
                    // Keep polling unless explicit fatal error
                    if (chk?.Code is "EXPIRED" or "NOT_FOUND")
                    {
                        ShowError(chkErr);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by user switching tabs
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            TgLoginBtn.IsEnabled = true;
            TgPollingText.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnTokenLoginClick(object sender, RoutedEventArgs e)
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
        var input = TokenBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            ShowError("Введите токен или ссылку подписки.");
            return;
        }

        TokenLoginBtn.IsEnabled = false;
        try
        {
            if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var sub = await App.ApiClient.FetchSubscriptionAsync(input);
                if (sub is null || SubscriptionParser.ParseNodes(sub.Value.Body).Count == 0)
                {
                    ShowError("Не удалось загрузить серверы по этой ссылке подписки.");
                    return;
                }
                App.Storage.SetSubscriptionUrl(input);
                App.Storage.Update(s =>
                {
                    s.CachedSubscriptionBody = sub.Value.Body;
                    s.CachedTrafficInfo = sub.Value.TrafficInfo;
                });
                Authenticated?.Invoke();
                return;
            }

            // Treat as JWT / API token
            App.Storage.SetToken(input);
            var (ok, profile, err) = await App.ApiClient.GetProfileAsync();
            if (!ok || profile is null)
            {
                App.Storage.SetToken(null);
                ShowError(err ?? "Недействительный токен авторизации.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(profile.SubLink))
            {
                App.Storage.SetSubscriptionUrl(profile.SubLink);
            }
            Authenticated?.Invoke();
        }
        finally
        {
            TokenLoginBtn.IsEnabled = true;
        }
    }

    private async void OnEmailLoginClick(object sender, RoutedEventArgs e)
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
        var email = EmailBox.Text.Trim();
        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ShowError("Введите email и пароль.");
            return;
        }

        EmailLoginBtn.IsEnabled = false;
        try
        {
            var (ok, resp, err) = await App.ApiClient.LoginAsync(email, password);
            if (!ok || resp is null || string.IsNullOrWhiteSpace(resp.Token))
            {
                ShowError(err ?? "Неверный email или пароль.");
                return;
            }

            App.Storage.SetToken(resp.Token);
            Authenticated?.Invoke();
        }
        finally
        {
            EmailLoginBtn.IsEnabled = true;
        }
    }

    private void OnOpenBotClick(object sender, RoutedEventArgs e) =>
        WindowsIntegrationService.OpenUrlInBrowser(AppConfig.BotUrl);

    private void OnOpenSupportClick(object sender, RoutedEventArgs e) =>
        WindowsIntegrationService.OpenUrlInBrowser(AppConfig.SupportUrl);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }
}
