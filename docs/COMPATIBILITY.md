# 兼容性矩阵与降级行为

本文回答三个问题：**哪些 VRCVideoCacher 版本能用**、**每个反射层缺了会怎样**、
**VRCVC 升级后怎么自查**。判定逻辑的唯一实现在
`bridge/CookieBridge.Core/CompatibilityMatrix.cs` 与 `bridge/CookieBridge.Hook/VrcvcBridge.cs`。

---

## 1. 兼容矩阵

| 等级 | 版本范围 | 标签（管理器显示） | 判定依据 | 实际行为 |
| --- | --- | --- | --- | --- |
| `Verified` | 恰好 `2026.10.29` | 已验证版本 | `parsed == VerifiedVersion` | 契约写入 + 进程内锁 + UI 通知全部按设计工作 |
| `Supported` | `>= 2025.1.1` 且 `< 2026.10.29` | 兼容（旧版本） | `parsed >= MinimumSupported` | 同上；个别反射层若缺失会自动回退到 Cookie 文件写入 |
| `UntestedNewer` | `> 2026.10.29` | 未验证（新版本） | `parsed > VerifiedVersion` | **默认启用**，按反射逐层探测自动降级；不改 VRCVC 任何文件 |
| `UntestedOlder` | `< 2025.1.1` | 未验证（过旧） | 其余数值版本 | 只保证契约模式（写 `youtube_cookies.txt`），不保证进程内通知 |
| `Unknown` | 解析不出 `YYYY.M[.D]` | 未知版本 | `Parse()` 返回 null | 契约模式仍启用；提示运行一次兼容性探测 |

`CompatReport.HookAllowed` 在所有等级下都是 `true` —— **任何版本都不会阻止 VRCVC 启动**，
也不会阻止 hook 装载。这是刻意的：设计只依赖 yt-dlp 的 `--cookies` 契约 + 若干独立反射探测，
未知版本是「降级」而不是「禁用」。

常数：

```csharp
CompatibilityMatrix.VerifiedVersion  = new Version(2026, 10, 29)
CompatibilityMatrix.MinimumSupported = new Version(2025, 1, 1)
```

### 版本字符串怎么解析

```csharp
[GeneratedRegex(@"^(\d{4})\.(\d{1,2})(?:\.(\d{1,2}))?")]
```

* 只取前导 `YYYY.M[.D]`，后面任何东西都被忽略 →
  `2026.10.29-sabr`、`2026.10.29+abc1234`、`2026.10.29 (Steam)` 都解析为 `2026.10.29`；
* 因此 **SABR 测试构建 `2026.10.29-sabr` 落在 `Verified`**。

### 版本号从哪来（`VrcvcLocator.DetectVersion`）

按顺序取第一个能用的：

| 顺序 | 来源 | `VrcvcVersionInfo.Source` | 说明 |
| --- | --- | --- | --- |
| 1 | 进程内 Hook 反射到的 `Program.Version` | `running-process` | 最准；仅进程内模式可用 |
| 2 | `VRCVideoCacher.exe` 的 PE 版本资源（`ProductVersion` / `FileVersion` / `ProductName` 中匹配 `\d{4}\.\d` 的第一个） | `pe-file-version` | 契约模式下的常规来源 |
| 3 | 最新的 `%APPDATA%\VRCVideoCacher\Logs\VRCVideoCacher*.log` 前 200 行里的 `VRCVideoCacher version <X>` | `log-header` | 兜底 |
| 4 | 无 | `unknown` | 对应 `CompatLevel.Unknown` |

`Program.Version` 本体是 `AssemblyInformationalVersion`，值来自 `VRCVideoCacher.csproj` 的
`<Version>2026.10.29</Version>`；`IncludeSourceRevisionInInformationalVersion=false`，所以
不会有 `+<sha>` 后缀。Hook 侧另外会 `Split('+')[0].Trim()` 兜一层。

---

## 2. Hook 的反射能力层（L1–L6）

`VrcvcBridge.TryCreate()` 对每个能力**独立探测**，互不影响，也不用 Harmony / IL patch / 偏移。
探测结果写进 `HookStatus.Layers`，落盘到
`%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\state\hook-status.json`。

| 层 | 目标 | 反射成员（`VRCVideoCacher` 程序集内） | 探测通过时 `Layers` 值 | 缺失时的降级行为 |
| --- | --- | --- | --- | --- |
| **L1** | Cookie 文件路径 | `VRCVideoCacher.YTDL.YtdlManager` 的静态字段 `CookiesPath` | `L1.cookiePath = resolved` | 用契约路径 `%APPDATA%\VRCVideoCacher\youtube_cookies.txt`（`Layers` 记为 `fallback`）。这是**唯一必须存在的契约**，而它来自 yt-dlp，不是 VRCVC 内部实现，所以「降级」实际上等于正常工作 |
| **L2** | 进程内串行化 | `VRCVideoCacher.YTDL.YtdlCookieJar.AcquireAsync([CancellationToken])` | `L2.jarGate = resolved` | 改用跨进程互斥量 `Local\VRCVideoCacherCookieBridge.CookieWrite`（`absent`）。风险从「不会与 yt-dlp 抢文件」降为「靠 5 秒锁超时与内容比较避开」，仍不会写坏文件（原子替换），但极小概率丢掉一次 token 轮换 |
| **L3** | UI 刷新通知 | `VRCVideoCacher.Program.NotifyCookiesUpdated()`（无参静态） | `L3.notify = resolved` | 静默跳过（`absent`）。副作用只是 VRCVC 界面上的 Cookie 状态不立即刷新，功能不受影响 |
| **L4** | 有效性判定 | `VRCVideoCacher.Program.IsCookiesValid(string)` | `L4.validity = resolved` | 用本地等价实现 `NetscapeCookieFile.IsValidForVrcvc()`（"同时含 `youtube.com` 与 `LOGIN_INFO`"，与上游逐字一致）。两者当前语义相同，缺失只意味着「规则可能落后于上游」 |
| **L5** | 版本号 | `VRCVideoCacher.Program` 静态字段 `Version`，否则程序集 `AssemblyInformationalVersionAttribute` | `L5.version = resolved` | 记为 `unknown`（`absent`），兼容性判定落到 `Unknown` 等级，功能不变 |
| **L6** | 观察上游自己的 Cookie 事件 | `VRCVideoCacher.Program` 静态事件 `OnCookiesUpdated` | `L6.cookieEvent = resolved` | 不订阅（`absent`）。**仅用于记录**：hook 收到该事件时只写一行 DEBUG 日志（`Host reported a cookie update from its own API.`），**不会**据此改写 vault 或目标文件 —— vault 始终是权威 |

补充说明：

* 探测失败时 `Layers` 的值可能带异常类型名，例如 `L1.cookiePath = fallback(NullReferenceException)`；
* 除了六层，还有一个伪键 `mode`：当连 `VRCVideoCacher.Program` 类型都找不到时，
  `Layers["mode"] = "contract-only"`，此时 hook 只做「vault → 目标文件」的搬运；
* `HookStatus.Layers` 的代码注释写的是「L1..L5」，实际已到 L6，以本文为准；
* **前提是 hook 真的运行在 VRCVC 进程内**。Steam 版做不到（单文件宿主关闭启动钩子），
  且用户级 `DOTNET_STARTUP_HOOKS` 现在是 **opt-in**（`HookInstallation.Install(..., registerStartupHook: false)`
  默认不写）。所以 **Steam 用户看不到任何 Layers 是正常的**——那不是故障，见 §3.4。

### 层的组合意味着什么

| 场景 | 可用层 | 行为 |
| --- | --- | --- |
| 自编译 fork + Steam 版 VRCVC 内部结构 | L1–L6 全 resolved（`2026.10.29` 实测范围） | 最优：合并 jar、进程内锁、UI 刷新 |
| 上游大改内部结构（例如把 `YtdlCookieJar` 删了） | L1 + L3/L4/L5 可能仍在，L2 缺失 | 仍能同步；改用跨进程互斥量 |
| 上游把 `youtube_cookies.txt` 改名了 | L1 仍 resolved（读的是同一个字段，值变了） | 自动跟随新路径；契约路径仅在 L1 缺失时使用 |
| 上游把字段/类都重构了 | 只剩 `mode = contract-only` | 只按契约路径写文件；若上游同时改了文件名，就需要改代码（见 §4） |
| 完全不是 VRCVideoCacher 的宿主 | `Initialize()` 写日志后 5 分钟等不到宿主程序集，`LastError = host-assembly-not-found`，转被动 | 无副作用 |

---

## 3. VRCVC 升级后如何自查

### 3.1 用管理器（推荐）

1. 打开 `CookieBridge.Manager.exe`；
2. 看顶部状态：VRCVC 版本、兼容等级（`已验证 / 兼容（旧版本）/ 未验证（新版本）/ 过旧 / 未知`）；
3. 看 **Layers** 面板：每行应显示 `L1.cookiePath … L6.cookieEvent` 各自 `resolved / fallback / absent`；
4. 若等级变成「未验证（新版本）」但所有层仍 `resolved` → 正常，属预期（新版本比测试版本新）；
5. 若某层变成 `absent` → 查阅 §2 对应行的「降级行为」，判断是否可接受。

管理器读取的是 `state\hook-status.json`（心跳 ≤ 90 秒且进程存活才算「正在运行」）。

### 3.2 不用管理器（纯命令行）

```powershell
$root = "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge"

# 1) 当前心跳与反射层
Get-Content "$root\state\hook-status.json" -Raw | ConvertFrom-Json |
  Select-Object vrcvcVersion, hookVersion, lastSyncUtc, lastSyncOutcome, lastHeartbeatUtc,
                @{n='layers';e={ $_.layers.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" } }}

# 2) 心跳是否新鲜（<=90 秒）
$s = Get-Content "$root\state\hook-status.json" -Raw | ConvertFrom-Json
[datetimeoffset]::UtcNow - [datetimeoffset]$s.lastHeartbeatUtc

# 3) 强制重新探测：停 VRCVC → 删状态文件 → 重启 VRCVC
Get-Process VRCVideoCacher -ErrorAction SilentlyContinue | Stop-Process
Remove-Item "$root\state\hook-status.json" -ErrorAction SilentlyContinue
# 从 Steam 启动 VRCVC，然后重新读 hook-status.json
```

### 3.3 进程内模式下让 hook 立刻重新探测（不重启）

hook 监听命名管道 `VRCVideoCacherCookieBridge`，支持 `reload`：清空反射缓存并重新 `TryCreate`，
随后强制同步一次。

```powershell
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'VRCVideoCacherCookieBridge', 'InOut')
$pipe.Connect(2000)
$w = New-Object System.IO.StreamWriter($pipe); $w.AutoFlush = $true
$r = New-Object System.IO.StreamReader($pipe)
$w.WriteLine('{"command":"reload"}')
$r.ReadLine()
```

同样可以把 `reload` 换成 `sync-now` 或 `status`。

> 管道是**可选**能力：单文件裁剪宿主可能根本没带 `System.IO.Pipes`，此时
> `HookControlPipe.TryStart` 返回 false，日志里出现
> `pipe  Control pipe unavailable in this host (...)`，管理器就退回只读 `hook-status.json`。

### 3.4 契约模式下的自查（Steam 版常态）

契约模式没有反射层可看，判据是文件：

```powershell
# 目标文件是否有效 + 条数
$t = "$env:APPDATA\VRCVideoCacher\youtube_cookies.txt"
(Get-Content $t | Where-Object { $_ -and $_ -notlike '#*' -and ($_ -split "`t").Count -eq 7 }).Count
Select-String -Path $t -Pattern 'LOGIN_INFO' -SimpleMatch | Measure-Object | Select-Object -Expand Count
```

条数 ≥ 1 且 `LOGIN_INFO` 命中 ≥ 1 → VRCVC 的 `Program.IsCookiesValid` 会通过，
`GenerateYtdlArgs` 会拼上 `--cookies`。

---

## 4. 什么情况下才需要改代码

| 触发条件 | 需要改哪里 | 是否影响 Steam 版 |
| --- | --- | --- |
| 上游把 `youtube_cookies.txt` 改名（同时改 `YtdlManager.CookiesPath` 的初值） | 不用改：L1 反射会读到新值。只有 L1 也读不到时才需要改 `BridgePaths.VrcvcCookieFile` | 仅当 L1 不可用（Steam 版就是这种） |
| 上游改变 `Program.IsCookiesValid` 的判定规则（例如不再要求 `LOGIN_INFO`） | 更新 `NetscapeCookieFile.IsValidForVrcvc`；`docs/CONTRACT.md` §4 同步 | 是 |
| 上游改变 Netscape 字段约定（字段数/顺序、`#HttpOnly_` 前缀处理） | `NetscapeCookieFile.Parse/Canonicalize`，以及扩展的 `serializeNetscapeCookies` | 是 |
| 上游删除或重命名 `YtdlCookieJar` | 无需改代码：L2 自动降级为跨进程互斥量。若想恢复「零窗口」保序，需实现新的等锁方式 | 是（但 Steam 版本来就拿不到 L2） |
| 上游把 `FilterCookies` 的 `ST-` 规则改掉 | `NetscapeCookieFile.Canonicalize` 的 `ST-` 过滤 + 扩展侧过滤 | 是 |
| 上游把 Native API 端口 9696 的 `/youtube-cookies` 取消 | 与本产品无关（CookieBridge 不走 HTTP）。只影响「同时装官方扩展」的场景 | 否 |
| 上游开始用 DPAPI/凭据管理器保护 cookie 文件 | 需要重写目标文件写入路径（会变成格式不兼容） | 是，且属破坏性变更 |
| `System.StartupHookProvider.IsSupported` 变成 true（上游不再裁剪） | 无需改代码：`DOTNET_STARTUP_HOOKS` 自动生效；`CookieBridgePlugin.TryLoad()` 可保留（幂等 `_attempted` 保护） | 是（变好） |
| 出现新的浏览器（未在 `NativeHostRegistration.Targets` 里） | 在 `NativeHostRegistration.Targets` 加一行 `BrowserHostTarget` | 是 |
| 扩展 ID 变化（重新生成 `key`） | 重新注册 Native Host（`allowed_origins` 必须同步）；`browser-extension/extension-id.txt` 更新 | 是 |

判定原则：**只要 `youtube_cookies.txt` 这个契约还在，契约模式就不需要改代码**。

---

## 5. 未验证 / 需要实测确认的部分

| 项 | 状态 |
| --- | --- |
| `> 2026.10.29` 的上游版本上 L1–L6 是否仍全部 `resolved` | 未验证（本分支唯一实测版本是 `2026.10.29`） |
| `>= 2025.1.1` 区间内每个版本的具体层可用性 | 未验证；矩阵给的是「结构性兼容」判断（`Program` / `YtdlManager` / `YtdlCookieJar` 结构一致） |
| Linux 构建 | 当前不设计支持（DPAPI 是 Windows API，`SecretProtector` 只有 crypt32 实现）；`CookieBridge.Tests` 只在 Windows 上通过 |
| 上游 SABR 构建（`2026.10.29-sabr`） | 解析为 `Verified`（版本正则只看数字部分）；层可用性未单独实测，按同源代码判断应与主版本一致 |
| trimmed 宿主里 `System.IO.Pipes` 是否可用 | 每个版本可能不同；`HookControlPipe` 已做成失败即降级，日志会记录 |
