using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using TraeTools.Services.Qoder;

namespace TraeTools.ViewModels;

/// <summary>
/// 一次设备码登录的会话状态。
///
/// <para>
/// <see cref="Verifier"/> 是 PKCE 的秘密：只存内存，不落盘、不进日志、不进界面文案。
/// <see cref="Region"/> 在发起时就绑定——用国内版的会话去轮询国际版会静默把登录态
/// 写进另一套端点，用户看到「账号在但登录不了」，所以跨区一律拒绝。
/// </para>
/// </summary>
public sealed class QoderLoginSession
{
    public required string LoginId { get; init; }
    public required QoderRegion Region { get; init; }
    public required string Verifier { get; init; }
    public required string Nonce { get; init; }
    public required string AuthorizeUrl { get; init; }
    public required DateTime ExpiresAt { get; init; }

    public bool Done { get; set; }
    public string? Error { get; set; }
    public QoderUserInfo? UserInfo { get; set; }
    public QoderDeviceToken? Token { get; set; }
}

/// <summary>设备码登录的发起 / 轮询 / 取消。</summary>
public static class QoderLogin
{
    /// <summary>与上游授权页的等待窗口一致。</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>进程内会话表。登录发起与轮询可能来自不同 UI 动作，必须共享状态。</summary>
    private static readonly Dictionary<string, QoderLoginSession> Sessions = new();
    private static readonly object Gate = new();

    /// <summary>
    /// 发起登录：纯本地计算（生成 PKCE、nonce、machine_id），不发任何网络请求。
    /// 用户拿着 <see cref="QoderLoginSession.AuthorizeUrl"/> 在浏览器里完成授权，
    /// 再由 <see cref="PollAsync"/> 轮询取令牌。
    /// </summary>
    public static QoderLoginSession Start(QoderRegion region)
    {
        var machineId = QoderIdentity.ResolveMachineId(region);
        var (verifier, challenge) = QoderIdentity.Pkce();
        var nonce = QoderIdentity.Nonce(region);
        var url = QoderApiClient.BuildAuthorizeUrl(region, machineId, nonce, challenge);
        var session = new QoderLoginSession
        {
            LoginId = "qoder_" + Guid.NewGuid().ToString("N"),
            Region = region,
            Verifier = verifier,
            Nonce = nonce,
            AuthorizeUrl = url,
            ExpiresAt = DateTime.Now + Timeout,
        };
        lock (Gate) Sessions[session.LoginId] = session;
        return session;
    }

    /// <summary>取消一次登录（幂等）。返回是否真的取消了。</summary>
    public static bool Cancel(string loginId)
    {
        lock (Gate) return Sessions.Remove(loginId);
    }

    /// <summary>
    /// 轮询一次。返回 null 表示还没授权（继续等）；返回会话表示已终结（成功或失败，
    /// 看 <see cref="QoderLoginSession.Error"/>）。
    ///
    /// <para>
    /// 会话检查全部在发 HTTP 之前完成：会话不存在 / 区域不符 / 已超时 都直接终结，
    /// 不浪费一次上游请求，也不会把错区的登录态写进账号库。
    /// </para>
    /// </summary>
    public static async Task<QoderLoginSession?> PollAsync(
        QoderApiClient api, QoderRegion region, string loginId)
    {
        QoderLoginSession session;
        lock (Gate)
        {
            if (!Sessions.TryGetValue(loginId, out var found))
            {
                // 会话已被取消或从未存在：继续轮询只会空转到超时，直接终结。
                return new QoderLoginSession
                {
                    LoginId = loginId,
                    Region = region,
                    Verifier = "",
                    Nonce = "",
                    AuthorizeUrl = "",
                    ExpiresAt = DateTime.Now,
                    Done = true,
                    Error = "登录请求已失效，请重新点击「添加账号」",
                };
            }
            if (found.Region != region)
            {
                found.Done = true;
                found.Error = "登录请求与所选区域不匹配";
                Sessions.Remove(loginId);
                return found;
            }
            if (found.Done)
            {
                Sessions.Remove(loginId);
                return found;
            }
            if (DateTime.Now > found.ExpiresAt)
            {
                found.Done = true;
                found.Error = "授权超时，请重新点击「添加账号」";
                Sessions.Remove(loginId);
                return found;
            }
            session = found;
        }

        var token = await api.PollDeviceTokenAsync(region, session.Nonce, session.Verifier);
        if (token == null)
        {
            // 202/404 与网络抖动都是「还没授权」；真正的协议错误由 api.LastError 带出，
            // 但不终结会话——用户可能只是还没点确认。
            return null;
        }

        var info = await api.FetchUserInfoAsync(region, token.AccessToken);
        if (info == null)
        {
            lock (Gate)
            {
                session.Done = true;
                session.Error = api.LastError ?? "读取账号信息失败";
            }
            return session;
        }

        lock (Gate)
        {
            session.Done = true;
            session.Token = token;
            session.UserInfo = info;
        }
        return session;
    }

    /// <summary>在系统默认浏览器里打开授权页。</summary>
    public static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法打开浏览器，请手动复制授权链接：" + url, ex);
        }
    }
}
