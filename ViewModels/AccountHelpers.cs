using System;
using System.Linq;
using System.Threading.Tasks;
using TraeCheckin;

namespace TraeTools.ViewModels;

/// <summary>账号相关跨 VM 共享工具（DeviceId 补发 / Token 会话换新）。</summary>
public static class AccountHelpers
{
    /// <summary>缺号则生成不与其它账号重复的 16 位数字设备号（风控要求，多账号共用会触发 9074）。</summary>
    public static void EnsureDeviceId(TraeAccount acc)
    {
        var used = new System.Collections.Generic.HashSet<string>(
            MainViewModel.AppConfig?.Accounts.Where(a => a.Id != acc.Id).Select(a => a.DeviceId)
               .Where(d => !string.IsNullOrWhiteSpace(d)) ?? Array.Empty<string>(),
            StringComparer.Ordinal);
        // 已有且不与其它账号重复 → 保持不动（幂等）；为空或与别人相同（手工填错）→ 换发独立新号
        if (!string.IsNullOrWhiteSpace(acc.DeviceId) && !used.Contains(acc.DeviceId)) return;
        acc.DeviceId = NewDeviceId(used);
    }

    /// <summary>
    /// 换发一个不与其它账号重复的新设备号（不写入账号，由调用方决定是否采纳）。
    /// 9074/9095 都是「这个设备号当前用不了」，换号重签比原地退避有效。
    /// </summary>
    public static string NewDeviceId()
    {
        var used = new System.Collections.Generic.HashSet<string>(
            MainViewModel.AppConfig?.Accounts.Select(a => a.DeviceId)
               .Where(d => !string.IsNullOrWhiteSpace(d)) ?? Array.Empty<string>(),
            StringComparer.Ordinal);
        return NewDeviceId(used);
    }

    private static string NewDeviceId(System.Collections.Generic.HashSet<string> used)
    {
        string id;
        do { id = Random.Shared.NextInt64(1_000_000_000_000_000L, 10_000_000_000_000_000L).ToString(); }
        while (used.Contains(id));
        return id;
    }

    /// <summary>
    /// 校验/换新 Token。优先级：refreshToken 换新（约 180 天链路）→ Session 换新（约 14 天链路）。
    /// 返回是否持有可用 token。
    /// </summary>
    public static async Task<bool> EnsureValidTokenAsync(TraeAccount acc)
    {
        var cfg = MainViewModel.AppConfig;
        var api = MainViewModel.CheckinApi;
        var name = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
        if (cfg == null || api == null) return false;

        // 有 Session 但还没有 refreshToken：立刻升级，不等 Token 失效。
        // Token 与 Session 同为 14 天到期，等到 Token 失效时 Session 也已过期，届时换不到了。
        if (string.IsNullOrEmpty(acc.RefreshToken) && !string.IsNullOrEmpty(acc.Session))
            await TryUpgradeToRefreshTokenAsync(acc, api, name, cfg);

        if (!string.IsNullOrEmpty(acc.Token))
        {
            var st = await api.GetStatusAsync(acc.Token, acc.DeviceId);
            if (st != null && st.code == 0) return true;
            CheckinLog(name, $"Token 已失效（status code={st?.code ?? -1}），尝试续期");
        }

        // 首选 refreshToken：一次换取 14 天 access token，refreshToken 自身约 180 天
        if (!string.IsNullOrEmpty(acc.RefreshToken) &&
            (acc.RefreshExpiresAt == null || acc.RefreshExpiresAt > DateTime.Now))
        {
            var refreshed = await api.ExchangeTokenAsync(acc.RefreshToken);
            if (refreshed?.Token != null)
            {
                acc.Token = refreshed.Token;
                acc.AccountUid = TokenUtils.ParseAccountUid(refreshed.Token) ?? acc.AccountUid;
                acc.TokenUpdatedAt = DateTime.Now;
                // 服务端轮换时才覆盖；未轮换则保留旧 refreshToken
                if (refreshed.RefreshToken != null)
                {
                    acc.RefreshToken = refreshed.RefreshToken;
                    acc.RefreshExpiresAt = refreshed.RefreshExpiresAt;
                }
                try { cfg.Save(); } catch { /* 忽略 */ }
                CheckinLog(name, $"refreshToken 换新 Token 成功，有效期至 {refreshed.TokenExpiresAt:yyyy-MM-dd HH:mm}");
                return true;
            }
            CheckinLog(name, $"refreshToken 换新失败：{api.LastError}");
        }

        // 回落 Session：换取 8 小时 JWT
        if (!string.IsNullOrEmpty(acc.Session))
        {
            var renewed = await api.GetUserTokenAsync(acc.Session);
            if (!string.IsNullOrEmpty(renewed))
            {
                acc.Token = renewed;
                acc.AccountUid = TokenUtils.ParseAccountUid(renewed);
                acc.TokenUpdatedAt = DateTime.Now;
                try { cfg.Save(); } catch { /* 忽略 */ }
                CheckinLog(name, $"Session 换新 Token 成功，新 AccountUid={acc.AccountUid}");
                // 顺手把 Session 升级成 refreshToken，下次就走 180 天链路
                await TryUpgradeToRefreshTokenAsync(acc, api, name, cfg);
                return true;
            }
            CheckinLog(name, "Session 换新 Token 失败（返回为空）");
        }
        else
        {
            CheckinLog(name, "Token 失效且无 Session，无法自动换新");
        }
        return false;
    }

    /// <summary>
    /// 用 Session 换 refreshToken 并写入账号（幂等：已有未过期 refreshToken 时跳过）。
    /// 失败不影响调用方，只记日志。
    /// </summary>
    public static async Task TryUpgradeToRefreshTokenAsync(TraeAccount acc, TraeApiClient api, string name, AppConfig cfg)
    {
        if (!string.IsNullOrEmpty(acc.RefreshToken) &&
            (acc.RefreshExpiresAt == null || acc.RefreshExpiresAt > DateTime.Now)) return;
        if (string.IsNullOrEmpty(acc.Session)) return;

        var got = await api.GetRefreshTokenAsync(acc.Session);
        if (got?.RefreshToken == null)
        {
            CheckinLog(name, $"Session 换 refreshToken 失败：{api.LastError}");
            return;
        }
        acc.RefreshToken = got.RefreshToken;
        acc.RefreshExpiresAt = got.RefreshExpiresAt;
        try { cfg.Save(); } catch { /* 忽略 */ }
        CheckinLog(name, $"已取得 refreshToken，有效期至 {got.RefreshExpiresAt:yyyy-MM-dd HH:mm}");
    }

    /// <summary>
    /// 执行 claim，9074/9095 时换新设备号重签（策略见 <see cref="CheckinRetry"/>）。
    /// </summary>
    public static Task<(CheckinStatus? Result, string FailureReason)> ClaimWithDeviceRetryAsync(
        TraeAccount acc, TraeApiClient api, string displayName)
    {
        EnsureDeviceId(acc);
        return CheckinRetry.ClaimAsync(
            api, acc, NewDeviceId,
            msg => CheckinLog(displayName, msg),
            () => { try { MainViewModel.AppConfig?.Save(); } catch { /* 忽略 */ } });
    }

    /// <summary>
    /// 拉取账号资料（昵称/脱敏手机号/头像）+ 学生认证状态并回填 TraeAccount；返回是否成功。
    /// 结果按「账号·当天」内存缓存：设置页每次刷新只会联网拉一次，避免每账号每次 3 个请求。
    /// force=true 时跳过缓存（用于刚登录的账号，立即拿到最新资料）。
    /// </summary>
    public static async Task<bool> RefreshProfileAsync(TraeAccount acc, bool force = false)
    {
        var cfg = MainViewModel.AppConfig;
        var api = MainViewModel.CheckinApi;
        var name = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
        if (cfg == null || api == null || string.IsNullOrEmpty(acc.Token)) return false;

        // 当日已有缓存：直接回填，不再打接口
        lock (ProfileCacheLock)
        {
            if (!force && ProfileCache.TryGetValue(acc.Id, out var cached) && cached.Date == DateTime.Today)
            {
                acc.ScreenName = cached.ScreenName;
                acc.MobileMasked = cached.MobileMasked;
                acc.AvatarUrl = cached.AvatarUrl;
                acc.IsStudent = cached.IsStudent;
                if (string.IsNullOrWhiteSpace(acc.Name) && !string.IsNullOrEmpty(cached.ScreenName))
                    acc.Name = cached.ScreenName;
                return true;
            }
        }

        bool valid = await EnsureValidTokenAsync(acc);   // 先保证 token 可用
        if (!valid)
        {
            CheckinLog(name, "拉取账号资料失败：Token 无效且换新失败");
            return false;
        }
        var token = acc.Token ?? "";

        try
        {
            var profile = await api.GetUserProfileAsync(token, acc.Session);
            if (profile != null)
            {
                acc.ScreenName = profile.ScreenName;
                acc.MobileMasked = profile.MobileMasked;
                acc.AvatarUrl = profile.AvatarUrl;
                if (string.IsNullOrEmpty(acc.AccountUid) && !string.IsNullOrEmpty(profile.UserId))
                    acc.AccountUid = profile.UserId;
                // 未手动备注时用平台昵称充当展示名（Name 非空则保留用户备注）
                if (string.IsNullOrWhiteSpace(acc.Name) && !string.IsNullOrEmpty(profile.ScreenName))
                    acc.Name = profile.ScreenName;
                CheckinLog(name, $"资料拉取成功：昵称={profile.ScreenName}，手机={profile.MobileMasked}");
            }
            else
            {
                CheckinLog(name, "资料拉取返回 null");
            }
            int student = await api.GetStudentStatusAsync(token, acc.Session);
            acc.IsStudent = student == 1;
            try { cfg.Save(); } catch { /* 忽略 */ }

            lock (ProfileCacheLock)
            {
                ProfileCache[acc.Id] = new ProfileCacheEntry
                {
                    Date = DateTime.Today,
                    ScreenName = acc.ScreenName,
                    MobileMasked = acc.MobileMasked,
                    AvatarUrl = acc.AvatarUrl,
                    IsStudent = acc.IsStudent,
                };
            }
            return true;
        }
        catch (Exception ex)
        {
            CheckinLog(name, $"资料拉取异常：{ex.Message}");
            return false;
        }
    }

    // ---- 账号资料按天缓存（避免每次进设置页都打接口） ----

    private sealed class ProfileCacheEntry
    {
        public DateTime Date { get; init; }
        public string? ScreenName { get; init; }
        public string? MobileMasked { get; init; }
        public string? AvatarUrl { get; init; }
        public bool IsStudent { get; init; }
    }

    private static readonly object ProfileCacheLock = new();
    private static readonly Dictionary<string, ProfileCacheEntry> ProfileCache = new(StringComparer.Ordinal);

    // ==================== 数据存储目录（统一走 DataPaths）====================

    internal static readonly object HistoryIoLock = new();

    /// <summary>数据目录（数据库、签到历史、用量等），统一走 DataPaths.DataDir。</summary>
    internal static string DataDir => TraeTools.Services.DataPaths.DataDir;

    /// <summary>日志目录（各模块调试日志），统一走 DataPaths.LogsDir。</summary>
    internal static string LogsDir => TraeTools.Services.DataPaths.LogsDir;

    /// <summary>兼容旧名，指向 DataDir。</summary>
    internal static string HistoryDir => DataDir;

    private static bool _legacyHistoryCleaned;

    /// <summary>
    /// 清理旧版「每账号一份」的历史文件 history_{32位GUID}.txt（新版本统一写 history_yyyyMM.txt）。
    /// 幂等：整个进程只执行一次；读取期有锁保护。
    /// </summary>
    public static void CleanupLegacyHistoryFiles()
    {
        if (_legacyHistoryCleaned) return;
        _legacyHistoryCleaned = true;
        try
        {
            lock (HistoryIoLock)
            {
                if (!Directory.Exists(HistoryDir)) return;
                foreach (var file in Directory.GetFiles(HistoryDir, "history_*.txt"))
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(
                            Path.GetFileName(file), @"^history_[0-9a-fA-F]{32}\.txt$"))
                    {
                        try { File.Delete(file); } catch { /* 单个删除失败跳过 */ }
                    }
                }
            }
        }
        catch { /* 清理失败不影响 */ }
    }

    /// <summary>
    /// 记录一次签到成功（同时写入 SQLite 数据库与旧版 history 文本文件保持兼容）。
    /// </summary>
    public static void AppendHistory(TraeCheckin.TraeAccount acc, double gained)
    {
        var name = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name;

        // 写入 SQLite
        try
        {
            MainViewModel.CheckinDb?.InsertCheckin(DateTime.Now, acc.Id, name, gained, acc.IsMember);
        }
        catch { /* 数据库写入失败不影响签到 */ }

        // 同时写入旧版文本文件（保持兼容，后续版本可移除）
        try
        {
            lock (HistoryIoLock)
            {
                Directory.CreateDirectory(HistoryDir);
                var historyFile = Path.Combine(HistoryDir, $"history_{DateTime.Now:yyyyMM}.txt");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm} | {name} | 每日签到 | +{(int)gained}";
                File.AppendAllText(historyFile, line + Environment.NewLine);
            }
        }
        catch { /* 历史写入失败不影响签到 */ }
    }

    // ==================== 通用分类日志（排查问题用） ====================

    /// <summary>日志文件锁（独立于 HistoryIoLock，避免签到高峰争用）。</summary>
    private static readonly object LogLock = new();

    /// <summary>
    /// 写入一条分类调试日志到 {category}_log_yyyyMM.txt。
    /// 各模块按 category 归档：checkin / account / cloud / switch / usage。
    /// </summary>
    public static void AppLog(string category, string accountName, string message)
    {
        try
        {
            lock (LogLock)
            {
                var logFile = Path.Combine(LogsDir, $"{category}_log_{DateTime.Now:yyyyMM}.txt");
                var name = string.IsNullOrEmpty(accountName) ? "?" : accountName;
                File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{name}] {message}{Environment.NewLine}");
            }
        }
        catch { /* 日志写入失败不影响业务 */ }
    }

    /// <summary>签到日志快捷方法（兼容已有调用）。</summary>
    public static void CheckinLog(string accountName, string message)
        => AppLog("checkin", accountName, message);
}
