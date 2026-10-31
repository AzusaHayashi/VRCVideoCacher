> [!IMPORTANT]
> **Fork notice / Fork 说明**
>
> 本仓库是 [EllyVR/VRCVideoCacher](https://github.com/EllyVR/VRCVideoCacher) 的 **fork**，唯一目的是
> 增加 **YouTube Cookie 持久化桥（CookieBridge）**：让浏览器里已登录的 YouTube Cookie 持续、
> 自动地落到 VRCVideoCacher 自己的 `%APPDATA%\VRCVideoCacher\youtube_cookies.txt`，
> 不修改 VRCVideoCacher 的安装目录，也不依赖它正在运行。
>
> 上游功能（SABR、缓存、VRChat/Resonite 集成……）全部保留不变；本 fork 的改动只有三个文件
> 加三个新目录，详见下方「与上游的关系」。
>
> * 安装：**[docs/INSTALL.md](./docs/INSTALL.md)**（约 5 分钟，全程不需要管理员权限）
> * 架构：**[docs/ARCHITECTURE.md](./docs/ARCHITECTURE.md)** ｜ 安全：**[docs/SECURITY.md](./docs/SECURITY.md)**
> * 兼容性：**[docs/COMPATIBILITY.md](./docs/COMPATIBILITY.md)** ｜ 同步上游：**[docs/UPSTREAM-SYNC.md](./docs/UPSTREAM-SYNC.md)**
> * 常见问题：**[docs/FAQ.md](./docs/FAQ.md)** ｜ 内部契约：**[docs/CONTRACT.md](./docs/CONTRACT.md)**
> * 实测记录：**[docs/VERIFICATION.md](./docs/VERIFICATION.md)**

## Fork 说明（CookieBridge）

### 这是什么

上游 VRCVideoCacher 需要你**手动**把 YouTube Cookie 交给它（官方扩展在 VRCVC 运行时 POST 到
`127.0.0.1:9696`），而且只要你在浏览器里继续登录着浏览 YouTube，YouTube 就会轮换会话 token，
让之前存下的 Cookie 失效 —— 于是播放又回到 `Sign in to confirm you're not a bot`。

这个 fork 加了一个**常驻的、与 VRCVC 版本解耦的**桥：

* 浏览器扩展抓 `youtube.com` 的 Cookie（复用上游官方扩展 2.0.1 的抓取与 Netscape 序列化逻辑，
  仅把 HTTP POST 换成 **Native Messaging**）；
* 本地 Native Host 用 **Windows DPAPI（当前用户）** 加密存成 vault，
  再原子写入 VRCVideoCacher 的 Cookie 文件；
* VRCVideoCacher 每次调 yt-dlp 都会重新读这个文件，所以**换 Cookie 不需要重启 VRCVC**。

**不上传任何数据、不开监听端口、不装服务/驱动、不注入 DLL/不 patch 内存。**

### 组件

| 组件 | 目录 | 形态 | 作用 |
| --- | --- | --- | --- |
| 浏览器扩展 | [`browser-extension/`](./browser-extension/) | MV3（Chrome/Edge/Brave/Vivaldi）+ Firefox 138+，零构建、零 npm 依赖 | 抓 Cookie → Netscape 序列化 → `sendNativeMessage` |
| Native Host | [`bridge/CookieBridge.NativeHost/`](./bridge/CookieBridge.NativeHost/) | `CookieBridge.NativeHost.exe`（stdio） | 校验 → DPAPI 加密落 vault → 原子写 Cookie 文件 |
| 管理器 | [`bridge/CookieBridge.Manager/`](./bridge/CookieBridge.Manager/) | WPF GUI（framework-dependent，需 .NET 10 Desktop Runtime） | 安装/卸载、注册 Native Host、状态与 Layers、备份/恢复、日志查看 |
| Hook（可选） | [`bridge/CookieBridge.Hook/`](./bridge/CookieBridge.Hook/) | `CookieBridge.Hook.dll` + 一个 `StartupHook` 入口 | 进程内辅助：复用 VRCVC 自己的 `YtdlCookieJar` 锁与 UI 通知（仅自编译 fork 或支持启动钩子的宿主） |
| 共享库 | [`bridge/CookieBridge.Core/`](./bridge/CookieBridge.Core/) | 类库 | 路径、vault、Netscape 处理、原子写、注册表、日志、兼容矩阵 |

扩展 ID：Chrome `fdjmcdfolkgicljalacnlemepfhmnmfp`，Firefox `vrexcookiebridge@local`。

### 30 秒快速开始

```powershell
# 1) 安装 Native Host（当前用户，写 HKCU，不需要管理员；host + hook + 扩展副本一起落位并注册 6 个浏览器项）
powershell -ExecutionPolicy Bypass -File .\tools\Install-CookieBridge.ps1
#    或：运行 bridge\CookieBridge.Manager\bin\...\CookieBridge.Manager.exe → Native Host → 注册
#    脚本还支持 -Uninstall [-Purge] 卸载/彻底清理，-RegisterStartupHook 仅自编译 fork 需要

# 2) 浏览器加载扩展目录（chrome://extensions → 开发者模式 → 加载已解压的扩展程序）
#    可用 browser-extension\ 、dist\browser-extension\ 或 %LOCALAPPDATA%\VRCVideoCacher-CookieBridge\browser-extension\

# 3) 打开 https://www.youtube.com/ 并登录，等扩展徽章变绿 OK

# 4) 检查文件
(Get-Content "$env:APPDATA\VRCVideoCacher\youtube_cookies.txt" |
  Where-Object { $_ -and $_ -notlike '#*' -and ($_ -split "`t").Count -eq 7 }).Count   # 期望 > 0

# 5) 一条命令看全局状态（vault / 目标文件 / VRCVC 版本与兼容等级 / hook / 已注册浏览器）
& "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\host\CookieBridge.NativeHost.exe" --status

# 6) 启动 VRCVC，播一个需要登录的 YouTube 视频
```

完整步骤（含三种安装方式、逐条验证、卸载与回滚）：**[docs/INSTALL.md](./docs/INSTALL.md)**。

### 与上游的关系

* **同步方式**：`upstream` 指向 `EllyVR/VRCVideoCacher`，`origin` 指向你自己的 fork；
  fork 的改动尽量"薄"——只在 `VRCVideoCacher/Program.cs` 插入 4 行、`.gitignore` 末尾追加一节、
  本 README 顶部加这一段，其余全部是**新目录**（`bridge/`、`browser-extension/`、`docs/`），
  因此 `git rebase upstream/main` 的冲突面只有 3 个文件。
* 详细流程（浅克隆补全、rebase、冲突热点、发布 fork、回上游开 PR 的建议）：
  **[docs/UPSTREAM-SYNC.md](./docs/UPSTREAM-SYNC.md)**。
* 上游的本地化 README（`README_*.md`）与翻译文件**不改动**。

### 许可证与数据声明

* **不含任何真实 Cookie / 账号数据**。仓库与发布物里没有 `youtube_cookies.txt`、`vault.json`、
  日志或备份；测试数据全部是合成的 `FAKE_*` Cookie。`.gitignore` 显式排除了上述运行期文件。
* **许可证**：本仓库当前快照（以及它 fork 的上游 `upstream/main`，159 个文件）**都没有
  `LICENSE` / `COPYING` / `NOTICE` 文件**，因此这里无法引用具体的许可证类型与条款。
  在对外分发二进制或源码之前，请先按 [docs/UPSTREAM-SYNC.md](./docs/UPSTREAM-SYNC.md) 第 5 节核对
  并取得上游授权；上游若补充了许可证，以那份为准。原项目署名见 `VRCVideoCacher/Program.cs`
  的 `Creator_*` 常量与上方上游链接。

### 技术边界：为什么 Steam 单文件版走「契约模式」

Steam 版 VRCVideoCacher 是 .NET 10 + Avalonia 的**单文件自包含**发布（`PublishSingleFile` +
`SelfContained` + `PublishTrimmed`），磁盘上只有一个 `VRCVideoCacher.exe` 加 `manifest.vrmanifest`。

对真实 Steam 版进程抓 `COREHOST_TRACE`，得到：

```
Property TRUSTED_PLATFORM_ASSEMBLIES =
Property System.StartupHookProvider.IsSupported = false
```

即：单文件宿主没有可枚举的托管程序集清单，且裁剪后的 `StartupHookProvider` 功能开关是关的 ——
`DOTNET_STARTUP_HOOKS` 里写任何路径都不会被调用。另外 4 种覆盖手段（普通同名环境变量开关、
`DOTNET_` 前缀、`AppContextSwitchOverrides`、exe 旁 sidecar `runtimeconfig.json`）实测均无效；
同一 Hook DLL 在**框架依赖**宿主上则能正常加载（日志出现
`Startup hook initialised inside the host process.`），证明被挡住的是加载机制而不是插件本身。

所以产品有两种模式：

| 模式 | 谁写 Cookie 文件 | 适用 |
| --- | --- | --- |
| **契约模式**（默认） | Native Host（跨进程互斥量 + 等 yt-dlp 空闲 + 原子替换） | **Steam 版可用** |
| **进程内模式**（可选） | Hook（进程内 `YtdlCookieJar` 信号量 + `NotifyCookiesUpdated`） | 自编译 fork 构建，或 `StartupHookProvider.IsSupported = true` 的宿主 |

自编译 fork 的接入方式是 `Program.Main` 开头调用 `CookieBridgePlugin.TryLoad()`
（`VRCVideoCacher/Utils/CookieBridgePlugin.cs`）→ `Assembly.LoadFrom(%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\hook\CookieBridge.Hook.dll)`
→ `StartupHook.Initialize()`，不改其它逻辑。论证与实测细节：**[docs/ARCHITECTURE.md](./docs/ARCHITECTURE.md)** §8–§9。

---

<div align="center">

[![Header Banner](https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/4296960/d1bac93e4abb00108cda2137260b76a25bcffea4/header.jpg)](https://store.steampowered.com/app/4296960)

[![Wiki](https://img.shields.io/badge/Wiki-Info-green)](https://github.com/EllyVR/VRCVideoCacher/wiki) [![Steam Download](https://img.shields.io/badge/Steam-Download-blue?logo=steam)](https://store.steampowered.com/app/4296960) [![Github Download](https://img.shields.io/badge/Github-Download-blue?logo=github)](https://github.com/EllyVR/VRCVideoCacher/releases/latest) [![Discord Server](https://img.shields.io/badge/Discord-Join%20Server-5865F2?logo=discord)](https://discord.gg/z5kVNkmQuS)

<hr>
</div>

**Language:** **English** | [日本語](./README_ja-JP.md) | [Magyar](./README_hu-HU.md) | [한국어](./README_ko-KR.md) | [Português do Brasil](./README_pt-BR.md) | [Italiano](./README_it-IT.md) | [Русский](./README_ru-RU.md) | [简体中文](./README_zh-CN.md) | [Français](./README_fr-FR.md) | [Norsk (bokmål)](./README_nb-NO.md)

### Wiki
- [Launch Options](https://github.com/EllyVR/VRCVideoCacher/wiki/Launch-Options)
- [Cli Config Options](https://github.com/EllyVR/VRCVideoCacher/wiki/Config-Options)
- [Linux](https://github.com/EllyVR/VRCVideoCacher/wiki/Linux)

### What is VRCVideoCacher?

VRCVideoCacher keeps YouTube (and other) videos playing reliably in **VRChat** and **Resonite**, and caches them to your local disk so they load instantly next time.

YouTube has been moving to a new streaming protocol called **SABR**, which ordinary `yt-dlp` link extraction can't play. VRCVideoCacher speaks SABR natively and restreams it to the game as seekable video — so playback keeps working through YouTube's changes, in up to **4K**, with a full scrub bar.

### Features

- 📺 **Plays YouTube's new SABR streams** natively — including videos that plain yt-dlp can no longer play — restreamed to AVPro as seekable HD/4K video.
- 🍪 **Beats "Sign in to confirm you're not a bot"** using your YouTube cookies plus an auto-managed PO token provider — no manual setup.
- 💾 **Local video cache** with a configurable size limit, so repeat plays load instantly and cut bandwidth.
- 🖥️ **Desktop app** with a dashboard, cache browser, download queue, play history and log viewer. Minimizes to the system tray, or run headless with `--nogui`.
- 🔄 **Self-updating** — keeps itself and its tools (yt-dlp, FFmpeg, Deno) up to date automatically.
- 🎮 Works with **VRChat** and **Resonite**, on **Windows** and **Linux**.

### How does it work?

On startup it replaces VRChat/Resonite's `yt-dlp.exe` with a small stub that forwards video requests to the VRCVideoCacher app running in the background. The original `yt-dlp.exe` is restored automatically when the app exits.

Auto installs missing codecs: [VP9](https://apps.microsoft.com/detail/9n4d0msmp0pt) | [AV1](https://apps.microsoft.com/detail/9mvzqvxjbq9v) | [AC-3](https://apps.microsoft.com/detail/9nvjqjbdkn97)

### Are there any risks involved?

**From VRC or EAC?** No.

**From YouTube/Google?** There's always a theoretical risk when automating access to YouTube, so use a separate/alternative Google account if you want to be cautious.

In practice, though:
- The main developer has used their primary YouTube account since the very start of development, with no issues.
- We've never had a single report of an account ban related to this tool.

### How we fix bot checks

YouTube's "Sign in to confirm you're not a bot" wall is beaten with two things working together:

- **Your YouTube cookies** — so requests look like a real signed-in session.
- **A PO token** — a proof-of-origin token YouTube now demands. This is minted and managed for you automatically in the background; no setup needed.

VRCVideoCacher handles the PO token side on its own — all you need to provide is your cookies:

1. Open the built-in **YouTube Cookie Setup** wizard on the dashboard. It installs our browser extension for [Chrome](https://chromewebstore.google.com/detail/vrcvideocacher-cookies-ex/kfgelknbegappcajiflgfbjbdpbpokge) or [Firefox](https://addons.mozilla.org/en-US/firefox/addon/vrcvideocachercookiesexporter) ([more info](https://github.com/clienthax/VRCVideoCacherBrowserExtension)).
2. With VRCVideoCacher running, visit [YouTube.com](https://www.youtube.com) while signed in, at least once.
3. Once it has your cookies, you can uninstall the extension.

> **Note:** if you browse YouTube again in the same browser while still logged in, YouTube rotates your cookies and invalidates the ones VRCVideoCacher stored.
>
> To avoid this, either:
> - Delete your YouTube cookies from that browser afterwards, or
> - Leave the extension installed, or
> - Use a separate browser/account just for this.

### A note on ads (non-Premium accounts)

SABR injects ads **server-side**. For non-Premium accounts, YouTube withholds the actual video until the client "watches" the ad — it has to wait out a short enforced delay before any video data is sent. In VRChat this shows up as a pause of a few seconds before a video starts playing.

- **YouTube Premium accounts** are served SABR ad-free, with no such delay. This is the smoothest experience.
- **Non-Premium accounts** will see those startup delays on SABR playback.

If the delays bother you, turn **off** the "Prefer SABR streaming" option in Settings. VRCVideoCacher will then try the normal direct URL first, and only fall back to SABR for videos that are SABR-only — avoiding the ad delay on most videos.

There's no way to remove the delay while actually streaming SABR on a non-Premium account: the wait is enforced by YouTube's servers, not by VRCVideoCacher.

### Fix YouTube videos sometimes failing to play

> Loading failed. File not found, codec not supported, video resolution too high or insufficient system resources.

Sync your system time: Windows Settings → Time & Language → Date & Time → under "Additional settings" click "Sync now". Keeping **Windows Update** current also matters — some audio codecs used by newer streams only decode correctly on an up-to-date Windows.

### Uninstalling

- If you have VRCX, delete the startup shortcut "VRCVideoCacher" from `%AppData%\VRCX\startup`
- Delete config and cache from `%AppData%\VRCVideoCacher`
- Delete "yt-dlp.exe" from `%LocalAppData%Low\VRChat\VRChat\Tools` and restart VRChat or rejoin world.
