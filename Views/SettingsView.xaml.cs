using System.Windows;
using System.Windows.Controls;
using K2pVpnWindows.Config;
using K2pVpnWindows.Models;
using K2pVpnWindows.Services;

namespace K2pVpnWindows.Views;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    public event Action? LoggedOut;

    public SettingsView()
    {
        InitializeComponent();
        RefreshFromStorage();
    }

    public void RefreshFromStorage()
    {
        var s = App.Storage.Current;
        BypassRussiaSwitch.IsChecked = s.BypassRussia;
        AutoStartSwitch.IsChecked = s.AutoStartWithWindows;
        MinimizeTraySwitch.IsChecked = s.MinimizeToTray;
        GeodataDateText.Text = $"База геоданных: {App.WinIntegration.GetGeodataDateDisplay()}";
        VersionText.Text = $"v{AppConfig.AppVersion}";
    }

    private async void OnBypassRussiaToggle(object sender, RoutedEventArgs e)
    {
        var enabled = BypassRussiaSwitch.IsChecked == true;
        App.Storage.Update(s => s.BypassRussia = enabled);

        if (App.XrayEngine.State == VpnConnectionState.Connected && App.XrayEngine.ActiveNode is not null)
        {
            await App.XrayEngine.StartAsync(App.XrayEngine.ActiveNode, App.Storage.Current.VpnMode, enabled);
        }
    }

    private async void OnUpdateGeodataClick(object sender, RoutedEventArgs e)
    {
        UpdateGeodataBtn.IsEnabled = false;
        UpdateGeodataBtn.Content = "Загрузка...";
        try
        {
            var ok = await App.WinIntegration.UpdateGeodataAsync();
            GeodataDateText.Text = ok
                ? $"База геоданных: {App.WinIntegration.GetGeodataDateDisplay()}"
                : "Ошибка загрузки геоданных";

            if (ok && App.XrayEngine.State == VpnConnectionState.Connected && App.XrayEngine.ActiveNode is not null)
            {
                await App.XrayEngine.StartAsync(App.XrayEngine.ActiveNode, App.Storage.Current.VpnMode, App.Storage.Current.BypassRussia);
            }
        }
        finally
        {
            UpdateGeodataBtn.Content = "Обновить";
            UpdateGeodataBtn.IsEnabled = true;
        }
    }

    private void OnAutoStartToggle(object sender, RoutedEventArgs e)
    {
        var enabled = AutoStartSwitch.IsChecked == true;
        App.Storage.Update(s => s.AutoStartWithWindows = enabled);
        WindowsIntegrationService.SetAutoStart(enabled);
    }

    private void OnMinimizeTrayToggle(object sender, RoutedEventArgs e)
    {
        var enabled = MinimizeTraySwitch.IsChecked == true;
        App.Storage.Update(s => s.MinimizeToTray = enabled);
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        CheckUpdatesBtn.IsEnabled = false;
        UpdateStatusText.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "Проверка релизов на GitHub...";

        try
        {
            var (available, latestTag, downloadUrl, _) = await App.WinIntegration.CheckForUpdatesAsync();
            if (!available)
            {
                UpdateStatusText.Text = $"У вас актуальная версия (v{AppConfig.AppVersion}).";
                return;
            }

            UpdateStatusText.Text = $"Доступна новая версия: {latestTag}";
            var result = System.Windows.MessageBox.Show(
                $"Доступна новая версия {latestTag} (текущая: v{AppConfig.AppVersion}).\nСкачать и установить обновление сейчас?",
                "Обновление К2П VPN",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes && !string.IsNullOrWhiteSpace(downloadUrl))
            {
                UpdateStatusText.Text = "Скачивание обновления...";
                await App.WinIntegration.DownloadAndApplyUpdateAsync(downloadUrl, () =>
                {
                    Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
                });
            }
        }
        finally
        {
            CheckUpdatesBtn.IsEnabled = true;
        }
    }

    private void OnOpenCabinetClick(object sender, RoutedEventArgs e) =>
        WindowsIntegrationService.OpenUrlInBrowser(AppConfig.CabinetUrl);

    private void OnOpenSupportClick(object sender, RoutedEventArgs e) =>
        WindowsIntegrationService.OpenUrlInBrowser(AppConfig.SupportUrl);

    private async void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        await App.XrayEngine.StopAsync();
        await App.ApiClient.LogoutAsync();
        LoggedOut?.Invoke();
    }
}
