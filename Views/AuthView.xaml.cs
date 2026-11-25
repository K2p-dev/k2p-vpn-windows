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
    private string _loginPassword = string.Empty;
    private string _regPassword = string.Empty;
    private string? _pendingVerifyToken;

    public event Action? Authenticated;

    public AuthView()
    {
        InitializeComponent();
    }

    private void OnTabLoginClick(object sender, RoutedEventArgs e) => SwitchTab(0);
    private void OnTabRegisterClick(object sender, RoutedEventArgs e) => SwitchTab(1);

    private void SwitchTab(int index)
    {
        _tgPollCts?.Cancel();
        TgPollingText.Visibility = Visibility.Collapsed;
        ErrorBanner.Visibility = Visibility.Collapsed;

        LoginPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        RegisterPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;

        SetAuthTabState(TabLoginBtn, index == 0);
        SetAuthTabState(TabRegisterBtn, index == 1);
    }

    private static void SetAuthTabState(System.Windows.Controls.Button btn, bool active)
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

    private void OnLoginPasswordChanged(object sender, RoutedEventArgs e)
    {
        _loginPassword = LoginPasswordBox.Password;
    }

    private void OnRegPasswordChanged(object sender, RoutedEventArgs e)
    {
        _regPassword = RegPasswordBox.Password;
    }

    private async void OnEmailLoginClick(object sender, RoutedEventArgs e)
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
        var email = LoginEmailBox.Text.Trim();
        var password = _loginPassword;
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
                if (resp?.Code is "DEVICE_LIMIT_EXCEEDED")
                {
                    ShowError("Достигнут лимит устройств (4 из 4). Отвяжите неиспользуемое устройство в Telegram-боте или веб-кабинете.");
                }
                else
                {
                    ShowError(err ?? "Неверный email или пароль.");
                }
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

    private async void OnRegInitClick(object sender, RoutedEventArgs e)
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
        var email = RegEmailBox.Text.Trim();
        var password = _regPassword;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ShowError("Введите email и пароль для регистрации.");
            return;
        }

        RegInitBtn.IsEnabled = false;
        try
        {
            var (ok, resp, err) = await App.ApiClient.RegisterEmailInitAsync(email, password);
            if (!ok || resp is null || string.IsNullOrWhiteSpace(resp.VerifyToken))
            {
                ShowError(err ?? "Не удалось отправить код подтверждения.");
                return;
            }

            _pendingVerifyToken = resp.VerifyToken;
            RegStep1Panel.Visibility = Visibility.Collapsed;
            RegStep2Panel.Visibility = Visibility.Visible;
        }
        finally
        {
            RegInitBtn.IsEnabled = true;
        }
    }

    private async void OnRegVerifyClick(object sender, RoutedEventArgs e)
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
        var code = RegCodeBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(_pendingVerifyToken) || string.IsNullOrWhiteSpace(code))
        {
            ShowError("Введите код подтверждения из письма.");
            return;
        }

        RegVerifyBtn.IsEnabled = false;
        try
        {
            var (ok, resp, err) = await App.ApiClient.RegisterEmailVerifyAsync(_pendingVerifyToken, code);
            if (!ok || resp is null || string.IsNullOrWhiteSpace(resp.Token))
            {
                ShowError(err ?? "Неверный код подтверждения.");
                return;
            }

            App.Storage.SetToken(resp.Token);
            Authenticated?.Invoke();
        }
        finally
        {
            RegVerifyBtn.IsEnabled = true;
        }
    }

    private void OnRegBackClick(object sender, RoutedEventArgs e)
    {
        RegStep2Panel.Visibility = Visibility.Collapsed;
        RegStep1Panel.Visibility = Visibility.Visible;
        ErrorBanner.Visibility = Visibility.Collapsed;
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
                if (!chkOk && chk?.Code is "EXPIRED" or "NOT_FOUND")
                {
                    ShowError(chkErr ?? "Срок действия ссылки истёк.");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled when switching tabs
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
