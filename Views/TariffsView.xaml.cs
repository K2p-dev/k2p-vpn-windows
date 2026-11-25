using System.Windows;
using System.Windows.Controls;
using K2pVpnWindows.Config;
using K2pVpnWindows.Models;
using K2pVpnWindows.Services;

namespace K2pVpnWindows.Views;

public sealed class TariffCardItem
{
    public int Months { get; init; }
    public string Title { get; init; } = string.Empty;
    public int BasePrice { get; init; }
    public int FinalPrice { get; init; }
    public string BadgeText { get; init; } = string.Empty;
    public Visibility BadgeVisibility => string.IsNullOrWhiteSpace(BadgeText) ? Visibility.Collapsed : Visibility.Visible;
    public string PriceText => FinalPrice == 0 ? "Бесплатно" : $"{FinalPrice} ₽";
    public string OldPriceText => $"{BasePrice} ₽";
    public Visibility OldPriceVisibility => BasePrice > FinalPrice ? Visibility.Visible : Visibility.Collapsed;
}

public partial class TariffsView : System.Windows.Controls.UserControl
{
    public TariffsView()
    {
        InitializeComponent();
    }

    public async Task LoadAsync(UserProfileResponse? profile = null)
    {
        TariffErrorText.Visibility = Visibility.Collapsed;

        profile ??= (await App.ApiClient.GetProfileAsync()).Data;
        var isPremium = string.Equals(profile?.Status, "premium", StringComparison.OrdinalIgnoreCase);

        if (profile is not null)
        {
            SubStatusTitle.Text = isPremium ? "Подписка активна" : "Подписка не активна";
            SubStatusSubtitle.Text = isPremium ? $"Осталось: {profile.DaysLeft} дн." : "Выберите подходящий тариф ниже";
            if (profile.DiscountPercent > 0)
            {
                DiscountBadgeText.Text = $"Скидка -{profile.DiscountPercent}%";
                DiscountBadge.Visibility = Visibility.Visible;
            }
            else
            {
                DiscountBadge.Visibility = Visibility.Collapsed;
            }
        }

        var (ok, prices, _) = await App.ApiClient.GetPricesAsync();
        List<TariffCardItem> items;

        if (ok && prices is not null && prices.Tariffs.Count > 0)
        {
            items = prices.Tariffs.Select(t => new TariffCardItem
            {
                Months = t.Months,
                Title = t.Title,
                BasePrice = t.BasePrice,
                FinalPrice = t.FinalPrice,
                BadgeText = t.Months switch
                {
                    0 => "Пробный",
                    3 => "Выгодно",
                    6 => "Хит",
                    12 => "Максимум",
                    _ => string.Empty
                }
            }).ToList();
        }
        else
        {
            items =
            [
                new TariffCardItem { Months = 0, Title = "Пробный период", BasePrice = 0, FinalPrice = 0, BadgeText = "3 дня" },
                new TariffCardItem { Months = 1, Title = "1 месяц", BasePrice = 199, FinalPrice = 199 },
                new TariffCardItem { Months = 3, Title = "3 месяца", BasePrice = 597, FinalPrice = 499, BadgeText = "Выгодно" },
                new TariffCardItem { Months = 6, Title = "6 месяцев", BasePrice = 1194, FinalPrice = 899, BadgeText = "Хит" }
            ];
        }

        TariffsListControl.ItemsSource = items;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void OnPayClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TariffCardItem item }) return;
        TariffErrorText.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(App.Storage.GetToken()))
        {
            WindowsIntegrationService.OpenUrlInBrowser(AppConfig.CabinetUrl);
            return;
        }

        var (ok, resp, err) = await App.ApiClient.CreatePaymentAsync(item.Months);
        if (ok && resp is not null && !string.IsNullOrWhiteSpace(resp.PayUrl))
        {
            WindowsIntegrationService.OpenUrlInBrowser(resp.PayUrl);
        }
        else
        {
            TariffErrorText.Text = err ?? "Не удалось создать платёж. Попробуйте через веб-кабинет.";
            TariffErrorText.Visibility = Visibility.Visible;
        }
    }

    private void OnOpenOfferClick(object sender, RoutedEventArgs e) =>
        WindowsIntegrationService.OpenUrlInBrowser(AppConfig.OfferUrl);
}
