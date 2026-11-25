using System.Windows;
using K2pVpnWindows.Services;

namespace K2pVpnWindows;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;

    public static SettingsStorageService Storage { get; private set; } = null!;
    public static K2pApiClient ApiClient { get; private set; } = null!;
    public static XrayEngineService XrayEngine { get; private set; } = null!;
    public static WindowsIntegrationService WinIntegration { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, "K2pVpnWindows_SingleInstance_Mutex", out var isNew);
        if (!isNew)
        {
            System.Windows.MessageBox.Show("Приложение «Как 2 пальца» уже запущено.", "Как 2 пальца",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Storage = new SettingsStorageService();
        ApiClient = new K2pApiClient(Storage);
        XrayEngine = new XrayEngineService(Storage);
        WinIntegration = new WindowsIntegrationService(ApiClient, Storage);

        var startMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        var mainWindow = new MainWindow(startMinimized);
        MainWindow = mainWindow;
        if (!startMinimized)
        {
            mainWindow.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            XrayEngine?.StopAsync().GetAwaiter().GetResult();
            SystemProxyService.DisableSystemProxy();
        }
        catch
        {
            // Ensure clean exit
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
