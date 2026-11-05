using System;

namespace TraeTools.Services.Qoder;

/// <summary>
/// 一个 Qoder 账号。令牌由设备码授权取得：<c>dt-</c> 是访问令牌（约 30 天），
/// <c>drt-</c> 是刷新令牌。额度信息（积分）是签到页展示的运行时数据，随每次刷新覆盖。
/// </summary>
public class QoderAccount
{
    /// <summary>上游 userinfo 的 id，作为账号唯一键。</summary>
    public string Id { get; set; } = "";

    /// <summary>昵称（userinfo 的 name，缺省时用 id 前 8 位）。</summary>
    public string? Name { get; set; }

    /// <summary>所属区域："qoder.com.cn" 或 "qoder.com"，决定所有接口的 host。</summary>
    public string Domain { get; set; } = "qoder.com.cn";

    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }

    /// <summary>
    /// 个人访问令牌（<c>pt-</c>，长期有效）。设备流登录不会产生它，
    /// 但用户手动粘贴过 PAT 时它是访问令牌过期后的唯一兜底——可以兑换出 jobToken。
    /// </summary>
    public string? PersonalToken { get; set; }

    /// <summary>访问令牌到期时间（本地时间）。null 表示上游没给，按 30 天推算。</summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime? AddedAt { get; set; }

    // ===== 以下为运行时展示数据，不参与身份判断 =====

    /// <summary>剩余积分。-1 表示尚未取到（不要显示成 0）。</summary>
    public double Remaining { get; set; } = -1;
    public double Used { get; set; }
    public double Total { get; set; }
    public bool IsExceeded { get; set; }

    /// <summary>套餐名（/api/v2/user/plan 的 planName）。</summary>
    public string? Plan { get; set; }

    /// <summary>今日已领取的活动文案；null 表示今天还没领到。</summary>
    public string? LastClaimMessage { get; set; }

    public DateTime? LastClaimAt { get; set; }

    public bool HasToken => !string.IsNullOrEmpty(AccessToken);

    /// <summary>当前凭据形态：能不能调用、能不能刷新、有没有兜底 PAT。</summary>
    public QoderCredentialShape Shape()
    {
        var accessKind = QoderTokens.KindOf(AccessToken);
        var refreshKind = QoderTokens.KindOf(RefreshToken);
        return new QoderCredentialShape
        {
            Access = accessKind != null && QoderTokens.IsAccess(accessKind.Value) ? accessKind : null,
            Refresh = refreshKind != null && QoderTokens.IsRefresh(refreshKind.Value) ? refreshKind : null,
            Pat = !string.IsNullOrEmpty(PersonalToken),
            Unrecognized = !string.IsNullOrEmpty(RefreshToken) && refreshKind == null,
        };
    }
}
