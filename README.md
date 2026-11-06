> **关于本仓库**：上游 [star620/TraeTools](https://github.com/star620/TraeTools) 的主维护者「星梦」因学业繁忙，已于 2026/10/23 起停止维护。本仓库是该项目的个人 fork，仅供自用，不承诺长期维护、也不保证问题响应。若你需要持续维护的版本，建议自行 fork 上游并接手。

# TraeTools

> Trae 一体化管理工具：多账号每日签到 · 云端自动签到 · 账号快速切换 · Qoder 签到

TraeTools 是对 **TRAE-Checkin** 的重构版，并集成了 **Trea-Switch** 的账号切换能力，用 Avalonia 重写为现代化桌面应用。一个工具覆盖 Trae 账号的「登录 → 签到 → 切换 → 云端托管」全流程，并附带 Qoder 的每日活动领取与积分查询。

### 气死我了，怎么老是给我爆9074

## 功能特性

- **多账号每日签到**：一页管理多个 Trae 账号，一键签到全部启用账号；自动签到可设定时间，到点自动执行
- **云端签到（永不掉线）**：一键把签到任务部署到你自己的 GitHub fork（GitHub Actions 每天北京时间 11:00 运行），无需电脑开机
- **账号切换**：为每个账号备份登录态（vault），顶栏下拉直接冷切换客户端账号，失败自动回滚
- **Qoder 签到**：设备码授权登录 Qoder（国内 / 国际），一键领取每日活动积分并查看剩余额度与套餐
- **账号资料**：展示每个账号的昵称 / 脱敏手机号 / 头像 / 学生认证徽章
- **飞书推送**：签到结果可实时推送到飞书机器人
- **自动更新**：内置检查更新与静默替换安装

## 界面

| 导航 | 说明 |
|---|---|
| 每日签到 | 本月日历、连签天数、签到记录、一键/自动签到 |
| 云端签到 | GitHub 授权、一键部署、部署状态与日志 |
| Qoder | 设备码授权登录、活动领取、积分与套餐 |
| 运行日志 | 各模块行为日志，可回溯切换与签到过程 |
| 设置 | 自动签到配置、Token 信息、飞书推送、账号管理、关于 |

## 快速开始

> **macOS 用户看这里**：macOS 原生版 TraeBar（菜单栏常驻多账号自动签到，Swift/AppKit 实现）在本仓库的 [`macos`](https://github.com/star620/TraeTools/tree/macos) 分支独立维护与发布，版本 tag 为 `macos-v*`，发布产物见 [Releases](https://github.com/star620/TraeTools/releases)。本分支（main）仅维护 Windows 版。

### 方式一：直接使用
在 [Releases](https://github.com/star620/TraeTools/releases) 下载最新版运行（Windows，需 .NET 9 桌面运行时）。

### 方式二：源码编译

需要 **.NET 9 SDK**：

```bash
git clone https://github.com/star620/TraeTools.git
cd TraeTools
dotnet build -c Release
```

产物位于 `bin/Release/net9.0-windows/TraeTools.exe`。

## 使用指南

### 1. 添加 / 登录账号
`设置 → 账号管理 → + 添加`：自动打开登录窗口（内嵌浏览器），手机号+验证码登录后自动读取凭证并保存。登录成功时会用会话 Cookie 换取 refreshToken（约 180 天），之后自动续期；Token 失效时先用 refreshToken 换新（14 天），再回落会话 Cookie（8 小时）。

### 2. 每日签到
签到页点「一键签到」或开启「每日自动签到」；也可用托盘菜单的「立即签到」。签到结果写入本地历史，日历与连签天数随之更新。

### 3. Qoder 签到
1. `Qoder` 页选择国内版或国际版，点「添加账号」
2. 程序在浏览器打开授权页，在其中选择要登录的 Qoder 账号并确认；程序只读取授权结果，不接触密码
3. 点「一键签到」领取当天的活动奖励（每日 100 Credits 一类的活动），「刷新积分」查看剩余额度与套餐

> Qoder 的每日奖励通过活动平台发放。领取按账号身份去重：后领的账号可能收到「已由同一人领取」，上游判为已领取的账号按成功处理。每个账号登录时使用独立的设备标识，避免被服务端聚合成同一台设备。

### 4. 云端签到（部署到 GitHub Actions）
1. `云端签到` 页点击「授权 GitHub」（设备码授权，无需密码，仅需 repo 权限）
2. 点击「一键部署到云端」：程序自动 fork 本仓库 → 写入每个账号的 `TRAE_SESSION` / `TRAE_DEVICE_ID` secret → 启用并触发验证运行
3. 验证成功即生效：**每天北京时间 11:00** 自动签到，即使本机关机也不中断

部署原理：fork 仓库中的 `.github/workflows/checkin.yml` 定时运行 `checkin.py`，脚本用 `X-Cloudide-Session` 换取新 JWT 后调用签到接口（仅依赖 Python 标准库）。

> 从旧版 TRAE-Checkin 迁移：已部署过旧 fork 的用户，在新版「重新部署」时会提示自动删除旧 fork 并重建，无需手动去网页操作。旧 fork 仓库可自行删除。

### 5. 账号切换
顶栏的「当前账号」下拉选择目标账号后点「切换」：程序会自动关闭客户端、备份当前登录态、写入目标账号、拉起客户端并校验结果；失败会自动回滚到原账号。切换过程写入「运行日志」页。账号增删在 `设置 → 账号管理`。

## 常见问题

| 现象 | 说明 / 处理 |
|---|---|
| 签到提示「当前参与用户太多」 | Trae 风控：同设备/网络频繁请求触发。建议错开高峰时段、减少账号数、不同账号用独立设备号 |
| 云端签到失败但日志只有「参与用户太多」 | 风控 9074 是暂时的：脚本已改为 6 次重试（3/6/12/24/48 秒）；仍失败则说明该时段风控严格，次日再试 |
| 云端部署 HTTP 401 | GitHub 授权失效或权限不足：点「重新授权」；仍失败则自动删除旧 fork 重建 |
| 云端提示「未找到 workflow」 | 旧 fork 早于本仓库添加 checkin.yml：按提示一键「删除并重建 fork」即可自愈 |
| 签到 0 积分 | 当日已签过（重复签到不加分），或非会员当日基础 150 分已到账 |
| Token 失效 | 程序先用 refreshToken（约 180 天）换新，失败再回落 `X-Cloudide-Session` 静默换新；两者都失效时才提示重新登录 |
| 重新登录无反应 | 已修复：旧版本会把「读到与当前相同的 Token」误判为登录未完成而空转 30 分钟；现在按账号 ID 与有效期校验，窗口会显示被拒原因 |

> 云端签到凭证 `TRAE_SESSION` 有效期约 14 天，过期后请在软件内重新登录并重新部署。

## 开发者

- 技术栈：Avalonia / .NET 10 / CommunityToolkit.Mvvm / Sodium.Core / WebView2
- 目录：`ViewModels/` 页面逻辑、`Services/` 网络与领域服务（`Services/Qoder/` 为 Qoder 侧）、`Views/` 界面、`Models/` 数据模型
- 行为默认不对外请求官方非公开接口之外的能力；积分接口为逆向网页接口，字段随官方可能变化，代码均做容错解析

## 免责声明

- 本项目为个人开源工具，与 ByteDance / Trae 官方无任何关系
- 使用自动签到、账号切换等功能请遵守 Trae 服务条款，风险自负
- 请勿出售、外借或用于非法用途；账号凭证仅保存在你自己的电脑/GitHub secret 中

## 致谢

- [TRAE-Checkin](https://github.com/star620/TRAE-Checkin)：本项目的前身，云端签到体系（checkin.py + GitHub Actions 流程）源自该仓库
- [Trea-Switch](https://github.com/star620/Trea-Switch)：账号切换（vault 建档 / 冷切换 / 守护回滚）能力源自该仓库

## 许可证

见 [LICENSE](LICENSE)。
