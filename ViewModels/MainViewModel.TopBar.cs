using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TraeTools.Models;

namespace TraeTools.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> 的增量部分：顶栏显示的当前生效账号。
/// 账号切换（下拉 + 切换按钮）已移除，切号统一在「设置」页的账号管理里做。
/// </summary>
public partial class MainViewModel
{
    /// <summary>当前真正生效（激活）的账号文案，用于顶栏提示；无账号时给出明确说明。</summary>
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

    /// <summary>刷新顶栏展示。账号增删、激活账号变化后调用（调用方负责在 UI 线程上调用）。</summary>
    public void RefreshTopBar() => OnPropertyChanged(nameof(TopActiveAccountText));
}
