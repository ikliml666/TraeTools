using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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

    /// <summary>设备码登录的轮询间隔与总时长（与上游授权页的等待窗口一致）。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(10);

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
        SelectedAccount = Accounts[0];
        RefreshSummaries();
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
    /// 添加账号：本地生成 PKCE/nonce → 开浏览器授权页 → 轮询直到拿到令牌 → 拉 userinfo 建档。
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
        try
        {
            var machineId = QoderIdentity.ResolveMachineId(region);
            var (verifier, challenge) = QoderIdentity.Pkce();
            var nonce = QoderIdentity.Nonce(region);
            var url = QoderApiClient.BuildAuthorizeUrl(region, machineId, nonce, challenge);

            StatusMessage = "已在浏览器打开授权页，请在其中选择并确认账号";
            LoginHint = url;
            OpenInBrowser(url);

            var deadline = DateTime.Now + PollTimeout;
            while (DateTime.Now < deadline)
            {
                await Task.Delay(PollInterval);
                var token = await api.PollDeviceTokenAsync(region, nonce, verifier);
                if (token == null)
                {
                    StatusMessage = api.TakeLastError() ?? "等待你在浏览器中完成授权…";
                    continue;
                }

                StatusMessage = "已取得令牌，正在读取账号信息…";
                var info = await api.FetchUserInfoAsync(region, token.AccessToken);
                if (info == null)
                {
                    StatusMessage = api.LastError ?? "读取账号信息失败";
                    return;
                }
                _store.Upsert(info, region, token);
                _store.Save();
                Reload();
                StatusMessage = $"已添加账号：{info.Name ?? info.Id}";
                return;
            }
            StatusMessage = "授权超时，请重新点击「添加 Qoder 账号」";
        }
        catch (Exception ex)
        {
            StatusMessage = "登录失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
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
            var quota = await api.FetchQuotaAsync(target);
            if (quota != null)
            {
                target.Remaining = quota.Remaining;
                target.Used = quota.Used;
                target.Total = quota.Total;
                target.IsExceeded = quota.IsExceeded;
                StatusMessage = $"积分已刷新：剩余 {quota.Remaining:0.##}";
            }
            else
            {
                StatusMessage = api.LastError ?? "积分查询失败";
            }
            var plan = await api.FetchPlanAsync(target);
            if (!string.IsNullOrEmpty(plan)) target.Plan = plan;
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
                if (!acc.HasToken)
                {
                    failed++;
                    details.Add($"{name}：缺少令牌，请重新登录");
                    continue;
                }
                var campaigns = await api.FetchCampaignsAsync(acc);
                if (campaigns == null)
                {
                    failed++;
                    details.Add($"{name}：{api.LastError ?? "活动列表不可用"}");
                    continue;
                }
                var claimable = campaigns.Where(c => c.IsCreditKind && c.IsClaimable).ToList();
                var already = campaigns.Where(c => c.IsCreditKind && c.IsClaimed).ToList();

                if (claimable.Count == 0)
                {
                    if (already.Count > 0)
                    {
                        ok++;
                        acc.LastClaimMessage = "今日已领取";
                        acc.LastClaimAt = DateTime.Now;
                        details.Add($"{name}：今日已领取（{already[0].Title}）");
                    }
                    else
                    {
                        failed++;
                        details.Add($"{name}：暂无可领取的活动");
                    }
                    continue;
                }

                var got = 0.0;
                var any = false;
                foreach (var c in claimable)
                {
                    var result = await api.ClaimCampaignAsync(acc, c.Id);
                    if (result.Ok)
                    {
                        any = true;
                        got += c.Amount;
                        details.Add($"{name}：已领取「{c.Title}」{c.Amount:0.##}");
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
                    var quota = await api.FetchQuotaAsync(acc);
                    if (quota != null) acc.Remaining = quota.Remaining;
                }
                else
                {
                    failed++;
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
        OpenInBrowser(LoginHint);
    }

    private static void OpenInBrowser(string url)
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
