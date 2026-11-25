using System.Windows;
using System.Windows.Controls;
using K2pVpnWindows.Config;
using K2pVpnWindows.Models;
using K2pVpnWindows.Services;

namespace K2pVpnWindows.Views;

public sealed class DeviceDisplayItem
{
    public required DeviceItemDto Device { get; init; }
    public string DisplayName => string.IsNullOrWhiteSpace(Device.DeviceName)
        ? (Device.IsCurrent ? "Этот ПК (Текущее)" : "Устройство")
        : (Device.IsCurrent ? $"{Device.DeviceName} (Текущее)" : Device.DeviceName);
    public string HwidShort => Device.Hwid.Length > 12 ? $"ID: {Device.Hwid[..12]}..." : $"ID: {Device.Hwid}";
    public Visibility DeleteVisibility => Device.IsCurrent ? Visibility.Collapsed : Visibility.Visible;
}

public partial class ProfileView : System.Windows.Controls.UserControl
{
    public event Action? LoggedOut;

    public ProfileView()
    {
        InitializeComponent();
    }

    public async Task LoadAsync(UserProfileResponse? profile = null)
    {
        HwidText.Text = $"HWID: {App.Storage.HardwareId}";

        if (string.IsNullOrWhiteSpace(App.Storage.GetToken()))
        {
            AccountNameText.Text = "Режим прямой подписки";
            AccountStatusText.Text = "Авторизация по URL подписки";
            DevicesCountText.Text = "—";
            DevicesItemsControl.ItemsSource = null;
            DevicesEmptyText.Visibility = Visibility.Visible;
            return;
        }

        DevicesEmptyText.Visibility = Visibility.Collapsed;
        profile ??= (await App.ApiClient.GetProfileAsync()).Data;
        if (profile is not null)
        {
            AccountNameText.Text = !string.IsNullOrWhiteSpace(profile.Username)
                ? $"@{profile.Username}"
                : (!string.IsNullOrWhiteSpace(profile.Email) ? profile.Email : $"ID #{profile.UserId}");
            var isPremium = string.Equals(profile.Status, "premium", StringComparison.OrdinalIgnoreCase);
            AccountStatusText.Text = isPremium ? $"Активна • осталось {profile.DaysLeft} дн." : "Подписка не активна";
        }

        var (ok, devResp, _) = await App.ApiClient.GetDevicesAsync();
        if (ok && devResp is not null)
        {
            DevicesCountText.Text = $"{devResp.Devices.Count} / {devResp.MaxDevices}";
            DevicesItemsControl.ItemsSource = devResp.Devices.Select(d => new DeviceDisplayItem { Device = d }).ToList();
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void OnDeleteDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceDisplayItem item }) return;
        var res = System.Windows.MessageBox.Show(
            $"Отвязать устройство «{item.DisplayName}»?",
            "Отвязка устройства",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res != MessageBoxResult.Yes) return;

        var (ok, _, err) = await App.ApiClient.DeleteDeviceAsync(item.Device.Hwid);
        if (ok)
        {
            await LoadAsync();
        }
        else
        {
            System.Windows.MessageBox.Show(err ?? "Не удалось отвязать устройство.", AppConfig.AppName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
