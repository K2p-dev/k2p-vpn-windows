using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using K2pVpnWindows.Config;
using K2pVpnWindows.Models;

namespace K2pVpnWindows.Services;

public sealed class K2pApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Single pooled SocketsHttpHandler shared across the application lifetime
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
        MaxConnectionsPerServer = 8,
        AutomaticDecompression = DecompressionMethods.All
    };

    private static readonly HttpClient ApiHttp = new(SharedHandler, disposeHandler: false)
    {
        BaseAddress = new Uri(AppConfig.ApiBaseUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(25)
    };

    private static readonly HttpClient PublicHttp = new(SharedHandler, disposeHandler: false)
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static K2pApiClient()
    {
        PublicHttp.DefaultRequestHeaders.UserAgent.ParseAdd("K2P-VPN-Windows/0.2.0");
    }

    private readonly SettingsStorageService _storage;

    public event Action<string>? SessionRevoked;

    public K2pApiClient(SettingsStorageService storage)
    {
        _storage = storage;
    }

    private static bool IsTrustedBackendHost(Uri? uri)
    {
        if (uri is null || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        return uri.Host.Equals("proksbro.space", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".proksbro.space", StringComparison.OrdinalIgnoreCase);
    }

    private HttpRequestMessage CreateApiRequest(HttpMethod method, string relativeUrl, object? body = null)
    {
        var fullUri = new Uri(ApiHttp.BaseAddress!, relativeUrl);
        var req = new HttpRequestMessage(method, fullUri);

        // Attach Authorization and device headers ONLY to *.proksbro.space over HTTPS
        if (IsTrustedBackendHost(fullUri))
        {
            req.Headers.Add("X-Device-HWID", _storage.HardwareId);
            req.Headers.Add("X-Device-Name", _storage.DeviceName);

            var token = _storage.GetToken();
            if (!string.IsNullOrWhiteSpace(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        if (body is not null)
        {
            req.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return req;
    }

    private async Task<(bool Ok, T? Data, string? Error)> SendAsync<T>(HttpRequestMessage req, CancellationToken ct = default)
    {
        try
        {
            using var resp = await ApiHttp.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (resp.StatusCode == HttpStatusCode.Unauthorized && IsTrustedBackendHost(req.RequestUri))
            {
                if (raw.Contains("DEVICE_REVOKED", StringComparison.OrdinalIgnoreCase))
                {
                    _storage.ClearAuth();
                    SessionRevoked?.Invoke("Это устройство было отвязано от аккаунта.");
                    return (false, default, "Устройство отвязано от аккаунта");
                }
                if (raw.Contains("INVALID_TOKEN", StringComparison.OrdinalIgnoreCase))
                {
                    _storage.ClearAuth();
                    SessionRevoked?.Invoke("Сессия истекла. Выполните вход повторно.");
                    return (false, default, "Сессия истекла");
                }
            }

            if (!resp.IsSuccessStatusCode)
            {
                var errModel = TryDeserialize<AuthResponse>(raw);
                var msg = errModel?.Message ?? errModel?.Error ?? $"Ошибка сервера ({(int)resp.StatusCode})";
                return (false, TryDeserialize<T>(raw), msg);
            }

            var data = TryDeserialize<T>(raw);
            return (true, data, null);
        }
        catch (Exception ex)
        {
            return (false, default, $"Ошибка сети: {ex.Message}");
        }
    }

    public Task<(bool Ok, AuthResponse? Data, string? Error)> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        using var req = CreateApiRequest(HttpMethod.Post, "api/auth/login", new LoginRequest(email, password));
        return SendAsync<AuthResponse>(req, ct);
    }

    public Task<(bool Ok, TgInitResponse? Data, string? Error)> InitTelegramAuthAsync(CancellationToken ct = default)
    {
        using var req = CreateApiRequest(HttpMethod.Post, "api/auth/tg/init");
        return SendAsync<TgInitResponse>(req, ct);
    }

    public Task<(bool Ok, TgCheckResponse? Data, string? Error)> CheckTelegramAuthAsync(string token, CancellationToken ct = default)
    {
        var url = $"api/auth/tg/check?token={Uri.EscapeDataString(token)}";
        using var req = CreateApiRequest(HttpMethod.Get, url);
        return SendAsync<TgCheckResponse>(req, ct);
    }

    public async Task LogoutAsync()
    {
        try
        {
            using var req = CreateApiRequest(HttpMethod.Post, "api/auth/logout");
            using var _ = await ApiHttp.SendAsync(req);
        }
        catch
        {
            // Ignore network failures on logout
        }
        finally
        {
            _storage.ClearAuth();
        }
    }

    public Task<(bool Ok, UserProfileResponse? Data, string? Error)> GetProfileAsync(CancellationToken ct = default)
    {
        using var req = CreateApiRequest(HttpMethod.Get, "api/client/profile");
        return SendAsync<UserProfileResponse>(req, ct);
    }

    public Task<(bool Ok, DevicesListResponse? Data, string? Error)> GetDevicesAsync(CancellationToken ct = default)
    {
        using var req = CreateApiRequest(HttpMethod.Get, "api/client/devices");
        return SendAsync<DevicesListResponse>(req, ct);
    }

    public Task<(bool Ok, AuthResponse? Data, string? Error)> DeleteDeviceAsync(string hwid, CancellationToken ct = default)
    {
        using var req = CreateApiRequest(HttpMethod.Delete, $"api/client/devices/{Uri.EscapeDataString(hwid)}");
        return SendAsync<AuthResponse>(req, ct);
    }

    public Task<(bool Ok, PriceListResponse? Data, string? Error)> GetPricesAsync(CancellationToken ct = default)
    {
        using var req = CreateApiRequest(HttpMethod.Get, "api/payments/prices");
        return SendAsync<PriceListResponse>(req, ct);
    }

    public Task<(bool Ok, CreatePaymentResponse? Data, string? Error)> CreatePaymentAsync(int months, CancellationToken ct = default)
    {
        using var req = CreateApiRequest(HttpMethod.Post, "api/payments/create", new CreatePaymentRequest(months));
        return SendAsync<CreatePaymentResponse>(req, ct);
    }

    public async Task<(string Body, string TrafficInfo)?> FetchSubscriptionAsync(string subUrl, CancellationToken ct = default)
    {
        try
        {
            using var resp = await PublicHttp.GetAsync(subUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync(ct);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in resp.Headers)
            {
                headers[h.Key] = string.Join(";", h.Value);
            }
            var traffic = SubscriptionParser.ParseTrafficInfo(headers);
            return (body, traffic);
        }
        catch
        {
            return null;
        }
    }

    public async Task<GithubReleaseModel?> GetLatestGithubReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, AppConfig.GithubReleasesApi);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await PublicHttp.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var releases = await resp.Content.ReadFromJsonAsync<List<GithubReleaseModel>>(JsonOptions, ct);
            return releases?.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> DownloadFileAsync(string url, string destinationPath, CancellationToken ct = default)
    {
        try
        {
            using var resp = await PublicHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return false;

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tempPath = destinationPath + ".tmp";
            await using (var fs = File.Create(tempPath))
            {
                await resp.Content.CopyToAsync(fs, ct);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static T? TryDeserialize<T>(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(raw, JsonOptions);
        }
        catch
        {
            return default;
        }
    }
}
