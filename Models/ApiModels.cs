using System.Text.Json.Serialization;

namespace K2pVpnWindows.Models;

public sealed record LoginRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password
);

public sealed record AuthResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("token")] public string? Token { get; init; }
    [JsonPropertyName("user_id")] public long? UserId { get; init; }
    [JsonPropertyName("hwid")] public string? Hwid { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}

public sealed record EmailRegisterInitRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password
);

public sealed record EmailRegisterInitResponse
{
    [JsonPropertyName("verify_token")] public string? VerifyToken { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
}

public sealed record EmailRegisterVerifyRequest(
    [property: JsonPropertyName("verify_token")] string VerifyToken,
    [property: JsonPropertyName("code")] string Code
);

public sealed record TgInitResponse
{
    [JsonPropertyName("token")] public string? Token { get; init; }
    [JsonPropertyName("bot_url")] public string? BotUrl { get; init; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
}

public sealed record TgCheckResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("token")] public string? Token { get; init; }
    [JsonPropertyName("user_id")] public long? UserId { get; init; }
    [JsonPropertyName("hwid")] public string? Hwid { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}

public sealed record UserProfileResponse
{
    [JsonPropertyName("user_id")] public long? UserId { get; init; }
    [JsonPropertyName("tg_linked")] public bool TgLinked { get; init; }
    [JsonPropertyName("username")] public string? Username { get; init; }
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("days_left")] public int DaysLeft { get; init; }
    [JsonPropertyName("expiry_time_ms")] public long? ExpiryTimeMs { get; init; }
    [JsonPropertyName("discount_percent")] public int DiscountPercent { get; init; }
    [JsonPropertyName("trial_used")] public bool TrialUsed { get; init; }
    [JsonPropertyName("devices_count")] public int DevicesCount { get; init; }
    [JsonPropertyName("max_devices")] public int MaxDevices { get; init; } = 4;
    [JsonPropertyName("sub_link")] public string? SubLink { get; init; }
}

public sealed record DeviceItemDto
{
    [JsonPropertyName("id")] public long? Id { get; init; }
    [JsonPropertyName("hwid")] public string Hwid { get; init; } = string.Empty;
    [JsonPropertyName("device_name")] public string? DeviceName { get; init; }
    [JsonPropertyName("created_at")] public long? CreatedAt { get; init; }
    [JsonPropertyName("last_seen")] public long? LastSeen { get; init; }
    [JsonPropertyName("is_current")] public bool IsCurrent { get; init; }
}

public sealed record DevicesListResponse
{
    [JsonPropertyName("devices")] public List<DeviceItemDto> Devices { get; init; } = [];
    [JsonPropertyName("max_devices")] public int MaxDevices { get; init; } = 4;
}

public sealed record TariffDto
{
    [JsonPropertyName("months")] public int Months { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("base_price")] public int BasePrice { get; init; }
    [JsonPropertyName("final_price")] public int FinalPrice { get; init; }
}

public sealed record PriceListResponse
{
    [JsonPropertyName("discount_percent")] public int DiscountPercent { get; init; }
    [JsonPropertyName("tariffs")] public List<TariffDto> Tariffs { get; init; } = [];
}

public sealed record CreatePaymentRequest(
    [property: JsonPropertyName("months")] int Months
);

public sealed record CreatePaymentResponse
{
    [JsonPropertyName("order_id")] public string? OrderId { get; init; }
    [JsonPropertyName("amount")] public int? Amount { get; init; }
    [JsonPropertyName("pay_url")] public string? PayUrl { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}

public sealed record ClientConfigResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("sub_link")] public string? SubLink { get; init; }
    [JsonPropertyName("expiry_time_ms")] public long? ExpiryTimeMs { get; init; }
}

public sealed record GithubReleaseAsset
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; init; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; init; }
}

public sealed record GithubReleaseModel
{
    [JsonPropertyName("tag_name")] public string TagName { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("html_url")] public string HtmlUrl { get; init; } = string.Empty;
    [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
    [JsonPropertyName("assets")] public List<GithubReleaseAsset> Assets { get; init; } = [];
}
