using System.Collections.Generic;

namespace TraeTools.Services.Qoder;

/// <summary>
/// Qoder 的国内 / 国际两套端点。两区的域名、client_id、UA 全部不同，
/// 令牌刷新也按区域路由（拿错 host 直接 401）。
/// 数值来自 NextAgentX/qoder-trae-workbuddy-switch 的 realm 定义，已用真机探测核对授权页可达性。
/// </summary>
public sealed class QoderRegion
{
    public string Id { get; init; } = "";
    public string Website { get; init; } = "";
    public string OpenApiBase { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string RedirectUri { get; init; } = "";
    public string Domain { get; init; } = "";
    public string UserAgent { get; init; } = "";
    /// <summary>授权 URL 是否追加 redirect_uri（国内需要，国际不需要）。</summary>
    public bool SendRedirectUri { get; init; }
    /// <summary>nonce 是否用带横线的 UUID（国内）还是 32 位无横线 hex（国际）。</summary>
    public bool NonceDashed { get; init; }

    public static readonly QoderRegion Cn = new()
    {
        Id = "cn",
        Website = "https://qoder.com.cn",
        OpenApiBase = "https://openapi.qoder.com.cn",
        ClientId = "1c5e33e1-364d-4ce6-b02c-acaa81274a5c",
        RedirectUri = "qoder-work-cn://",
        Domain = "qoder.com.cn",
        UserAgent = "QoderWork/1.1.64",
        SendRedirectUri = true,
        NonceDashed = true,
    };

    public static readonly QoderRegion Global = new()
    {
        Id = "global",
        Website = "https://qoder.com",
        OpenApiBase = "https://openapi.qoder.sh",
        ClientId = "e883ade2-e6e3-4d6d-adf7-f92ceff5fdcb",
        RedirectUri = "qoder://aicoding.aicoding-agent/login-success",
        Domain = "qoder.com",
        UserAgent = "Qoder/1.1.64",
        SendRedirectUri = false,
        NonceDashed = false,
    };

    public static IReadOnlyList<QoderRegion> All { get; } = new[] { Cn, Global };

    public static QoderRegion? FromDomain(string? domain)
        => domain == Cn.Domain ? Cn : domain == Global.Domain ? Global : null;
}
