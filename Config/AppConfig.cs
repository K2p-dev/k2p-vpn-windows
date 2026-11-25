using System.IO;

namespace K2pVpnWindows.Config;

public static class AppConfig
{
    public const string AppName = "Как 2 пальца";
    public const string AppSlogan = "Два движения. Ноль проблем.";
    public const string AppVersion = "0.2.6 BETA";
    public const int MaxDevices = 4;

    public const string ApiBaseUrl = "https://api.proksbro.space";
    public const string CabinetUrl = "https://lk.proksbro.space";
    public const string OfferUrl = "https://pay.proksbro.space/offer";
    public const string BotUrl = "https://t.me/easyforeasy_bot";
    public const string SupportUrl = "https://t.me/two_fingers_suport";
    public const string GithubReleasesApi = "https://api.github.com/repos/K2p-dev/k2p-vpn-windows/releases?per_page=1";

    public const string GeodataGeoIpUrl = "https://cdn.jsdelivr.net/gh/runetfreedom/russia-v2ray-rules-dat@release/geoip.dat";
    public const string GeodataGeoSiteUrl = "https://cdn.jsdelivr.net/gh/runetfreedom/russia-v2ray-rules-dat@release/geosite.dat";
    public const string GeodataGeoSiteFallbackUrl = "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/geosite.dat";
    public const string GeodataGeoIpFallbackUrl = "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/geoip.dat";

    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "K2pVpn");

    public static string ConfigFilePath => Path.Combine(AppDataDir, "config.json");
    public static string GeodataOverrideDir => Path.Combine(AppDataDir, "geodata");
    public static string XrayRuntimeConfigPath => Path.Combine(AppDataDir, "xray-runtime.json");
    public static string HevTunConfigPath => Path.Combine(AppDataDir, "hev-tun.yml");
    public static string CoreDir => Path.Combine(AppContext.BaseDirectory, "Core");
}
