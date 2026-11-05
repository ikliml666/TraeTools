using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTools.Models;

namespace TraeTools.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> 的增量部分：顶栏的「当前账号 + 下拉切换」。
///
/// 上游原本只能在「账号切换」页里选账号再点切换；本文件把同一套能力提到顶栏，
/// 任何页面都能一眼看到当前账号并直接切走。
///
/// 关键约束：**不另起一套切换实现**——账号列表与选中项直接复用上游
/// <see cref="SwitchViewModel"/> 的集合与选中属性，执行也走它既有的
/// SwitchAccount 命令（内部复用 TraeSwitch 的 SwitcherService 冷切换编排）。
/// </summary>
public partial class MainViewModel
{
    private AccountInfo? _topSelectedAccount;

    /// <summary>
    /// 顶栏下拉的账号列表：以 Trae 账号为真源（与签到页同一份），
    /// 保证下拉里显示的就是用户实际管理的账号，而不是内部的建档名。
    /// </summary>
    public ObservableCollection<AccountInfo> TopAccounts { get; } = new();

    /// <summary>
    /// 顶栏下拉当前选中的账号。写入时按名字桥接到切换服务的建档账号
    /// （冷切换执行的是建档备份，两边的账号标识是两套命名）。
    /// </summary>
    public AccountInfo? TopSelectedAccount
    {
        get => _topSelectedAccount;
        set
        {
            if (!SetProperty(ref _topSelectedAccount, value)) return;
            var matched = value == null
                ? null
                : _switch.Accounts.FirstOrDefault(a =>
                    string.Equals(a.Name, value.Name, StringComparison.OrdinalIgnoreCase));
            _switch.SelectedAccount = matched;
            OnPropertyChanged(nameof(TopSwitchHint));
        }
    }

    /// <summary>目标账号没有建档备份时的提示（顶栏据此禁用「切换」并给出原因）。</summary>
    public string? TopSwitchHint =>
        TopSelectedAccount == null ? null
        : _switch.SelectedAccount == null ? "该账号尚未建档，无法冷切换" : null;

    /// <summary>「切换」按钮是否可用。</summary>
    public bool TopCanSwitch => TopSelectedAccount != null && _switch.SelectedAccount != null && !_switch.IsSwitching;

    /// <summary>当前真正生效（激活）的账号文案，用于顶栏右侧提示；无账号时给出明确说明。</summary>
    public string TopActiveAccountText
    {
        get
        {
            var cfg = AppConfig;
            if (cfg == null || cfg.Accounts.Count == 0) return "尚未添加账号";
            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId) ?? cfg.Accounts[0];
            var name = string.IsNullOrEmpty(acc.Name) ? $"账号@{acc.Id[..Math.Min(6, acc.Id.Length)]}" : acc.Name;
            return $"当前生效：{name}";
        }
    }

    /// <summary>切换是否进行中（顶栏按钮据此禁用，避免并发切换）。</summary>
    public bool TopIsSwitching => _switch.IsSwitching;

    /// <summary>
    /// 刷新顶栏展示。账号增删、激活账号变化后调用（调用方负责在 UI 线程上调用）。
    /// 重建下拉集合，并把选中项落在当前激活账号上。
    /// </summary>
    public void RefreshTopBar()
    {
        var cfg = AppConfig;
        TopAccounts.Clear();
        if (cfg != null)
        {
            foreach (var a in cfg.Accounts)
            {
                var name = string.IsNullOrEmpty(a.Name)
                    ? (a.Id.Length > 6 ? a.Id[..6] : a.Id)
                    : a.Name!;
                TopAccounts.Add(new AccountInfo
                {
                    Id = a.Id,
                    Name = name,
                    Initial = name.Length > 0 ? name[..1] : "?",
                    IsCurrent = a.Id == cfg.ActiveAccountId,
                });
            }
        }

        TopSelectedAccount = TopAccounts.FirstOrDefault(a => a.IsCurrent) ?? TopAccounts.FirstOrDefault();
        OnPropertyChanged(nameof(TopActiveAccountText));
        OnPropertyChanged(nameof(TopIsSwitching));
        OnPropertyChanged(nameof(TopCanSwitch));
    }

    /// <summary>
    /// 顶栏「切换」：直接执行上游既有的冷切换命令。
    /// 切换进度写入运行日志（SwitchViewModel 内部走 AccountHelpers.AppLog("switch", …)）。
    /// </summary>
    [RelayCommand]
    private async Task SwitchToSelectedAccountAsync()
    {
        try
        {
            if (TopSelectedAccount == null) return;

            OnPropertyChanged(nameof(TopIsSwitching));
            await _switch.SwitchAccountCommand.ExecuteAsync(null);
        }
        catch { /* 失败原因由运行日志呈现 */ }
        finally
        {
            RefreshTopBar();
        }
    }
}
