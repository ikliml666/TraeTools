using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeCheckin;
using TraeSwitch.Services;
using TraeTools.Services;
using TraeTools.Services.Qoder;

namespace TraeTools.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly CheckinViewModel _checkin;
    private readonly SwitchViewModel _switch;
    private readonly CloudViewModel _cloud;
    private readonly QoderViewModel _qoder;
    private readonly SettingsViewModel _settings;
    private readonly LogViewModel _log;

    [ObservableProperty]
    private ViewModelBase? _currentPage;

    [ObservableProperty]
    private string _currentPageKey = "checkin";

    // 全局服务容器：页面 VM 通过静态属性访问
    public static AppConfig? AppConfig;
    public static TraeApiClient? CheckinApi;
    public static QoderApiClient? QoderApi;
    public static SettingsStore? SwitchSettings;
    public static VaultService? Vault;
    public static GitHubApiClient? GitHubApi;
    public static CheckinDatabase? CheckinDb;

    /// <summary>界面展示的应用版本号，与"关于/更新检查"（TraeTools.Services.AppVersion）同源。</summary>
    public string AppVersion => Services.AppVersion.Display;

    public MainViewModel()
    {
        // 初始化真实服务（try-catch，失败则为 null，页面 VM 用 mock 兜底）
        try
        {
            AppConfig = AppConfig.Load();
        }
        catch
        {
            AppConfig = new AppConfig();
        }

        try
        {
            CheckinApi = new TraeApiClient();
        }
        catch
        {
            CheckinApi = null;
        }

        try
        {
            QoderApi = new QoderApiClient();
        }
        catch
        {
            QoderApi = null;
        }

        try
        {
            SwitchSettings = new SettingsStore(CarrierDefaults.SettingsDir);
        }
        catch
        {
            SwitchSettings = null;
        }

        try
        {
            if (SwitchSettings != null)
            {
                var vaultRoot = TraeTools.Services.DataPaths.VaultDir;
                Vault = new VaultService(SwitchSettings.Data.RootDir, vaultRoot);
            }
        }
        catch
        {
            Vault = null;
        }

        try
        {
            GitHubApi = new GitHubApiClient();
        }
        catch
        {
            GitHubApi = null;
        }

        try
        {
            CheckinDb = new CheckinDatabase();
            // 首次启动时从旧版文本文件迁移数据到数据库
            // DataPaths.Migrate() 已在 Program.cs 中先行调用，文件已搬入新目录
            // 这里扫描新目录（DataDir）即可；同时兼容扫描旧目录以防迁移未执行
            int migrated = 0;
            var newDataDir = TraeTools.Services.DataPaths.DataDir;
            var oldBaseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TraeCheckin");
            var oldDataDir = Path.Combine(oldBaseDir, "data");
            foreach (var dir in new[] { newDataDir, oldBaseDir, oldDataDir })
            {
                if (Directory.Exists(dir))
                    migrated += CheckinDb.MigrateFromHistoryFiles(dir)
                              + CheckinDb.MigrateFromSnapshotFiles(dir);
            }
            if (migrated > 0)
                AccountHelpers.AppLog("account", "", $"从旧版文本文件迁移了 {migrated} 条记录到数据库");
        }
        catch
        {
            CheckinDb = null;
        }

        Instance = this;
        _checkin = new CheckinViewModel();
        _switch = new SwitchViewModel();
        _cloud = new CloudViewModel();
        _qoder = new QoderViewModel();
        _settings = new SettingsViewModel();
        _settings.LoadExtendedSettings();   // 载入本 PR 新增的推送/日志/主题/设备号配置
        // 设置页下拉改主题时，标题栏 ☾/☀ 图标同步刷新
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ThemeMode))
                OnPropertyChanged(nameof(IsDarkMode));
        };
        _log = new LogViewModel();

        _checkin.NavigateRequested += p => Navigate(p);
        _switch.NavigateRequested += p => Navigate(p);
        _cloud.NavigateRequested += p => Navigate(p);
        _qoder.NavigateRequested += p => Navigate(p);
        _settings.NavigateRequested += p => Navigate(p);
        _log.NavigateRequested += p => Navigate(p);

        // 顶栏账号下拉：以 Trae 账号为真源填充，并落在当前激活账号上
        RefreshTopBar();

        CurrentPage = _checkin;
        CurrentPageKey = "checkin";

        // 启动时把所有账号的 Session 一次性升级成 refreshToken（幂等，已有未过期的会跳过）。
        // 不做这一步的话，只有被刷新过的账号能拿到 180 天链路，其余仍在 14 天后失效。
        _ = UpgradeAllRefreshTokensAsync();

        // 启动日志：记录账号概况，便于排查问题
        try
        {
            if (AppConfig != null)
            {
                var accs = AppConfig.Accounts;
                AccountHelpers.AppLog("account", "", $"===== 程序启动，共 {accs.Count} 个账号 =====");
                foreach (var a in accs)
                {
                    var n = string.IsNullOrEmpty(a.Name) ? (a.Id.Length > 6 ? a.Id[..6] : a.Id) : a.Name!;
                    AccountHelpers.AppLog("account", n, $"DeviceId={a.DeviceId}，Token={(string.IsNullOrEmpty(a.Token) ? "无" : $"有({a.Token!.Length})")}，Enabled={a.Enabled}，LastCheckin={a.LastCheckinDate?.ToString("MM-dd HH:mm") ?? "无"}");
                }
            }
        }
        catch { /* 启动日志失败不影响 */ }
    }

    /// <summary>启动时把全部账号的 Session 升级成 refreshToken，让每个账号都拿到 180 天链路。</summary>
    private static async Task UpgradeAllRefreshTokensAsync()
    {
        var cfg = AppConfig;
        var api = CheckinApi;
        if (cfg == null || api == null) return;
        foreach (var acc in cfg.Accounts.ToList())
        {
            var name = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
            await AccountHelpers.TryUpgradeToRefreshTokenAsync(acc, api, name, cfg);
        }
    }

    public bool IsCheckinActive => CurrentPageKey == "checkin";
    public bool IsCloudActive => CurrentPageKey == "cloud";
    public bool IsQoderActive => CurrentPageKey == "qoder";
    public bool IsSettingsActive => CurrentPageKey == "settings";
    public bool IsLogActive => CurrentPageKey == "log";

    /// <summary>标题栏显示的当前页面名。</summary>
    public string CurrentPageTitle => CurrentPageKey switch
    {
        "cloud" => "云端签到",
        "qoder" => "Qoder",
        "log" => "运行日志",
        "settings" => "设置",
        _ => "每日签到",
    };

    /// <summary>标题栏 ☾/☀ 图标状态：跟随当前生效主题实时变化（设置页下拉改动也会同步）。</summary>
    public bool IsDarkMode => ThemeService.IsDark;

    /// <summary>标题栏快捷开关：在明亮 / 深色之间切换；「跟随系统」仍可在设置页选择。</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        _settings.ThemeMode = ThemeService.IsDark ? ThemeService.Light : ThemeService.Dark;
        OnPropertyChanged(nameof(IsDarkMode));
    }

    partial void OnCurrentPageKeyChanged(string value)
    {
        OnPropertyChanged(nameof(IsCheckinActive));
        OnPropertyChanged(nameof(IsCloudActive));
        OnPropertyChanged(nameof(IsQoderActive));
        OnPropertyChanged(nameof(IsSettingsActive));
        OnPropertyChanged(nameof(IsLogActive));
        OnPropertyChanged(nameof(CurrentPageTitle));
    }

    /// <summary>账号管理切换激活账号后：签到页 + 账号状态同步 + 顶栏刷新。</summary>
    public static async void NotifyActiveAccountChanged()
    {
        if (Instance is not { } vm) return;
        try
        {
            vm._checkin.Reload();
            await vm._settings.RefreshAccountStatesAsync();
            vm.RefreshTopBar();   // 顶栏「当前生效」文案与下拉选中项随之更新
        }
        catch { /* 异步刷新失败不应导致崩溃 */ }
    }

    /// <summary>签到完成后同步账号状态。</summary>
    public static async Task NotifyAsyncRefresh()
    {
        if (Instance is not { } vm) return;
        await vm._settings.RefreshAccountStatesAsync();
    }

    /// <summary>当前 VM 实例（供静态通知使用）。</summary>
    public static MainViewModel? Instance { get; private set; }

    /// <summary>由 MainWindow 定时器定时调用：到点自动签到（TraeCheckin「每日自动签到」能力）。</summary>
    public Task<bool> AutoCheckinIfDue() => _checkin.TryAutoCheckinAsync();

    /// <summary>由 MainWindow 定时器定时调用：到点自动执行 Qoder 签到（与 Trae 各自独立）。</summary>
    public Task<bool> QoderAutoCheckinIfDue() => _qoder.TryAutoCheckinAsync();

    /// <summary>托盘「立即签到」：对当前账号执行一次签到（不弹窗，由调用方提示结果）。</summary>
    public async Task<bool> QuickCheckinAsync()
    {
        try
        {
            var cfg = AppConfig;
            var api = CheckinApi;
            if (cfg == null || api == null) return false;
            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg.Accounts.FirstOrDefault();
            if (acc == null || string.IsNullOrEmpty(acc.Token)) return false;
            var name = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
            AccountHelpers.CheckinLog(name, $"[托盘快签] 开始签到，DeviceId={acc.DeviceId}");
            var (result, claimFailure) = await AccountHelpers.ClaimWithDeviceRetryAsync(acc, api, name);
            if (result == null)
            {
                AccountHelpers.CheckinLog(name, $"[托盘快签] Claim 失败：{claimFailure}");
                return false;
            }
            acc.LastCheckinDate = DateTime.Now;
            cfg.LastCheckinDate = DateTime.Now;
            try
            {
                // 解析本次所得并写入签到历史（与签到页/自动签到同口径）
                var after = await api.GetStatusAsync(acc.Token, acc.DeviceId);
                double gained = TraeCheckin.CheckinEvaluator.ResolveGainedCredits(after ?? result, acc.IsMember);
                AccountHelpers.AppendHistory(acc, gained);
                AccountHelpers.CheckinLog(name, $"[托盘快签] 签到成功，获得 {gained} 积分");
            }
            catch { /* 历史写入失败不影响 */ }
            try
            {
                var credits = await api.GetRemainingCreditsAsync(acc.Token, acc.DeviceId);
                if (credits >= 0)
                {
                    acc.RemainingCredits = credits;   // 按账号持久化
                    cfg.LastRemaining = credits;
                }
            }
            catch { /* 积分刷新失败不影响签到结果 */ }
            try { cfg.Save(); } catch { /* 忽略 */ }
            return true;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        switch (page)
        {
            case "checkin":
                _checkin.Reload();     // 同上，签到页跟随激活账号
                CurrentPage = _checkin;
                CurrentPageKey = "checkin";
                break;
            case "cloud":
                CurrentPage = _cloud;
                CurrentPageKey = "cloud";
                break;
            case "qoder":
                _qoder.Reload();
                _ = _qoder.LoadCreditsAsync();   // 还没取过积分的账号在进页面时补拉一次
                CurrentPage = _qoder;
                CurrentPageKey = "qoder";
                break;
            case "settings":
                _settings.LoadExtendedSettings();   // 每次进入都从配置重读，避免多页面改配置后界面过期
                _ = _settings.RefreshAccountStatesAsync();
                CurrentPage = _settings;
                CurrentPageKey = "settings";
                break;
            case "log":
                // 进页面时重建「最近」内存缓冲并重扫磁盘文件（日志文件可能被其它进程追加）
                _log.ReloadRecent();
                _log.RefreshFiles();
                CurrentPage = _log;
                CurrentPageKey = "log";
                break;
        }
    }
}