using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using K2pVpnWindows.Models;
using K2pVpnWindows.Services;

namespace K2pVpnWindows.Views;

public partial class DebugLogWindow : Window
{
    private string _activeFilter = "ALL";

    public DebugLogWindow()
    {
        InitializeComponent();
        ReloadAllLogs();
        AppLogger.LogAdded += OnLogAdded;
        Closed += (_, _) => AppLogger.LogAdded -= OnLogAdded;
    }

    private void OnLogAdded(LogEntry entry)
    {
        if (_activeFilter != "ALL" && !entry.Category.Equals(_activeFilter, StringComparison.OrdinalIgnoreCase))
            return;

        Dispatcher.BeginInvoke(() =>
        {
            LogTextBox.AppendText(entry.Formatted + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        });
    }

    private void ReloadAllLogs()
    {
        LogTextBox.Text = AppLogger.DumpText(_activeFilter);
        LogTextBox.ScrollToEnd();
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string cat }) return;
        _activeFilter = cat;
        UpdateFilterButtons();
        ReloadAllLogs();
    }

    private void UpdateFilterButtons()
    {
        SetFilterBtn(FilterAllBtn, _activeFilter == "ALL");
        SetFilterBtn(FilterXrayBtn, _activeFilter == "XRAY");
        SetFilterBtn(FilterTunBtn, _activeFilter == "TUN");
        SetFilterBtn(FilterRouteBtn, _activeFilter == "ROUTE");
        SetFilterBtn(FilterAppBtn, _activeFilter == "APP");
    }

    private static void SetFilterBtn(System.Windows.Controls.Button btn, bool active)
    {
        if (active)
        {
            btn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AccentBrush");
            btn.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AccentForegroundBrush");
        }
        else
        {
            btn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "CardVariantBrush");
            btn.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
        }
    }

    private async void OnTestConnectionClick(object sender, RoutedEventArgs e)
    {
        var socksPort = App.XrayEngine.CurrentSocksPort;
        if (App.XrayEngine.State != VpnConnectionState.Connected || socksPort <= 0)
        {
            TestStatusText.Text = "Сначала подключите VPN, чтобы протестировать локальный SOCKS5-порт.";
            return;
        }

        TestConnBtn.IsEnabled = false;
        TestStatusText.Text = $"Тестирование DNS и HTTP через 127.0.0.1:{socksPort}...";

        try
        {
            var sw = Stopwatch.StartNew();
            var dnsIps = await Dns.GetHostAddressesAsync("api.ipify.org");
            var dnsMs = sw.ElapsedMilliseconds;
            AppLogger.Log("APP", $"[TEST] DNS api.ipify.org -> {string.Join(", ", dnsIps.Select(i => i.ToString()))} ({dnsMs} ms)");

            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
                UseProxy = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

            sw.Restart();
            var ipResp = (await http.GetStringAsync("https://api.ipify.org")).Trim();
            sw.Stop();

            var msg = $"Успех! Внешний IP через Xray SOCKS5: {ipResp} ({sw.ElapsedMilliseconds} ms)";
            TestStatusText.Text = msg;
            AppLogger.Log("APP", $"[TEST] {msg}");
        }
        catch (Exception ex)
        {
            var err = $"Ошибка теста SOCKS5: {ex.Message}";
            TestStatusText.Text = err;
            AppLogger.Log("APP", $"[TEST] {err}");
        }
        finally
        {
            TestConnBtn.IsEnabled = true;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(LogTextBox.Text);
        TestStatusText.Text = "Логи скопированы в буфер обмена.";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить логи подключения",
            FileName = $"k2p-vpn-logs-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Text files (*.txt)|*.txt"
        };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, LogTextBox.Text, new UTF8Encoding(false));
            TestStatusText.Text = $"Сохранено: {Path.GetFileName(dlg.FileName)}";
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        AppLogger.Clear();
        LogTextBox.Clear();
    }
}
