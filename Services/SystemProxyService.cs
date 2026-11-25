using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace K2pVpnWindows.Services;

public static class SystemProxyService
{
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOptionW(nint hInternet, int dwOption, nint lpBuffer, int dwBufferLength);

    public static void EnableSystemProxy(string host = "127.0.0.1", int httpPort = 10809)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
        if (key is null) return;

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"{host}:{httpPort}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", "localhost;127.*;10.*;172.16.*;192.168.*;<local>", RegistryValueKind.String);
        NotifySystem();
    }

    public static void DisableSystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key is null) return;

            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            NotifySystem();
        }
        catch
        {
            // Best effort cleanup on exit
        }
    }

    private static void NotifySystem()
    {
        InternetSetOptionW(nint.Zero, InternetOptionSettingsChanged, nint.Zero, 0);
        InternetSetOptionW(nint.Zero, InternetOptionRefresh, nint.Zero, 0);
    }
}
