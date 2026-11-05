using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TraeTools.Services.Json;

namespace TraeTools.Services.Qoder;

/// <summary>一条活动的归类结果。</summary>
public enum QoderCampaignClass
{
    /// <summary>今天已经领过（上游 claimStatus=CLAIMED 且有奖励）。</summary>
    Already,
    /// <summary>现在可以领。</summary>
    Claimable,
    /// <summary>名额/库存类暂时不可领，过段时间可能恢复。</summary>
    Pending,
    /// <summary>被前置条件锁住（如新人任务未完成），今天内不会自己解锁。</summary>
    Locked,
    /// <summary>详情类活动（没有奖励或不是领取类动作），不参与领取。</summary>
    View,
}

/// <summary>一个可领取/已领取的活动条目。</summary>
public sealed class QoderCampaign
{
    public string Id { get; init; } = "";
    public string? Key { get; init; }
    public string Title { get; init; } = "";
    /// <summary>上游 claimStatus 原值（大写），如 CLAIMABLE / CLAIMED / PENDING。</summary>
    public string Status { get; init; } = "";
    public string? ActionType { get; init; }
    /// <summary>奖励数量；0 表示没写或不是积分类活动。</summary>
    public double Amount { get; init; }
    public string? BenefitKind { get; init; }
    public string? UnavailableReason { get; init; }
    /// <summary>上游的成就完成标记；false 且不可领 ⇒ 归为 Locked。</summary>
    public bool AchievementCompleted { get; init; }

    /// <summary>积分类活动：benefit.kind 为 CREDITS 或留空（老活动不写 kind）。</summary>
    public bool IsCreditKind =>
        string.IsNullOrEmpty(BenefitKind) || BenefitKind.Equals("CREDITS", StringComparison.OrdinalIgnoreCase);

    public bool IsClaimed => Status.Equals("CLAIMED", StringComparison.OrdinalIgnoreCase);
    public bool IsClaimable => Status.Equals("CLAIMABLE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 领取类动作：actionType 留空或 CLAIM_BENEFIT。
    /// 领取成功后官方会把 actionType 降级成 VIEW_DETAILS，所以这个判据**不能**用来判「已领」。
    /// </summary>
    private bool IsActionable =>
        string.IsNullOrEmpty(ActionType) || ActionType.Equals("CLAIM_BENEFIT", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 归类。<b>顺序不可调</b>：先判 CLAIMED，再看是否可领。
    /// 若先按 <see cref="IsActionable"/> 过滤，领取后 actionType 被降级的活动会被误判成「详情类」，
    /// 用户看到的是「明明领过却报今天没活动」。
    /// </summary>
    public QoderCampaignClass Classify()
    {
        if (IsClaimed)
            return Amount > 0 || IsActionable ? QoderCampaignClass.Already : QoderCampaignClass.View;

        // 以下只针对还没领的活动：详情类没有奖励，不参与领取。
        if (!IsActionable) return QoderCampaignClass.View;

        if (!IsClaimable)
        {
            var reason = (UnavailableReason ?? "").ToUpperInvariant();
            if (reason == "REDEMPTION_CODE_OUT_OF_STOCK") return QoderCampaignClass.Pending;
            if (reason == "ACHIEVEMENT_NOT_COMPLETED") return QoderCampaignClass.Locked;
            if (!AchievementCompleted) return QoderCampaignClass.Locked;
            return QoderCampaignClass.View;
        }
        return QoderCampaignClass.Claimable;
    }

    /// <summary>上游失败码的中文说明；不认识返回 null（调用方原样透出，不编）。</summary>
    public static string? FailureReason(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        return code.ToUpperInvariant() switch
        {
            "REDEMPTION_CODE_OUT_OF_STOCK" => "今日名额已发完（每日 10:00 刷新，次日再来）",
            "ACHIEVEMENT_NOT_COMPLETED" => "需先完成新人任务（成就未完成）",
            "CAMPAIGN_NOT_ACTIVE" => "活动已结束 / 未开始",
            "RISK_BLOCKED" => "风控拦截（当前设备 / 账号不可领取）",
            "RISK_DEPENDENCY_UNAVAILABLE" => "风控服务不可用，稍后重试",
            _ => null,
        };
    }
}

/// <summary>一次领取的结果。</summary>
public sealed class QoderClaimResult
{
    public bool Ok { get; init; }
    /// <summary>上游回报「今日已领」（幂等重放），语义上等同成功。</summary>
    public bool AlreadyClaimed { get; init; }
    /// <summary>服务端按「人」去重：同机其它账号本轮已领。既非成功也非凭据故障。</summary>
    public bool Blocked { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// Qoder OpenAPI 客户端：设备码轮询、userinfo、活动领取、额度查询。
///
/// <para>
/// 所有请求都要带 Qoder 自己的身份头（X-Machine-ID / X-Session-ID / X-Request-ID），
/// 值由 <see cref="QoderIdentity"/> 按 uid 派生——上游会重建同样的值做一致性校验。
/// </para>
/// <para>
/// 活动领取（<c>/sash/api/v1/me/campaigns</c>）是现行签到机制，除身份头外还必须带
/// <c>cosy-clienttype: 10</c> 与 <c>User-Agent: Qoder</c>：缺了服务端不报错，
/// 直接返回空活动列表。而 <c>cosy-machine*</c> 六个头**一个都不能发**——
/// 我们没有原生机器身份，发派生假值会被判成非官方客户端，
/// 把可领的「每日 100 Credits」整条过滤掉，比不发更糟。
/// </para>
/// </summary>
public sealed class QoderApiClient
{
    /// <summary>设备流刷新端点（<c>drt-</c> 打这里）。</summary>
    internal const string PathDeviceTokenRefresh = "/api/v1/deviceToken/refresh";
    /// <summary>jobToken 刷新端点（<c>jrt-</c> 打这里）。</summary>
    internal const string PathJobTokenRefresh = "/api/v1/jobToken/refresh";
    /// <summary>PAT 兑换 jobToken（只有 <c>pt-</c> 时的兜底路径）。</summary>
    internal const string PathJobTokenExchange = "/api/v1/jobToken/exchange";
    /// <summary>活动平台（签到入口）。</summary>
    internal const string PathCampaigns = "/sash/api/v1/me/campaigns";

    private readonly HttpClient _http;

    public string? LastError { get; private set; }

    /// <summary>取出并清空最近一次错误（轮询等循环里用它避免重复展示同一条）。</summary>
    public string? TakeLastError()
    {
        var err = LastError;
        LastError = null;
        return err;
    }

    /// <summary>供 <see cref="QoderRefresh"/> 这类静态调用方写入失败原因。</summary>
    internal void SetError(string message) => LastError = message;

    public QoderApiClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    // ===================== 设备码登录 =====================

    /// <summary>
    /// 拼授权页 URL。登录发起是纯本地行为：authorize 端点需要已认证身份，不能用它起流程。
    /// 参数值一律不做 URL 编码（上游按原文解析）。
    /// </summary>
    public static string BuildAuthorizeUrl(QoderRegion region, string machineId, string nonce, string challenge)
    {
        var sb = new StringBuilder();
        sb.Append(region.Website).Append("/device/selectAccounts");
        sb.Append("?challenge=").Append(challenge);
        sb.Append("&challenge_method=S256");
        sb.Append("&nonce=").Append(nonce);
        sb.Append("&client_id=").Append(region.ClientId);
        sb.Append("&machine_id=").Append(machineId);
        if (region.SendRedirectUri)
            sb.Append("&redirect_uri=").Append(region.RedirectUri);
        return sb.ToString();
    }

    /// <summary>
    /// 轮询一次设备码。返回 null 表示用户还没授权（HTTP 202/404 都是这个含义，
    /// 必须当 pending 而不是错误）；返回非 null 表示已拿到令牌。
    /// </summary>
    public async Task<QoderDeviceToken?> PollDeviceTokenAsync(QoderRegion region, string nonce, string verifier)
    {
        var url = region.OpenApiBase + "/api/v1/deviceToken/poll?nonce=" + nonce
                  + "&verifier=" + verifier + "&challenge_method=S256";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req.Headers.TryAddWithoutValidation("User-Agent", region.UserAgent);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var status = (int)resp.StatusCode;
            if (status == 202 || status == 404)
            {
                return null;   // 尚未授权
            }
            var json = await resp.Content.ReadAsStringAsync();
            if (status < 200 || status >= 300)
            {
                LastError = $"轮询失败：HTTP {status} {json}";
                return null;
            }
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var token = ReadString(root, "token") ?? ReadString(root, "device_token");
            if (string.IsNullOrEmpty(token)) return null;
            return new QoderDeviceToken
            {
                AccessToken = token,
                RefreshToken = ReadString(root, "refresh_token"),
                ExpiresAt = ReadExpiry(root, TimeSpan.FromDays(30)),
            };
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>用访问令牌拉账号身份。缺 id 直接失败——没有 id 就无法给账号建档。</summary>
    public async Task<QoderUserInfo?> FetchUserInfoAsync(QoderRegion region, string accessToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, region.OpenApiBase + "/api/v1/userinfo");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req.Headers.TryAddWithoutValidation("User-Agent", region.UserAgent);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"userinfo 失败：HTTP {(int)resp.StatusCode} {json}";
                return null;
            }
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var id = ReadId(root, "id");
            if (string.IsNullOrEmpty(id))
            {
                LastError = "userinfo 缺少 id —— 已拒绝写入";
                return null;
            }
            return new QoderUserInfo { Id = id, Name = ReadString(root, "name") };
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    // ===================== 令牌刷新 =====================

    /// <summary>
    /// 按家族刷新令牌。设备流与 jobToken 走不同端点，拿错端点上游回 401。
    /// </summary>
    public async Task<QoderTokenRefresh?> RefreshTokenAsync(
        QoderRegion region, QoderRefreshPlan plan, string refreshToken)
    {
        var path = plan == QoderRefreshPlan.Job ? PathJobTokenRefresh : PathDeviceTokenRefresh;
        var body = AppJson.Serialize(new QoderTokenRefreshRequest { RefreshToken = refreshToken });
        return await PostTokenAsync(region, path, body, plan, refreshToken);
    }

    /// <summary>PAT 兑换 jobToken（<c>pt-</c> 的续期路径）。</summary>
    public async Task<QoderTokenRefresh?> ExchangePersonalTokenAsync(QoderRegion region, string personalToken)
    {
        var body = AppJson.Serialize(new QoderPatExchangeRequest { PersonalToken = personalToken });
        return await PostTokenAsync(region, PathJobTokenExchange, body, QoderRefreshPlan.Job, "");
    }

    private async Task<QoderTokenRefresh?> PostTokenAsync(
        QoderRegion region, string path, string body, QoderRefreshPlan plan, string previousRefresh)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, region.OpenApiBase + path);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", region.UserAgent);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var status = (int)resp.StatusCode;
            var json = await resp.Content.ReadAsStringAsync();
            if (status < 200 || status >= 300)
            {
                LastError = $"刷新令牌失败：HTTP {status} {json}";
                return null;
            }
            using var doc = JsonDocument.Parse(json);
            var parsed = QoderRefresh.Parse(plan, doc.RootElement, previousRefresh);
            if (string.IsNullOrEmpty(parsed.AccessToken))
            {
                LastError = "刷新响应缺少访问令牌";
                return null;
            }
            return parsed;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    // ===================== 额度与签到 =====================

    /// <summary>查询额度（积分）。解析不出有效结构时抛错，不把「读不懂」写成 0 余额。</summary>
    public async Task<QoderQuota?> FetchQuotaAsync(QoderAccount account)
    {
        var region = QoderRegion.FromDomain(account.Domain);
        if (region == null || string.IsNullOrEmpty(account.AccessToken)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, region.OpenApiBase + "/api/v2/quota/usage");
            ApplyIdentityHeaders(req, region, account);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"额度查询失败：HTTP {(int)resp.StatusCode} {json}";
                return null;
            }
            using var doc = JsonDocument.Parse(json);
            return ParseQuota(doc.RootElement);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>查询套餐名（用于展示）。</summary>
    public async Task<string?> FetchPlanAsync(QoderAccount account)
    {
        var region = QoderRegion.FromDomain(account.Domain);
        if (region == null || string.IsNullOrEmpty(account.AccessToken)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, region.OpenApiBase + "/api/v2/user/plan");
            ApplyIdentityHeaders(req, region, account);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 上游实际字段是 plan_tier_name（如 "Pro Trial"、"Free"），planName/name 是旧形态。
            return ReadString(root, "plan_tier_name") ?? ReadString(root, "planName") ?? ReadString(root, "name");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>拉取活动列表。返回 null 表示整区没有活动平台（404/405/410），空列表表示有平台但没有活动。</summary>
    public async Task<List<QoderCampaign>?> FetchCampaignsAsync(QoderAccount account)
    {
        var region = QoderRegion.FromDomain(account.Domain);
        if (region == null || string.IsNullOrEmpty(account.AccessToken)) return null;

        var first = await FetchCampaignsAtAsync(region, account);
        if (first.Status != 404 && first.Status != 405 && first.Status != 410) return first.Items;

        // 「整区没有接口」的结论必须可复现：404/405/410 可能是代理/网关抖动，
        // 实测同账号相隔十几分钟一次 DISABLED 一次 CLAIMABLE。原样重发一次再判。
        var retry = await FetchCampaignsAtAsync(region, account);
        if (retry.Status != 404 && retry.Status != 405 && retry.Status != 410) return retry.Items;
        LastError = "该区域没有活动平台";
        return null;
    }

    private async Task<(int Status, List<QoderCampaign>? Items)> FetchCampaignsAtAsync(
        QoderRegion region, QoderAccount account)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, region.OpenApiBase + PathCampaigns);
            ApplyCampaignHeaders(req, region, account);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var status = (int)resp.StatusCode;
            var json = await resp.Content.ReadAsStringAsync();
            if (status < 200 || status >= 300)
            {
                LastError = $"活动列表失败：HTTP {status} {json}";
                return (status, null);
            }
            using var doc = JsonDocument.Parse(json);
            return (status, ParseCampaigns(doc.RootElement));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return (0, null);
        }
    }

    /// <summary>领取一个活动。409 与 ALREADY_CLAIMED/REPLAYED 都按「今日已领」成功处理。</summary>
    public async Task<QoderClaimResult> ClaimCampaignAsync(QoderAccount account, string campaignId)
    {
        var region = QoderRegion.FromDomain(account.Domain);
        if (region == null || string.IsNullOrEmpty(account.AccessToken))
            return new QoderClaimResult { Ok = false, Message = "账号缺少可用令牌" };
        try
        {
            var url = region.OpenApiBase + "/sash/api/v1/me/campaigns/" + campaignId + "/claim";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            ApplyCampaignHeaders(req, region, account);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var status = (int)resp.StatusCode;
            var json = await resp.Content.ReadAsStringAsync();

            if (status == 409)
                return new QoderClaimResult { Ok = true, AlreadyClaimed = true, Message = "今日已领取（上游幂等确认）" };

            string? code = null;
            string? failureCode = null;
            string? claimStatus = null;
            if (json.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    code = ReadString(doc.RootElement, "errorCode");
                    failureCode = ReadString(doc.RootElement, "failureCode");
                    claimStatus = ReadString(doc.RootElement, "status");
                }
                catch (JsonException) { /* 非 JSON 响应按失败处理，下面统一给出原文 */ }
            }
            if (code != null && (code.Equals("ALREADY_CLAIMED", StringComparison.OrdinalIgnoreCase)
                              || code.Equals("REPLAYED", StringComparison.OrdinalIgnoreCase)))
                return new QoderClaimResult { Ok = true, AlreadyClaimed = true, Message = "今日已领取" };

            // 服务端按「人」去重：同机其它账号本轮已领。这不是失败，但也不是成功，
            // 得单独标出来，否则用户看到「领取失败」会以为凭据坏了。
            var blocked = (failureCode != null && failureCode.Equals("SAME_PERSON_ALREADY_CLAIMED", StringComparison.OrdinalIgnoreCase))
                       || (claimStatus != null && claimStatus.Equals("BLOCKED", StringComparison.OrdinalIgnoreCase));
            if (blocked)
                return new QoderClaimResult
                {
                    Ok = false,
                    Blocked = true,
                    Message = "同人已领取（同一设备/身份下的其他账号本轮已领，服务端按人去重）",
                };

            if (status >= 200 && status < 300)
                return new QoderClaimResult { Ok = true, Message = "领取成功" };

            LastError = $"领取失败：HTTP {status} {json}";
            return new QoderClaimResult { Ok = false, Message = LastError };
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return new QoderClaimResult { Ok = false, Message = ex.Message };
        }
    }

    // ===================== 内部 =====================

    private static void ApplyIdentityHeaders(HttpRequestMessage req, QoderRegion region, QoderAccount account)
    {
        var uid = account.Id;
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + account.AccessToken);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("User-Agent", region.UserAgent);
        req.Headers.TryAddWithoutValidation("X-Request-ID",
            QoderIdentity.DeriveId(uid, "req") + "-" + (DateTimeOffset.Now.ToUnixTimeMilliseconds() % 1_000_000));
        req.Headers.TryAddWithoutValidation("X-Machine-ID", QoderIdentity.DeriveId(uid, "machine"));
        req.Headers.TryAddWithoutValidation("X-Session-ID", QoderIdentity.DeriveId(uid, "session"));
        req.Headers.TryAddWithoutValidation("Origin", region.Website);
        req.Headers.TryAddWithoutValidation("Referer", region.Website + "/");
    }

    private static void ApplyCampaignHeaders(HttpRequestMessage req, QoderRegion region, QoderAccount account)
    {
        ApplyIdentityHeaders(req, region, account);
        // 桌面端客户端标识：缺失时上游静默返回空活动列表（不报错），所以这三个必须发。
        req.Headers.Remove("User-Agent");
        req.Headers.TryAddWithoutValidation("User-Agent", "Qoder");
        req.Headers.TryAddWithoutValidation("cosy-clienttype", "10");
        req.Headers.TryAddWithoutValidation("cosy-version", "0.4.3");
    }

    private static QoderQuota? ParseQuota(JsonElement root)
    {
        // 上游把额度拆成 userQuota + addOnQuota 两块，各自求和；数值可能是浮点（99.0）。
        double remaining = 0, used = 0, total = 0;
        bool found = false;
        bool exceeded = ReadBool(root, "isQuotaExceeded");

        foreach (var name in new[] { "userQuota", "addOnQuota" })
        {
            if (!root.TryGetProperty(name, out var q) || q.ValueKind != JsonValueKind.Object) continue;
            // 只有额度块里真的带着这三个键，才说明这次响应是可读的；Free 账号三值全 0 是真实额度，不是结构变了。
            if (!q.TryGetProperty("remaining", out _) && !q.TryGetProperty("total", out _)) continue;
            found = true;
            remaining += ReadNumber(q, "remaining");
            used += ReadNumber(q, "used");
            total += ReadNumber(q, "total");
        }

        return found
            ? new QoderQuota { Remaining = remaining, Used = used, Total = total, IsExceeded = exceeded }
            : null;
    }

    private static List<QoderCampaign> ParseCampaigns(JsonElement root)
    {
        var list = new List<QoderCampaign>();
        if (!root.TryGetProperty("campaigns", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;   // 空表 ≠ 报错：上游可能只是没活动
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(item, "campaignId") ?? ReadString(item, "campaign_id");
            if (string.IsNullOrEmpty(id)) continue;

            double amount = 0;
            string? kind = null;
            if (item.TryGetProperty("benefit", out var benefit) && benefit.ValueKind == JsonValueKind.Object)
            {
                amount = ReadNumber(benefit, "amount");
                kind = ReadString(benefit, "kind");
            }

            list.Add(new QoderCampaign
            {
                Id = id,
                Key = ReadString(item, "campaignKey") ?? ReadString(item, "campaign_key"),
                Title = PickPlacementText(item) ?? "",
                Status = (ReadString(item, "claimStatus") ?? "").ToUpperInvariant(),
                ActionType = ReadString(item, "actionType"),
                Amount = amount,
                BenefitKind = kind,
                UnavailableReason = ReadString(item, "unavailableReason"),
                AchievementCompleted = ReadBool(item, "achievementCompleted"),
            });
        }
        return list;
    }

    /// <summary>标题/说明散落在 placements[].content.{zh,en} 里，逐字段取第一个非空值。</summary>
    private static string? PickPlacementText(JsonElement item)
    {
        if (!item.TryGetProperty("placements", out var placements) || placements.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var placement in placements.EnumerateArray())
        {
            if (placement.ValueKind != JsonValueKind.Object) continue;
            if (!placement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
                continue;
            var title = ReadString(content, "title") ?? ReadString(content, "zh") ?? ReadString(content, "en");
            if (!string.IsNullOrEmpty(title)) return title;
        }
        return null;
    }

    internal static DateTime? ReadExpiry(JsonElement root, TimeSpan fallback)
    {
        // expires_in 单位是毫秒；expires_at 是本地时间字符串。都没有就按 fallback 推算。
        var ms = ReadNumber(root, "expires_in");
        if (ms > 0) return DateTime.Now.AddMilliseconds(ms);
        var text = ReadString(root, "expires_at");
        if (!string.IsNullOrEmpty(text)
            && DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return parsed;
        return DateTime.Now.Add(fallback);
    }

    private static string? ReadId(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double ReadNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString(), out var d) ? d : 0,
            _ => 0,
        };
    }

    private static bool ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>设备码轮询成功后的令牌集合。</summary>
public sealed class QoderDeviceToken
{
    public string AccessToken { get; init; } = "";
    public string? RefreshToken { get; init; }
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>userinfo 的身份字段。</summary>
public sealed class QoderUserInfo
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
}

/// <summary>额度快照。</summary>
public sealed class QoderQuota
{
    public double Remaining { get; init; }
    public double Used { get; init; }
    public double Total { get; init; }
    public bool IsExceeded { get; init; }
}
