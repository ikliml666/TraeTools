using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace TraeTools.Services.Qoder;

/// <summary>刷新路由：由凭据形态选出，见 <see cref="QoderRefresh.Plan"/>。</summary>
public enum QoderRefreshPlan
{
    /// <summary><c>drt-</c> → <c>POST /api/v1/deviceToken/refresh</c></summary>
    Device,
    /// <summary><c>jrt-</c> → <c>POST /api/v1/jobToken/refresh</c></summary>
    Job,
    /// <summary>只有 <c>pt-</c> → <c>POST /api/v1/jobToken/exchange</c>（PAT 换 jobToken）</summary>
    PatExchange,
}

/// <summary>一次刷新拿回来的令牌。</summary>
public sealed class QoderTokenRefresh
{
    public string AccessToken { get; init; } = "";
    /// <summary>新的刷新令牌；上游可能不轮换，此时保留旧值。</summary>
    public string? RefreshToken { get; init; }
    /// <summary>到期时间（本地时间）。</summary>
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>
/// 令牌刷新：按凭据形态选路由 → 调上游 → 只回写令牌字段。
///
/// <para>
/// 认不出的刷新令牌<b>不猜</b>：有 PAT 就落到兑换兜底，两样都没有就给出可读错误，
/// 绝不打到一个碰运气的端点上。
/// </para>
/// <para>
/// 刷新只动令牌字段，不走 <see cref="QoderStore.Upsert"/> —— 那是登录路径，
/// 会整条替换账号对象，把本地备注、昵称、积分等上游不知道的字段清掉。
/// </para>
/// </summary>
public static class QoderRefresh
{
    /// <summary>
    /// 由账号凭据选出刷新路由。纯函数，不发起任何调用。
    /// 返回 null 表示这条账号没法自动续期，只能重新登录。
    /// </summary>
    public static QoderRefreshPlan? Plan(QoderAccount account)
    {
        if (!string.IsNullOrEmpty(account.RefreshToken))
        {
            var kind = QoderTokens.KindOf(account.RefreshToken);
            if (kind == QoderTokenKind.DeviceRefresh) return QoderRefreshPlan.Device;
            if (kind == QoderTokenKind.JobRefresh) return QoderRefreshPlan.Job;
            // 认不出的刷新令牌不猜，落到 PAT 兜底（若有）。
        }
        if (!string.IsNullOrEmpty(account.PersonalToken)) return QoderRefreshPlan.PatExchange;
        return null;
    }

    /// <summary>
    /// 刷新一条账号的令牌并回写。返回 null 表示失败（原因写在 <see cref="QoderApiClient.LastError"/>）。
    /// </summary>
    public static async Task<QoderTokenRefresh?> RefreshAsync(QoderApiClient api, QoderAccount account)
    {
        var plan = Plan(account);
        if (plan == null)
        {
            api.SetError("这条账号既没有可用的刷新令牌也没有 PAT —— 需要重新登录");
            return null;
        }
        var region = QoderRegion.FromDomain(account.Domain);
        if (region == null)
        {
            api.SetError("账号归属域名无法识别：" + account.Domain);
            return null;
        }

        var result = plan switch
        {
            QoderRefreshPlan.Device => await api.RefreshTokenAsync(region, QoderRefreshPlan.Device, account.RefreshToken!),
            QoderRefreshPlan.Job => await api.RefreshTokenAsync(region, QoderRefreshPlan.Job, account.RefreshToken!),
            QoderRefreshPlan.PatExchange => await api.ExchangePersonalTokenAsync(region, account.PersonalToken!),
            _ => null,
        };
        if (result == null) return null;

        if (!string.IsNullOrEmpty(result.AccessToken))
            account.AccessToken = result.AccessToken;
        if (!string.IsNullOrEmpty(result.RefreshToken))
            account.RefreshToken = result.RefreshToken;
        if (result.ExpiresAt != null)
            account.ExpiresAt = result.ExpiresAt;
        return result;
    }

    /// <summary>
    /// 解析刷新响应。字段名按家族分：设备流取 <c>token</c> 回落 <c>device_token</c>，
    /// jobToken 只认 <c>token</c>。
    /// </summary>
    internal static QoderTokenRefresh Parse(QoderRefreshPlan plan, JsonElement root, string previousRefresh)
    {
        var access = plan == QoderRefreshPlan.Device
            ? ReadString(root, "token") ?? ReadString(root, "device_token") ?? ""
            : ReadString(root, "token") ?? "";

        var refresh = ReadString(root, "refresh_token");
        if (string.IsNullOrEmpty(refresh)) refresh = previousRefresh;

        // expires_in 单位是毫秒；两个字段都没有时按令牌家族兜底（jrt- 24 小时 / drt- 30 天）。
        var expiresAt = QoderApiClient.ReadExpiry(root, plan == QoderRefreshPlan.Job
            ? TimeSpan.FromHours(24)
            : TimeSpan.FromDays(30));

        return new QoderTokenRefresh
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresAt = expiresAt,
        };
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
