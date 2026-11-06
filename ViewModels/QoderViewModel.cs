using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTools.Services.Qoder;

namespace TraeTools.ViewModels;

/// <summary>Qoder 页展示用的账号行（把存储模型摊平成可绑定的展示字段）。</summary>
public partial class QoderAccountRow : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Initial { get; init; } = "?";
    public string Domain { get; init; } = "";
    public string PlanText { get; init; } = "—";

    [ObservableProperty]
    private string _creditsText = "—";

    [ObservableProperty]
    private string _statusText = "未签到";

    [ObservableProperty]
    private string _statusType = "warn";

    [ObservableProperty]
    private bool _isCurrent;
}

/// <summary>
/// Qoder 页：设备码授权登录 + 活动签到 + 积分（额度）展示。
///
/// <para>
/// 登录完全本地发起：生成 PKCE 与 nonce，拼授权页 URL 交给浏览器，然后轮询
/// <c>/api/v1/deviceToken/poll</c>。轮询返回 202/404 都表示「用户还没点授权」，
/// 必须当 pending 继续等，不能当失败。
/// </para>
/// <para>
/// 签到走活动平台 <c>/sash/api/v1/me/campaigns</c>：只领积分类且状态为 CLAIMABLE 的活动。
/// 逐账号顺序执行——并发会踩上游风控。
/// </para>
/// </summary>
public partial class QoderViewModel : ViewModelBase
{
    private readonly QoderStore _store = QoderStore.Load();
    private readonly QoderApiClient? _api = MainViewModel.QoderApi;

    /// <summary>设备码登录的轮询间隔（上游授权页通常几秒内就能点完）。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);

    public ObservableCollection<QoderAccountRow> Accounts { get; } = new();

    [ObservableProperty]
    private QoderAccountRow? _selectedAccount;

    [ObservableProperty]
    private string _loginState = "未登录任何 Qoder 账号";

    [ObservableProperty]
    private string _loginHint = "";

    /// <summary>是否有可重新打开的授权页链接（无链接时隐藏该按钮）。</summary>
    public bool HasLoginHint => !string.IsNullOrEmpty(LoginHint);

    partial void OnLoginHintChanged(string value) => OnPropertyChanged(nameof(HasLoginHint));

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "点击「添加 Qoder 账号」开始设备码授权";

    [ObservableProperty]
    private string _checkinButtonText = "一键签到";

    [ObservableProperty]
    private string _creditsSummary = "—";

    [ObservableProperty]
    private string _planSummary = "—";

    /// <summary>当前选中的登录区域（默认国内）。</summary>
    [ObservableProperty]
    private int _regionIndex;

    public QoderViewModel()
    {
        Reload();
    }

    /// <summary>重建账号列表（读存储，不动网络）。</summary>
    public void Reload()
    {
        var keepId = SelectedAccount?.Id;
        Accounts.Clear();
        foreach (var acc in _store.Accounts)
        {
            Accounts.Add(BuildRow(acc));
        }
        if (Accounts.Count == 0)
        {
            LoginState = "未登录任何 Qoder 账号";
            CreditsSummary = "—";
            PlanSummary = "—";
            CheckinButtonText = "一键签到";
            SelectedAccount = null;
            return;
        }
        LoginState = $"已登录 {Accounts.Count} 个 Qoder 账号";
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == keepId) ?? Accounts[0];
        RefreshSummaries();
    }

    /// <summary>
    /// 进入页面时补拉还没取过的积分与套餐。只对缺数据的账号发请求，
    /// 已有数据的账号不重复打上游——这个动作每进一次页面都会跑。
    /// </summary>
    public async Task LoadCreditsAsync()
    {
        var api = _api;
        if (api == null || IsBusy) return;
        var pending = _store.Accounts.Where(a => a.Remaining < 0 || string.IsNullOrEmpty(a.Plan)).ToList();
        if (pending.Count == 0) return;

        IsBusy = true;
        try
        {
            foreach (var acc in pending)
            {
                if (!await EnsureTokenAsync(acc, api)) continue;
                var quota = await api.FetchQuotaAsync(acc);
                if (quota == null) continue;
                acc.Remaining = quota.Remaining;
                acc.Used = quota.Used;
                acc.Total = quota.Total;
                acc.IsExceeded = quota.IsExceeded;
                var plan = await api.FetchPlanAsync(acc);
                if (!string.IsNullOrEmpty(plan)) acc.Plan = plan;
            }
            _store.Save();
            Reload();
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedAccountChanged(QoderAccountRow? value) => RefreshSummaries();

    private void RefreshSummaries()
    {
        if (SelectedAccount == null)
        {
            CreditsSummary = "—";
            PlanSummary = "—";
            return;
        }
        var acc = _store.Accounts.FirstOrDefault(a => a.Id == SelectedAccount.Id);
        if (acc == null)
        {
            CreditsSummary = "—";
            PlanSummary = "—";
            return;
        }
        CreditsSummary = acc.Remaining < 0 ? "未获取" : $"{acc.Remaining:0.##}";
        PlanSummary = string.IsNullOrEmpty(acc.Plan) ? "—" : acc.Plan!;
        CheckinButtonText = acc.LastClaimAt is { } t && t.Date == DateTime.Now.Date
            ? "今日已领取 ✓"
            : "一键签到";
    }

    private static QoderAccountRow BuildRow(QoderAccount acc)
    {
        var name = string.IsNullOrEmpty(acc.Name)
            ? "账号@" + (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id)
            : acc.Name!;
        var todayClaimed = acc.LastClaimAt is { } t && t.Date == DateTime.Now.Date;
        return new QoderAccountRow
        {
            Id = acc.Id,
            Name = name,
            Initial = name.Length > 0 ? name[..1] : "?",
            Domain = acc.Domain,
            PlanText = string.IsNullOrEmpty(acc.Plan) ? "—" : acc.Plan!,
            CreditsText = acc.Remaining < 0 ? "—" : $"{acc.Remaining:0.##}",
            StatusText = todayClaimed ? "今日已领取" : "未签到",
            StatusType = todayClaimed ? "ok" : "warn",
        };
    }

    /// <summary>
    /// 添加账号：本地发起设备码授权 → 开浏览器 → 轮询直到拿到令牌 → 拉 userinfo 建档。
    /// 会话状态由 <see cref="QoderLogin"/> 持有（发起与轮询是两次独立的 UI 动作）。
    /// </summary>
    [RelayCommand]
    private async Task AddAccountAsync()
    {
        if (IsBusy) return;
        var api = _api;
        if (api == null)
        {
            StatusMessage = "Qoder 服务未初始化";
            return;
        }
        var region = RegionIndex == 0 ? QoderRegion.Cn : QoderRegion.Global;
        IsBusy = true;
        StatusMessage = "正在发起设备码授权…";
        LoginHint = "";
        QoderLoginSession? session = null;
        try
        {
            session = QoderLogin.Start(region);
            StatusMessage = "已在浏览器打开授权页，请在其中选择并确认账号";
            LoginHint = session.AuthorizeUrl;
            QoderLogin.OpenInBrowser(session.AuthorizeUrl);

            var deadline = DateTime.Now + QoderLogin.Timeout;
            while (DateTime.Now < deadline)
            {
                await Task.Delay(PollInterval);
                var polled = await QoderLogin.PollAsync(api, region, session.LoginId);
                if (polled == null)
                {
                    StatusMessage = api.TakeLastError() ?? "等待你在浏览器中完成授权…";
                    continue;
                }
                if (polled.Error != null)
                {
                    StatusMessage = "登录失败：" + polled.Error;
                    return;
                }
                if (polled.Token == null || polled.UserInfo == null) continue;

                _store.Upsert(polled.UserInfo, region, polled.Token);
                _store.Save();
                Reload();
                StatusMessage = $"已添加账号：{polled.UserInfo.Name ?? polled.UserInfo.Id}";
                LoginHint = "";
                return;
            }
            StatusMessage = "授权超时，请重新点击「添加账号」";
        }
        catch (Exception ex)
        {
            StatusMessage = "登录失败：" + ex.Message;
        }
        finally
        {
            // 会话里存着 PKCE verifier，用完必须释放；中途异常退出也一样。
            if (session != null) QoderLogin.Cancel(session.LoginId);
            IsBusy = false;
        }
    }

    /// <summary>
    /// 令牌过期就自动续期：能刷就刷（drt-/jrt-/PAT），刷不了才要求重新登录。
    /// 调用方只在拿到失败结果后调用一次，不做重试循环——刷不动的凭据重试也只是白打上游。
    /// </summary>
    private async Task<bool> EnsureTokenAsync(QoderAccount account, QoderApiClient api)
    {
        if (account.ExpiresAt is { } at && at > DateTime.Now && account.HasToken) return true;
        var refreshed = await QoderRefresh.RefreshAsync(api, account);
        if (refreshed == null) return false;
        _store.SetTokens(account.Id, refreshed);
        _store.Save();
        return true;
    }

    /// <summary>刷新当前选中账号的积分与套餐。</summary>
    [RelayCommand]
    private async Task RefreshCreditsAsync()
    {
        var api = _api;
        var target = SelectedAccount == null
            ? null
            : _store.Accounts.FirstOrDefault(a => a.Id == SelectedAccount.Id);
        if (api == null || target == null)
        {
            StatusMessage = "请先选择一个账号";
            return;
        }
        IsBusy = true;
        try
        {
            if (!await EnsureTokenAsync(target, api))
            {
                StatusMessage = "令牌已失效且无法自动续期，请重新登录：" + (api.LastError ?? "没有可用的刷新凭证");
                return;
            }
            await RefreshQuotaAsync(target, api);
            StatusMessage = target.Remaining < 0
                ? api.LastError ?? "积分查询失败"
                : $"积分已刷新：剩余 {target.Remaining:0.##}";
            _store.Save();
            Reload();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 一键签到：对每个账号顺序拉活动列表，领取所有可领的积分类活动。
    /// 「今天已领」由上游 claimStatus 判定，本地只做展示。
    /// </summary>
    [RelayCommand]
    private async Task DoCheckinAsync()
    {
        if (IsBusy) return;
        var api = _api;
        if (api == null)
        {
            StatusMessage = "Qoder 服务未初始化";
            return;
        }
        if (_store.Accounts.Count == 0)
        {
            StatusMessage = "没有可签到的 Qoder 账号，请先添加账号";
            return;
        }
        IsBusy = true;
        CheckinButtonText = "签到中…";
        var ok = 0;
        var failed = 0;
        var claimedCredits = 0.0;
        var details = new List<string>();
        try
        {
            foreach (var acc in _store.Accounts.ToList())
            {
                var name = string.IsNullOrEmpty(acc.Name) ? acc.Id[..Math.Min(6, acc.Id.Length)] : acc.Name!;
                if (!acc.HasToken && !acc.Shape().CanRefresh)
                {
                    failed++;
                    details.Add($"{name}：缺少令牌，请重新登录");
                    continue;
                }
                if (!await EnsureTokenAsync(acc, api))
                {
                    failed++;
                    details.Add($"{name}：令牌续期失败（{api.LastError ?? "没有可用的刷新凭证"}）");
                    continue;
                }
                var campaigns = await api.FetchCampaignsAsync(acc);
                if (campaigns == null)
                {
                    failed++;
                    details.Add($"{name}：{api.LastError ?? "活动列表不可用"}");
                    continue;
                }
                var claimable = campaigns.Where(c => c.Classify() == QoderCampaignClass.Claimable).ToList();
                var already = campaigns.Where(c => c.Classify() == QoderCampaignClass.Already).ToList();
                var blocked = campaigns
                    .Where(c => c.Classify() is QoderCampaignClass.Pending or QoderCampaignClass.Locked)
                    .ToList();

                if (claimable.Count == 0)
                {
                    if (already.Count > 0)
                    {
                        ok++;
                        acc.LastClaimMessage = "今日已领取";
                        acc.LastClaimAt = DateTime.Now;
                        details.Add(string.IsNullOrEmpty(already[0].Title)
                            ? $"{name}：今日已领取"
                            : $"{name}：今日已领取（{already[0].Title}）");
                    }
                    else if (blocked.Count > 0)
                    {
                        // 有活动但被库存/前置条件挡住——不是「今天没活动」，得说清是哪一种。
                        failed++;
                        var reason = QoderCampaign.FailureReason(blocked[0].UnavailableReason)
                                     ?? blocked[0].UnavailableReason
                                     ?? "暂不可领取";
                        details.Add($"{name}：{reason}");
                    }
                    else
                    {
                        failed++;
                        details.Add($"{name}：暂无可领取的活动");
                    }
                    await RefreshQuotaAsync(acc, api);
                    continue;
                }

                var got = 0.0;
                var any = false;
                var blockedCount = 0;
                foreach (var c in claimable)
                {
                    var result = await api.ClaimCampaignAsync(acc, c.Id);
                    if (result.Ok)
                    {
                        any = true;
                        got += c.Amount;
                        details.Add($"{name}：已领取「{c.Title}」{c.Amount:0.##}");
                    }
                    else if (result.Blocked)
                    {
                        blockedCount++;
                        details.Add($"{name}：{result.Message}");
                    }
                    else
                    {
                        details.Add($"{name}：「{c.Title}」领取失败 —— {result.Message}");
                    }
                    await Task.Delay(1000);   // 上游要求领取间隔，连点会踩风控
                }
                if (any)
                {
                    ok++;
                    claimedCredits += got;
                    acc.LastClaimMessage = $"已领取 {got:0.##}";
                    acc.LastClaimAt = DateTime.Now;
                    await RefreshQuotaAsync(acc, api);
                }
                else if (blockedCount > 0)
                {
                    // 同人已领：本账号今天确实领不到，但不是它自己的问题，按已处理记。
                    ok++;
                    acc.LastClaimMessage = "同人已领取";
                    acc.LastClaimAt = DateTime.Now;
                    await RefreshQuotaAsync(acc, api);
                }
                else
                {
                    failed++;
                    await RefreshQuotaAsync(acc, api);
                }
            }
            _store.Save();
            Reload();
            StatusMessage = failed == 0
                ? $"全部完成：{ok} 个账号，本次共领取 {claimedCredits:0.##} 积分"
                : $"部分完成：{ok} 个成功、{failed} 个失败（{string.Join("；", details)}）";
        }
        catch (Exception ex)
        {
            StatusMessage = "签到异常：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshSummaries();
        }
    }

    /// <summary>
    /// 拉一次积分与套餐并写回账号。失败只记在 <see cref="QoderApiClient.LastError"/>，
    /// 保留账号原有的数值——签到成不成功与积分显示是两件事，不能因为积分没拉到就判签到失败。
    /// </summary>
    private async Task RefreshQuotaAsync(QoderAccount acc, QoderApiClient api)
    {
        var quota = await api.FetchQuotaAsync(acc);
        if (quota != null)
        {
            acc.Remaining = quota.Remaining;
            acc.Used = quota.Used;
            acc.Total = quota.Total;
            acc.IsExceeded = quota.IsExceeded;
        }
        var plan = await api.FetchPlanAsync(acc);
        if (!string.IsNullOrEmpty(plan)) acc.Plan = plan;
    }

    /// <summary>
    /// 自动签到检查：到点、启用且当天未触发时，对所有 Qoder 账号执行一次签到。
    /// 与 Trae 的自动签到彼此独立——两者时间不同，各自的开关也不同。
    /// </summary>
    public async Task<bool> TryAutoCheckinAsync()
    {
        var cfg = MainViewModel.AppConfig;
        if (cfg == null || !cfg.QoderAutoCheckinEnabled) return false;
        if (string.IsNullOrWhiteSpace(cfg.QoderAutoCheckinTime)) return false;
        if (!TimeSpan.TryParse(cfg.QoderAutoCheckinTime, out var due)) return false;
        if (DateTime.Now.TimeOfDay < due) return false;
        if (_lastAutoCheckDate == DateTime.Today) return false;
        if (_store.Accounts.Count == 0) return false;
        if (_api == null || IsBusy) return false;   // 服务未就绪或正在手动签到，留到下一轮 tick
        _lastAutoCheckDate = DateTime.Today;   // 到点即置位，本日不再重复触发

        AccountHelpers.AppLog("qoder", "", $"自动签到开始（计划时间 {cfg.QoderAutoCheckinTime}）");
        await DoCheckinAsync();
        AccountHelpers.AppLog("qoder", "", $"自动签到结束：{StatusMessage}");
        return true;
    }

    /// <summary>自动签到上次已触发日期（每日仅触发一次）。</summary>
    private static DateTime _lastAutoCheckDate = DateTime.MinValue;

    /// <summary>删除当前选中的账号（只删本地凭据，不影响 Qoder 侧登录态）。</summary>
    [RelayCommand]
    private void RemoveAccount()
    {
        if (SelectedAccount == null) return;
        if (!_store.Remove(SelectedAccount.Id)) return;
        _store.Save();
        Reload();
        StatusMessage = "已删除账号";
    }

    /// <summary>重新打开授权页（轮询已超时或浏览器被关掉时用）。</summary>
    [RelayCommand]
    private void ReopenLoginPage()
    {
        if (string.IsNullOrEmpty(LoginHint)) return;
        QoderLogin.OpenInBrowser(LoginHint);
    }
}
