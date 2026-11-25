using System.IO;
using System.Windows;
using System.Windows.Controls;
using K2pVpnWindows.Config;
using K2pVpnWindows.Models;
using K2pVpnWindows.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace K2pVpnWindows.Views;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    private bool _isInitializing;

    public event Action? AppearanceChanged;

    public SettingsView()
    {
        InitializeComponent();
        RefreshFromStorage();
    }

    public void RefreshFromStorage()
    {
        _isInitializing = true;
        try
        {
            var s = App.Storage.Current;
            BypassRussiaSwitch.IsChecked = s.BypassRussia;
            AutoStartSwitch.IsChecked = s.AutoStartWithWindows;
            MinimizeTraySwitch.IsChecked = s.MinimizeToTray;
            GeodataDateText.Text = $"База геоданных: {App.WinIntegration.GetGeodataDateDisplay()}";
            VersionText.Text = $"v{AppConfig.AppVersion}";
            ExclusionsBox.Text = string.Join(", ", s.BypassExclusions);
            AccentColorPicker.SetHex(s.CustomAccentHex);
            DimOpacitySlider.Value = s.BackgroundDimOpacity;
            MediaFileNameText.Text = string.IsNullOrWhiteSpace(s.BackgroundMediaPath)
                ? "Файл не выбран"
                : Path.GetFileName(s.BackgroundMediaPath);

            UpdateAppearanceButtons(s);
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void UpdateAppearanceButtons(LocalAppSettings s)
    {
        var accent = (WpfBrush)FindResource("AccentBrush");
        var cardVar = (WpfBrush)FindResource("CardVariantBrush");
        var textPrim = (WpfBrush)FindResource("TextPrimaryBrush");

        ThemeDarkBtn.Background = s.ThemeMode == AppThemeMode.Dark ? accent : cardVar;
        ThemeDarkBtn.Foreground = s.ThemeMode == AppThemeMode.Dark ? WpfBrushes.White : textPrim;
        ThemeLightBtn.Background = s.ThemeMode == AppThemeMode.Light ? accent : cardVar;
        ThemeLightBtn.Foreground = s.ThemeMode == AppThemeMode.Light ? WpfBrushes.White : textPrim;
        ThemeSystemBtn.Background = s.ThemeMode == AppThemeMode.System ? accent : cardVar;
        ThemeSystemBtn.Foreground = s.ThemeMode == AppThemeMode.System ? WpfBrushes.White : textPrim;

        AccentBrandBtn.Background = s.AccentMode == AppAccentMode.Brand ? accent : cardVar;
        AccentBrandBtn.Foreground = s.AccentMode == AppAccentMode.Brand ? WpfBrushes.White : textPrim;
        AccentWinBtn.Background = s.AccentMode == AppAccentMode.WindowsSystem ? accent : cardVar;
        AccentWinBtn.Foreground = s.AccentMode == AppAccentMode.WindowsSystem ? WpfBrushes.White : textPrim;
        AccentCustomBtn.Background = s.AccentMode == AppAccentMode.Custom ? accent : cardVar;
        AccentCustomBtn.Foreground = s.AccentMode == AppAccentMode.Custom ? WpfBrushes.White : textPrim;

        CustomHexPanel.Visibility = s.AccentMode == AppAccentMode.Custom ? Visibility.Visible : Visibility.Collapsed;

        BgGradientBtn.Background = s.BackgroundType == AppBackgroundType.Gradient ? accent : cardVar;
        BgGradientBtn.Foreground = s.BackgroundType == AppBackgroundType.Gradient ? WpfBrushes.White : textPrim;
        BgImageBtn.Background = s.BackgroundType == AppBackgroundType.Image ? accent : cardVar;
        BgImageBtn.Foreground = s.BackgroundType == AppBackgroundType.Image ? WpfBrushes.White : textPrim;
        BgVideoBtn.Background = s.BackgroundType == AppBackgroundType.Video ? accent : cardVar;
        BgVideoBtn.Foreground = s.BackgroundType == AppBackgroundType.Video ? WpfBrushes.White : textPrim;

        MediaBgControls.Visibility = s.BackgroundType != AppBackgroundType.Gradient ? Visibility.Visible : Visibility.Collapsed;
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

    private async void OnSaveExclusionsClick(object sender, RoutedEventArgs e)
    {
        var items = ExclusionsBox.Text
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        App.Storage.Update(s => s.BypassExclusions = items);
        if (App.XrayEngine.State == VpnConnectionState.Connected && App.XrayEngine.ActiveNode is not null)
        {
            await App.XrayEngine.StartAsync(App.XrayEngine.ActiveNode, App.Storage.Current.VpnMode, App.Storage.Current.BypassRussia);
        }
    }

    private void OnThemeDarkClick(object sender, RoutedEventArgs e) => SetTheme(AppThemeMode.Dark);
    private void OnThemeLightClick(object sender, RoutedEventArgs e) => SetTheme(AppThemeMode.Light);
    private void OnThemeSystemClick(object sender, RoutedEventArgs e) => SetTheme(AppThemeMode.System);

    private void SetTheme(AppThemeMode mode)
    {
        App.Storage.Update(s => s.ThemeMode = mode);
        AppearanceChanged?.Invoke();
        RefreshFromStorage();
    }

    private void OnAccentBrandClick(object sender, RoutedEventArgs e) => SetAccent(AppAccentMode.Brand);
    private void OnAccentWinClick(object sender, RoutedEventArgs e) => SetAccent(AppAccentMode.WindowsSystem);
    private void OnAccentCustomClick(object sender, RoutedEventArgs e) => SetAccent(AppAccentMode.Custom);

    private void SetAccent(AppAccentMode mode)
    {
        App.Storage.Update(s => s.AccentMode = mode);
        AppearanceChanged?.Invoke();
        RefreshFromStorage();
    }

    private void OnVisualColorPicked(string hex)
    {
        App.Storage.Update(s =>
        {
            s.AccentMode = AppAccentMode.Custom;
            s.CustomAccentHex = hex;
        });
        AppearanceChanged?.Invoke();
    }

    private void OnBgGradientClick(object sender, RoutedEventArgs e) => SetBackgroundType(AppBackgroundType.Gradient);
    private void OnBgImageClick(object sender, RoutedEventArgs e) => SetBackgroundType(AppBackgroundType.Image);
    private void OnBgVideoClick(object sender, RoutedEventArgs e) => SetBackgroundType(AppBackgroundType.Video);

    private void SetBackgroundType(AppBackgroundType type)
    {
        App.Storage.Update(s => s.BackgroundType = type);
        AppearanceChanged?.Invoke();
        RefreshFromStorage();
    }

    private void OnPickMediaFileClick(object sender, RoutedEventArgs e)
    {
        var isVideo = App.Storage.Current.BackgroundType == AppBackgroundType.Video;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = isVideo ? "Выберите видео-фон (.mp4, .wmv)" : "Выберите изображение (.png, .jpg, .webp)",
            Filter = isVideo
                ? "Видео файлы (*.mp4;*.wmv)|*.mp4;*.wmv"
                : "Изображения (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp"
        };

        if (dlg.ShowDialog() == true)
        {
            App.Storage.Update(s => s.BackgroundMediaPath = dlg.FileName);
            AppearanceChanged?.Invoke();
            RefreshFromStorage();
        }
    }

    private void OnDimSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        App.Storage.Update(s => s.BackgroundDimOpacity = Math.Round(e.NewValue, 2));
        AppearanceChanged?.Invoke();
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
}
