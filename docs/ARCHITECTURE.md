# CookieBridge 架构

本文描述 **VRCVideoCacher-CookieBridge**（下称 CookieBridge）的组件、进程边界与数据流。
事实基线是当前分支 `feature/cookie-persistence` 的代码，不是设计意图。契约见
[`docs/CONTRACT.md`](./CONTRACT.md)，公共 API 见 [`docs/API.md`](./API.md)。

---

## 1. 一句话结论

CookieBridge 把「浏览器里已登录 YouTube 的 Cookie」变成 VRCVideoCacher 自己会读的
`%APPDATA%\VRCVideoCacher\youtube_cookies.txt`，**不修改 VRCVC 的安装目录、不依赖它的任何私有
API**。默认走**契约模式**（Steam 版可用）；进程内模式是可选增强，只对自编译 fork 构建或支持
托管启动钩子的宿主生效。

---

## 2. 组件与进程边界

```
┌─ 浏览器进程（Chrome / Edge / Chromium / Brave / Vivaldi / Firefox）──────────────────────────┐
│  browser-extension/                                                                          │
│    content.js         YouTube 页面加载 ─┐                                                    │
│    background.mjs     cookies.onChanged ┴─► 5s 防抖                                          │
│    utils.mjs          getAllYouTubeCookies()   ← 上游 2.0.1 逐字行为                         │
│                       serializeNetscapeCookies()                                             │
│                       sendToNativeHost()       ← 唯一与上游不同处：sendNativeMessage         │
└───────────────────────────────────┬──────────────────────────────────────────────────────────┘
                                    │ ① Native Messaging：stdio，4 字节小端长度 + UTF-8 JSON，≤1 MiB
                                    │   浏览器按 HKCU 注册表找到 host manifest，再拉起 host 进程
                                    ▼
┌─ CookieBridge.NativeHost.exe（独立进程；两种模式）──────────────────────────────────────────────┐
│  无参数（浏览器拉起）→ Native Messaging                                                          │
│  NativeMessageProtocol.ReadMessage                                                               │
│    → NativeMessageHandler.HandleJson                                                             │
│        cookie.update : Canonicalize → IsUsable → CookieVault.Save                                │
│                        → Hook 在线？→ 只写 pending.sync（交给 hook 落盘）                         │
│                        → Hook 离线？→ CookieTargetWriter.Write（互斥量 + 等 yt-dlp 空闲）         │
│        status/ping   : 只读快照                                                                  │
│  带参数（安装器/管理器/排障）→ CLI                                                               │
│    --register-host / --unregister-host / --status / --send / --version                            │
└───────────────────────────────────┬──────────────────────────────────────────────────────────┘
                                    │ ② DPAPI(CurrentUser) 加密 + 原子替换
                                    ▼
      %LOCALAPPDATA%\VRCVideoCacher-CookieBridge\vault.json      ← 唯一真相来源（密文）
      %LOCALAPPDATA%\VRCVideoCacher-CookieBridge\state\pending.sync  ← 「有新内容待落地」标记
                                    │
                   ┌────────────────┴──────────────────┐
                   │                                   │
       ③a Hook 在线                         ③b Hook 不在线（Steam 版常态）
       进程内模式                            契约模式：host 已直接写完目标文件
                   │                                   │
                   ▼                                   │
┌─ VRCVideoCacher 进程（可选注入 hook）──────────────┐  │
│  CookieBridge.Hook（CookieBridge.Hook.dll）        │  │
│    FileSystemWatcher(Root) ─► 1s 防抖 ─► SyncOnce   │  │
│    CookieMerger.Merge(vault, 当前目标文件)          │  │
│    L4 用 VRCVC 自己的 IsCookiesValid 判有效         │  │
│    L2 取 VRCVC 的 YtdlCookieJar 信号量后原子写       │  │
│    L3 NotifyCookiesUpdated() 刷新 UI               │  │
└────────────────────┬───────────────────────────────┘  │
                     └───────────────┬──────────────────┘
                                     ▼
      %APPDATA%\VRCVideoCacher\youtube_cookies.txt        ← 与 VRCVC 的唯一契约点（明文，Netscape）
      %APPDATA%\VRCVideoCacher\youtube_cookies.txt.cookiebridge.bak  ← 原子替换时的上一版
                                     │
                                     │ ④ 每次 yt-dlp 调用都重新读文件
                                     ▼
      VRCVideoCacher.YtdlManager.GenerateYtdlArgs()
        if (Program.IsCookiesEnabledAndValid()) args.Add("--cookies \"<CookiesPath>\"")
                                     ▼
                                 yt-dlp 子进程
                                     │
                                     │ ⑤ yt-dlp 退出时把轮换后的 token 写回同一文件
                                     └──────► 回到 youtube_cookies.txt
```

### 进程/边界清单

| 边界 | 谁在里面 | 与谁通信 | 通信方式 |
| --- | --- | --- | --- |
| 浏览器进程 | 扩展（MV3 service worker + content script） | Native Host | Native Messaging，stdio |
| Native Host 进程 | `CookieBridge.NativeHost.exe`（同一个二进制两种模式：无参数=Native Messaging，带参数=CLI） | 浏览器 / 文件系统 / 注册表 | stdin/stdout；vault、state、目标文件；`--register-host` 等 CLI 供安装器与管理器调用 |
| VRCVC 进程 | 上游业务代码（+ 可选 Hook 程序集） | yt-dlp 子进程、文件系统 | 进程内 `YtdlCookieJar` 信号量；`--cookies <path>` |
| yt-dlp 子进程 | yt-dlp / yt-dlp-stub | 网络、Cookie 文件 | 命令行参数，读写同一文件 |
| Manager 进程 | `CookieBridge.Manager.exe`（WPF GUI，framework-dependent） | 文件系统、注册表、HKCU 环境变量、Hook 控制管道 | 直接调用 `CookieBridge.Core`；命名管道 `VRCVideoCacherCookieBridge` |

**没有的东西**（同样重要）：没有本地 HTTP 监听端口，没有服务（service）、没有驱动、没有 DLL 注入、
没有 API Hook / Harmony patch、没有跨机器通信、不上传任何数据。

---

## 3. 磁盘布局

| 位置 | 内容 | 敏感 |
| --- | --- | --- |
| `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\vault.json` | DPAPI 密文 Cookie + 非敏感元数据 | 是（ciphertext） |
| `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\hook\CookieBridge.Hook.dll` | 进程内插件（单个托管 DLL） | 否 |
| `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\host\native-host-*.json` | Native Messaging manifest（Chromium / Firefox 各一份） | 否 |
| `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\browser-extension\` | 安装脚本复制过来的扩展副本（可选，卸载时一起清） | 否 |
| `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\host\CookieBridge.NativeHost.exe` 或 `dist\host\` | 本地写入程序（Native Messaging + CLI 双模式） | 否 |
| `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\install.json` | 插件安装记录（DLL 路径 + SHA256 + 安装时间；只在注册启动钩子时才有 `envEntriesAdded`） | 否 |
| `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\disabled.flag` | 禁用开关（文件存在=禁用） | 否 |
| `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\state\hook-status.json` | Hook 心跳 / 反射层探测结果 | 否 |
| `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\state\pending.sync` | host 告诉 hook「vault 有更新」 | 否 |
| `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\state\last-error.txt` | 最近一次扩展侧的机器码错误 | 否 |
| `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\logs\bridge.log`（`.1` `.2` `.3`） | 日志，单文件 ≤1 MiB，滚动 3 份 | 脱敏后否 |
| `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\backups\<时间戳>\youtube_cookies.txt` | 管理器手动备份 | **是（明文）** |
| `%APPDATA%\VRCVideoCacher\youtube_cookies.txt` | VRCVC 消费的 Cookie 文件 | **是（明文）** |
| `%APPDATA%\VRCVideoCacher\youtube_cookies.txt.cookiebridge.bak` | 原子替换的上一版 | **是（明文）** |

所有路径常量集中在 `bridge/CookieBridge.Core/BridgePaths.cs`；测试可用环境变量
`VRCCB_ROOT` / `VRCCB_VRCVC_DATA` / `VRCCB_TARGET_FILE` 重定向，生产环境下这三个变量必须不存在。

---

## 4. 数据流 A —— 契约模式（默认，Steam 版可用）

这是 Steam 版 VRCVideoCacher 唯一可用的路径，也是默认路径。

1. **抓取**：`content.js` 在 YouTube 页面加载时请求一次同步；`background.mjs` 另外监听
   `chrome.cookies.onChanged`（5 秒防抖）、安装/更新事件、状态页「立即同步」按钮。
2. **序列化**：`utils.mjs` 调 `getAllYouTubeCookies()`（含 `partitionKey` 兼容分支与 cookie store
   处理，全部是上游逐字逻辑），再 `serializeNetscapeCookies()`：7 个 `\t` 分隔字段、domain 强制
   `.` 前缀、`TRUE` 表示 includeSubdomains、`Math.floor(expirationDate || 0)`；并按契约 §4
   **丢弃 `ST-` 开头的行**（与上游 `ApiController.FilterCookies` 对齐）。
3. **传输**：`chrome.runtime.sendNativeMessage("com.vrcvideocacher.cookiebridge", {...})`，
   超时 20 秒。浏览器按 `HKCU\Software\<浏览器>\NativeMessagingHosts\com.vrcvideocacher.cookiebridge`
   找到 manifest，拉起 `CookieBridge.NativeHost.exe`。
4. **校验 + 落 vault**：host 侧 `NetscapeCookieFile.Canonicalize` 再做一次规范化（组件之间不可能
   对格式有分歧），`IsUsable` 要求同时含 `youtube.com` 与 `LOGIN_INFO` 且至少一条记录；
   通过后 `CookieVault.Save` → DPAPI 加密 → `AtomicFile.WriteAllText(vault.json, restrictAcl: true)`。
5. **写目标文件**：若 `HookStatus.Read()?.IsFresh != true`（Steam 版常态），host 自己调
   `CookieTargetWriter.Write`：
   * 先 `YtdlpActivity.WaitUntilIdle`（VRCVC 正在跑时最多等 10 秒，避免与 yt-dlp 的 token 轮换打架）；
   * 再取跨进程互斥量 `Local\VRCVideoCacherCookieBridge.CookieWrite`（默认 3 秒超时）；
   * 内容与现有文件规范化后相同 → `Unchanged`，不写；
   * 否则 `File.Replace` 原子替换，备份到 `.cookiebridge.bak`。
6. **响应**：`{ok:true, vaultUpdated:true, cookieCount:N, applied:"vault+cookie-file", targetWritten:true}`；
   扩展据此把徽章刷成绿色 `OK`。

**关键性质**：VRCVideoCacher 完全不需要在运行。目标文件在 VRCVC 启动前就已经是新的了。

---

## 5. 数据流 B —— 进程内模式（可选）

仅在以下宿主上生效：
* 从本 fork 自编译的构建（`Program.Main` 第一行调用 `CookieBridgePlugin.TryLoad()`）；
* 或任何 `System.StartupHookProvider.IsSupported = true` 的框架依赖宿主（见第 8 节）。

差别只有两处：

* **谁写目标文件**：host 发现 hook 心跳新鲜时不再自己写，只 `Touch pending.sync` 并返回
  `applied:"vault"`；写盘交给 hook —— 因为 hook 在 VRCVC 进程内，能拿到 `YtdlCookieJar` 的
  进程内信号量，这是唯一「绝对不会与 yt-dlp 抢锁」的时机。
* **落盘时多一步合并**：`CookieMerger.Merge(vault, 当前目标文件)`。规则见下。

### CookieMerger 规则（`bridge/CookieBridge.Hook/CookieMerger.cs`）

yt-dlp 退出时会重写 Cookie 文件（YouTube 会轮换 `__Secure-1PSIDTS`、`SIDCC` 等），所以：

| 情况 | 结果 |
| --- | --- |
| 目标文件不存在/空 | 直接用 vault |
| vault 里没有 `LOGIN_INFO` | 保留目标文件（保住轮换结果） |
| 目标文件里没有 `LOGIN_INFO` | 用 vault（浏览器说了算） |
| 两边 `LOGIN_INFO` 不同 | 用 vault（换了账号/重新登录，浏览器说了算） |
| 两边 `LOGIN_INFO` 相同 | 以目标文件为底，把浏览器发来的每条 Cookie 覆盖上去 |

### 抑制逻辑（用户主动清空 Cookie）

hook 另外监视目标文件本身。若目标文件被外部改动（不是自己写的，2 秒内的自写不算）后内容变成
「不可用」（不含 `youtube.com` + `LOGIN_INFO`），说明用户在 VRCVC 里点了「清除 Cookie」——
hook 立刻置 `_suppressUntilVaultChanges = true`，**在浏览器产生新的 vault 更新之前不再回写**。
这条路径是为了尊重用户明示的删除动作，避免「点了清除又被写回来」。

---

## 6. 时序 1：VRCVC 未运行

```
浏览器           NativeHost                 vault.json / target             VRCVC           yt-dlp
  │                  │                            │                          │               │
  ├─ 页面加载/onChanged                           │                          │               │
  ├─ getAll → 序列化                              │                          │               │
  ├─ sendNativeMessage ──►│                       │                          │               │
  │                  ├─ Canonicalize/IsUsable     │                          │               │
  │                  ├─ DPAPI 加密 ──────────────►│ (原子替换)                │               │
  │                  ├─ hook 离线 → 互斥量 + Replace ─────────────────────►  │ (明文目标文件)  │
  │◄─ {ok, targetWritten:true}                    │                          │               │
  ├─ 徽章 OK (绿)                                 │                          │               │
  │                  │                            │      用户双击启动 ────────┤               │
  │                  │                            │                          ├─ Main → TryLoad(hook，fork)
  │                  │                            │                          ├─ SyncOnce("startup")：内容相同→unchanged
  │                  │                            │                          ├─ 播放 → --cookies <path> ──►│
  │                  │                            │                          │               ├─ 用 cookie 请求
  │                  │                            │                          │               ├─ 退出回写轮换 token
```

检查点：`vault.json` 存在且 `updatedUtc` 是刚才；目标文件 `mtime` 是刚才；bridge.log 有
`target  Wrote VRCVC cookie file (...)` 或 `内容相同，跳过`。

---

## 7. 时序 2：VRCVC 运行中更新（不重启）

```
浏览器            NativeHost              vault/pending            Hook（VRCVC 进程内）        yt-dlp
  │                   │                       │                          │                    │
  ├─ YouTube 轮换 cookie / 重新登录            │                          │                    │
  ├─ 5s 防抖 → 抓取 → 序列化                   │                          │                    │
  ├─ sendNativeMessage ─►│                     │                          │                    │
  │                   ├─ 写 vault ────────────►│                          │                    │
  │                   ├─ HookStatus.IsFresh==true                         │                    │
  │                   ├─ 只写 pending.sync ───►│                          │                    │
  │◄─ {ok, applied:"vault", targetWritten:false}                          │                    │
  │                   │                       ├─ FileSystemWatcher ──────►│                    │
  │                   │                       │   1s 防抖                ├─ Merge(vault,target)
  │                   │                       │                          ├─ L4 IsCookiesValid
  │                   │                       │                          ├─ L2 AcquireAsync（等当前 yt-dlp 退出）
  │                   │                       │                          ├─ AtomicFile 替换 ──► 目标文件
  │                   │                       │                          ├─ L3 NotifyCookiesUpdated
  │                   │                       │                          ├─ 清 pending.sync
  │                   │                       │                          ├─ 心跳 30s 写 hook-status.json
  │                   │                       │                          │                    │
  │                   │                       │          下一次播放 ───────┴─ ► --cookies 读新文件 ─►│
```

要点：**没有重启，没有重新登录 VRCVC**。VRCVC 每次起 yt-dlp 都会重新读文件
（`GenerateYtdlArgs` 在每次调用时判断 `Program.IsCookiesEnabledAndValid()` 并拼 `--cookies`），
所以「换 cookie」这件事对 VRCVC 而言就是「文件变了」。

黄徽章（`!`）在进程内模式下是正常状态：说明 vault 已更新、由 hook 落地，host 自己没有写文件。

---

## 8. 为什么这样分层

### 8.1 唯一耦合点选在 yt-dlp 契约上，而不是 VRCVC 的内部 API

VRCVC 消费 Cookie 的入口只有一个：`YTDL/YtdlManager.cs` 的
`CookiesPath = Path.Join(Program.DataPath, "youtube_cookies.txt")`，并以
`--cookies "<path>"` 传给 yt-dlp（`GenerateYtdlArgs`）。这个路径是 **yt-dlp 的接口约定**，
不是 VRCVC 自己的实现细节；上游改内部类、换 UI 框架、加功能，都不会动它。

对比其它可能的耦合点：进程内 API 调用（要跟着版本改）、HTTP 端口 9696（要 VRCVC 在跑，端口可被
占用/被拦）、DLL 注入（见 8.3）。契约点的稳定性最高，破坏它的成本对上游来说也最高。

### 8.2 vault 与 VRCVC 完全解耦

`vault.json` 的格式由 CookieBridge 自己定义（`formatVersion: 1`），字段里没有任何 VRCVC 概念。
VRCVC 升级、降级、卸载、重装，vault 都不需要变；反过来说，换机器/换账号只会让 DPAPI 解不开，
不会让 VRCVC 侧出现半截状态。

### 8.3 为什么不是 DLL 注入 / API Hook

* **技术不可行（对 Steam 版而言）**：Steam 版是 **.NET 10 + Avalonia 单文件自包含发布**
  （`VRCVideoCacher.csproj`：`PublishSingleFile` + `SelfContained` + `PublishTrimmed` +
  `IncludeNativeLibrariesForSelfExtract`），唯一可执行文件是 `VRCVideoCacher.exe`
  （外加 `manifest.vrmanifest`）。实测证据见第 9 节：托管启动钩子被关闭
  （`System.StartupHookProvider.IsSupported = false`），`TRUSTED_PLATFORM_ASSEMBLIES` 为空。
  能从外部塞进进程内的机制（`DOTNET_STARTUP_HOOKS` 及其 4 种覆盖手段）全部无效；
  唯一可行的是**让应用自己 `Assembly.LoadFrom`**，而那需要改 `Program.Main` —— 也就是「编译一个 fork」，
  不是「注入现成的 Steam 版」。
* **工程代价**：注入 + Harmony patch 需要跟着每个版本的 IL 走，上游一改内部结构就全崩，而崩溃
  发生在用户进程里。
* **风险收益比**：契约模式已经能做到「实时生效、不重启」，进程内模式多出来的收益只是
  「拿到 VRCVC 自己的锁 + 通知 UI」，而这两点在契约模式下都有等价物（跨进程互斥量、
  下一次 yt-dlp 调用自然读到新文件）。

### 8.4 为什么还保留进程内模式

因为它把**锁竞争**这件事做到了理论最优：`YtdlCookieJar` 是 VRCVC 自己用来串行化 yt-dlp 的信号量
（`SabrExtractor`、`VideoDownloader`、`VideoId`、`ApiController` 四处 `AcquireAsync`）。
进程内 hook 可以 `await AcquireAsync()` 后再写文件，此时**必然没有 yt-dlp 在读/写这个文件**。
再加上 `CookieMerger` 保住 yt-dlp 的 token 轮换，这两点是契约模式做不到的。

于是分层是：**契约模式保底（永远可用），进程内模式增强（可用则用）**。任何一层缺失都只降级，
不阻断。

---

## 9. 技术边界：Steam 单文件版为什么走契约模式（实测）

对 Steam 版 `VRCVideoCacher.exe`（单文件、自包含、106 MB）设置 `COREHOST_TRACE=1` 并抓宿主日志：

```
Detected Single-File app bundle
Property TRUSTED_PLATFORM_ASSEMBLIES =
Property STARTUP_HOOKS = C:/.../CookieBridge.Hook.dll
Property System.StartupHookProvider.IsSupported = false
```

含义：

1. `Property STARTUP_HOOKS = <路径>` —— 参数确实传进去了，不是「路径写错」；
2. `TRUSTED_PLATFORM_ASSEMBLIES` 为空——单文件宿主没有可枚举的托管程序集清单，
   启动钩子的发现机制失去输入；
3. `System.StartupHookProvider.IsSupported = false`——`StartupHookProvider` 这个运行时功能开关
   在裁剪后的单文件宿主里是关的（`PublishTrimmed` + 裁剪分析把该 provider 判为不可达）。
   只要它是 `false`，`DOTNET_STARTUP_HOOKS` 里的任何路径都不会被调用。

因此 `DOTNET_STARTUP_HOOKS` 托管启动钩子**无法**载入真实 Steam 版进程。另外试过 4 种覆盖手段，
全部无效（Hook 日志始终没有出现 `Startup hook initialised`）：

| 尝试 | 具体做法 | 结果 |
| --- | --- | --- |
| 普通同名环境变量开关 | `System.StartupHookProvider.IsSupported=1` | 无效 |
| `DOTNET_` 前缀形式 | `DOTNET_System_StartupHookProvider_IsSupported=1` | 无效，开关在裁剪期已定 |
| `AppContextSwitchOverrides` | `AppContextSwitchOverrides=System.StartupHookProvider.IsSupported=true` | 无效，进程内已无该 provider 代码路径 |
| exe 旁 sidecar runtimeconfig | `*.runtimeconfig.json` 放在 exe 同目录 | 无效，单文件从 bundle 内读配置 |

**对照实验**：同一个 Hook DLL 在**框架依赖**宿主上正常加载，日志出现
`Startup hook initialised inside the host process.`（`bridge/CookieBridge.Hook/StartupHook.cs` 第 20 行），
随后 `Attached to host PID <n>`。这证明 DLL 本身、`StartupHook.Initialize()` 签名、以及全部逻辑都没问题，
被挡住的只是单文件宿主的加载机制。

**一个必须说清的细节**：`Assembly.LoadFrom(<外部 dll>)` 在单文件宿主里**是可行的**（实测成功）。
也就是说，如果让**应用自己**去加载插件，进程内模式就能工作 —— 这正是本 fork 采用的接入方式
（`CookieBridgePlugin.TryLoad()`）。不可行的只是「从应用外部、靠环境变量把钩子塞进去」。

完整实验记录（环境、命令、时间线、未验证项）：[`docs/VERIFICATION.md`](./VERIFICATION.md)。

结论与产品形态：

| 宿主 | 谁把 Cookie 写进目标文件 | 模式 |
| --- | --- | --- |
| Steam 版（单文件、裁剪、自包含） | Native Host（`CookieTargetWriter`） | 契约模式 |
| 本 fork 自编译构建 | 进程内 Hook（`HookRuntime`）；fork 的 `Program.Main` 直接调 `CookieBridgePlugin.TryLoad()` | 进程内模式 |
| 其它 `StartupHookProvider.IsSupported = true` 的宿主 | 进程内 Hook（`DOTNET_STARTUP_HOOKS`） | 进程内模式 |

---

## 10. 待解决的文档/实现偏差（记录，不改代码）

| 位置 | 现状 | 说明 |
| --- | --- | --- |
| `docs/CONTRACT.md` §6 | 把 `DOTNET_STARTUP_HOOKS` 写成唯一的 Hook 载入机制 | 对 Steam 版不成立。实际有两条：`DOTNET_STARTUP_HOOKS`（可用则用）与 fork 的 `CookieBridgePlugin.TryLoad()`。见第 9 节。 |
| `docs/CONTRACT.md` §6/§7 | 读起来像「安装时必须注册 `DOTNET_STARTUP_HOOKS`」 | 实现已改为 **opt-in**：`HookInstallation.Install(..., registerStartupHook: false)` 默认不写环境变量；安装器对应 `-RegisterStartupHook`，管理器对应「注册启动钩子」勾选框。Steam 版永远不需要。 |
| `docs/CONTRACT.md` §1 | 描述为「VRCVC 启动时由插件交出 Cookie」 | 实际是运行期 `FileSystemWatcher` 实时同步，不需要重启。 |
| `bridge/CookieBridge.Core/HookStatus.cs` 注释 | 写「Layers L1..L5」 | 实际已到 L6（`L6.cookieEvent`），见 `docs/COMPATIBILITY.md`。 |
| `browser-extension/README.md` §4 | 给出的 host manifest 同时含 `allowed_origins` 与 `allowed_extensions` | 实际 `NativeHostRegistration` 写**两个**文件：Chromium 版只有 `allowed_origins`，Firefox 版只有 `allowed_extensions`。见 `docs/INSTALL.md` §3。 |
| `VRCVideoCacher/Utils/CookieBridgePlugin.cs` | 任务书说是「需要在 fork 里新增的 20 行加载器」 | 本分支已存在并在 `Program.Main` 第 41 行调用（文件 65 行，含注释）。 |
| `bridge/CookieBridge.Manager/Services/ManagerServices.cs` `FindNativeHostExe()` | 候选路径用 `net10.0\...`，但 `CookieBridge.NativeHost.csproj` 的 `TargetFramework` 是 `net10.0-windows` | 结果：从源码树构建出的 `bin\Release\net10.0-windows\win-x64\CookieBridge.NativeHost.exe` 不会被自动找到，必须手动放到 `<管理器目录>\host\`。安装文档已按"手动放置"写。 |

---

## 11. 可验证的检查点汇总

| 想确认 | 命令 / 看哪里 |
| --- | --- |
| vault 是否加密 | `vault.json` 里 `protection` 为 `dpapi-current-user`，`payload` 是 base64 密文；`strings vault.json` 找不到 `LOGIN_INFO` |
| host 是否被拉起过 | `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\logs\bridge.log` 有 `host  Native messaging host started.` |
| 契约模式是否生效 | 目标文件 `mtime` 与 vault `updatedUtc` 同步；日志有 `target  Wrote VRCVC cookie file` |
| 进程内模式是否生效 | `state\hook-status.json` 里 `lastHeartbeatUtc` 在 90 秒内、`layers` 有 `L2.jarGate=resolved` |
| VRCVC 是否真的用了 cookie | VRCVC 日志里出现 `--cookies`（或播放需要登录的视频不再出现 bot 检查） |
