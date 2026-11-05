using System;

namespace TraeTools.Services.Qoder;

/// <summary>Qoder 的令牌家族。</summary>
public enum QoderTokenKind
{
    /// <summary><c>dt-</c>：设备流访问令牌，可直接调用上游。</summary>
    DeviceAccess,
    /// <summary><c>drt-</c>：设备流刷新令牌。</summary>
    DeviceRefresh,
    /// <summary><c>jt-</c>：jobToken 访问令牌（由 PAT 交换而来）。</summary>
    JobAccess,
    /// <summary><c>jrt-</c>：jobToken 刷新令牌。</summary>
    JobRefresh,
    /// <summary><c>pt-</c>：个人访问令牌，长期有效，用于交换 jobToken。</summary>
    Personal,
}

/// <summary>
/// 令牌前缀语义。<b>刷新端点由刷新令牌的前缀决定</b>，拿错端点会得到 401 而不是静默失败；
/// 反过来把「前缀不认识」当成「没有凭据」会让用户被要求重新登录，而凭据其实完好。
/// 因此判据一律走这里：整段前缀精确匹配，认不出就是认不出，不猜端点、不假装没凭据。
/// </summary>
public static class QoderTokens
{
    public static string Prefix(QoderTokenKind kind) => kind switch
    {
        QoderTokenKind.DeviceAccess => "dt-",
        QoderTokenKind.DeviceRefresh => "drt-",
        QoderTokenKind.JobAccess => "jt-",
        QoderTokenKind.JobRefresh => "jrt-",
        QoderTokenKind.Personal => "pt-",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>稳定标识（日志与展示用）。</summary>
    public static string Name(QoderTokenKind kind) => kind switch
    {
        QoderTokenKind.DeviceAccess => "device_access",
        QoderTokenKind.DeviceRefresh => "device_refresh",
        QoderTokenKind.JobAccess => "job_access",
        QoderTokenKind.JobRefresh => "job_refresh",
        QoderTokenKind.Personal => "personal",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static bool IsAccess(QoderTokenKind kind)
        => kind is QoderTokenKind.DeviceAccess or QoderTokenKind.JobAccess;

    public static bool IsRefresh(QoderTokenKind kind)
        => kind is QoderTokenKind.DeviceRefresh or QoderTokenKind.JobRefresh;

    /// <summary>
    /// 该令牌应该打到哪个刷新端点。访问令牌与 PAT 返回 null（它们不刷新）。
    /// </summary>
    public static string? RefreshPath(QoderTokenKind kind) => kind switch
    {
        QoderTokenKind.DeviceRefresh => QoderApiClient.PathDeviceTokenRefresh,
        QoderTokenKind.JobRefresh => QoderApiClient.PathJobTokenRefresh,
        QoderTokenKind.DeviceAccess or QoderTokenKind.JobAccess or QoderTokenKind.Personal => null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// 从令牌字面量判家族：整段前缀精确匹配，不 trim、不折叠大小写、不看子串。
    ///
    /// <para>
    /// 含空白字符的一律判为认不出——令牌里出现空格或换行只可能是读取时夹带了行尾或引号，
    /// 把它当合法令牌发出去会得到上游一个与「前缀不对」毫不相干的 401，用户联想不到根因。
    /// </para>
    /// </summary>
    public static QoderTokenKind? KindOf(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        foreach (var ch in token)
        {
            if (char.IsWhiteSpace(ch)) return null;
        }
        if (token.StartsWith("drt-", StringComparison.Ordinal)) return QoderTokenKind.DeviceRefresh;
        if (token.StartsWith("jrt-", StringComparison.Ordinal)) return QoderTokenKind.JobRefresh;
        if (token.StartsWith("dt-", StringComparison.Ordinal)) return QoderTokenKind.DeviceAccess;
        if (token.StartsWith("jt-", StringComparison.Ordinal)) return QoderTokenKind.JobAccess;
        if (token.StartsWith("pt-", StringComparison.Ordinal)) return QoderTokenKind.Personal;
        return null;
    }
}

/// <summary>
/// 一个账号当前可用凭据的形态。
///
/// <para>
/// 三项判据分家：能不能刷新看 <see cref="Refresh"/>（PAT 也算能刷新），
/// 能不能直接调用看 <see cref="Access"/>，有没有任何凭据看 <see cref="HasAny"/>。
/// 压成一个布尔就会给出错误的用户引导——「令牌过期了，请重新登录」与
/// 「令牌过期了，但 PAT 还在，可以自动换新的」是两种完全不同的处境。
/// </para>
/// </summary>
public readonly struct QoderCredentialShape
{
    public QoderTokenKind? Access { get; init; }
    public QoderTokenKind? Refresh { get; init; }
    public bool Pat { get; init; }
    /// <summary>刷新令牌存在但前缀认不出——不能说它没有，只能说不知道该怎么刷。</summary>
    public bool Unrecognized { get; init; }

    public bool CanRefresh => Refresh != null || Pat;
    public bool CanCall => Access != null;
    public bool HasAny => CanCall || CanRefresh || Unrecognized;

    public string Describe()
    {
        if (Access == null && Refresh == null && !Pat && !Unrecognized) return "没有凭据";
        var parts = new System.Collections.Generic.List<string>(3);
        if (Access != null) parts.Add("可调用（" + QoderTokens.Name(Access.Value) + "）");
        if (Refresh != null) parts.Add("可刷新（" + QoderTokens.Name(Refresh.Value) + "）");
        if (Pat) parts.Add("PAT 可兑换");
        if (Unrecognized) parts.Add("刷新令牌前缀无法识别");
        return string.Join("，", parts);
    }
}
