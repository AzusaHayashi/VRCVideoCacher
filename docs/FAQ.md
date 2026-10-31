# 常见问题

回答都对应具体代码位置，可以直接核对。找不到答案时先看
[`INSTALL.md`](./INSTALL.md) 的验证章节与 [`SECURITY.md`](./SECURITY.md) 的自查命令。

---

## 1. 为什么不直接注入 DLL？为什么"装不进" Steam 版？

**不是不想，是加载机制在 Steam 版上是关着的。**

Steam 版 VRCVideoCacher 是 .NET 10 + Avalonia 的**单文件自包含**发布
（`VRCVideoCacher.csproj`：`PublishSingleFile` + `SelfContained` + `PublishTrimmed`
+ `IncludeNativeLibrariesForSelfExtract`），磁盘上只有一个 `VRCVideoCacher.exe`
加一个 `manifest.vrmanifest`。

对真实 Steam 版进程抓 `COREHOST_TRACE` 得到的关键行：

```
Detected Single-File app bundle
Property TRUSTED_PLATFORM_ASSEMBLIES =
Property STARTUP_HOOKS = C:/.../CookieBridge.Hook.dll
Property System.StartupHookProvider.IsSupported = false
```

* 钩子路径**确实传进去了**（`Property STARTUP_HOOKS = ...`），不是我们写错路径；
* `TRUSTED_PLATFORM_ASSEMBLIES` 为空 —— 单文件宿主没有可枚举的托管程序集清单；
* `System.StartupHookProvider.IsSupported = false` —— 裁剪把启动钩子 provider 判成不可达，
  这个运行时开关一旦是 `false`，`DOTNET_STARTUP_HOOKS` 里写什么路径都不会被调用。

另外试过 4 种覆盖手段，全部无效：`System.StartupHookProvider.IsSupported=1`、
`DOTNET_System_StartupHookProvider_IsSupported=1`（`DOTNET_` 前缀形式）、
`AppContextSwitchOverrides=System.StartupHookProvider.IsSupported=true`、
exe 旁 sidecar `*.runtimeconfig.json`。

**对照实验**：同一个 Hook DLL 在**框架依赖**宿主上正常加载，日志出现
`Startup hook initialised inside the host process.`。所以 DLL 本身没问题，被挡住的只是单文件宿主的
加载机制。

反过来，`Assembly.LoadFrom(<外部 dll>)` 在单文件宿主里**是可以成功的**。区别在于：能成功的那个前提是
**应用自己**去调它（改 `Program.Main` → 编译一个 fork），而不是从外部用环境变量把钩子塞进去。

结论：

| 宿主 | 能不能进程内注入 | 走哪条路 |
| --- | --- | --- |
| Steam 版 | 不能 | **契约模式**：写 `%APPDATA%\VRCVideoCacher\youtube_cookies.txt` |
| 自编译 fork | 能（`Program.Main` 直接 `Assembly.LoadFrom` + `StartupHook.Initialize()`） | 进程内模式（增强） |

DLL 注入 / Harmony patch 也没有采用：它们要跟着每个版本的 IL 走，上游一改内部结构就在用户进程里崩，
而契约模式已经能实时生效、不用重启。详细论证见 [`ARCHITECTURE.md`](./ARCHITECTURE.md) §8–§9。

---

## 2. Cookie 会过期吗？失效了怎么办？

**会。** Cookie 的过期时间写在 Netscape 第 5 个字段（Unix 秒）。CookieBridge 会统计
最早/最晚过期时间，存进 `vault.json` 的 `metadata.earliestExpiryUtc` / `latestExpiryUtc`，
但**不会**因此拒绝写入。

注意 VRCVC 自己**不检查过期**：`Program.IsCookiesValid` 只看「文本里有没有 `youtube.com` 和
`LOGIN_INFO`」。所以过期 Cookie 会照常被传给 yt-dlp，表现为又一次
`Sign in to confirm you're not a bot`，而不是一个明确的"过期"提示。

### 失效的典型原因与处理

| 原因 | 现象 | 处理 |
| --- | --- | --- |
| 你在浏览器里长时间没访问 YouTube，YouTube 轮换了会话 token | 播放又出现 bot 检查 | 在浏览器里打开一次 `https://www.youtube.com/`（保持登录），扩展会自动重新抓取，几秒后目标文件更新 |
| 你换了 Google 账号 / 登出过 | 同上 | 同上；`LOGIN_INFO` 变了，`CookieMerger` 会直接用浏览器这份覆盖 |
| 你在**另一个**浏览器里也用同一账号浏览 YouTube | 两个浏览器的 Cookie 互相顶掉 | 只在一个浏览器装扩展（见第 5 题） |
| 使用无痕窗口 / Firefox 容器标签 | 抓到 0 条或抓不到登录态 | 在**普通窗口**登录并打开一次 YouTube |
| 会话被 Google 判为异常而撤销 | 怎么刷都没用 | 浏览器里登出再登入，然后打开 YouTube |

### 想彻底重置

```powershell
# 1) 删掉目标文件（VRCVC 侧）
Remove-Item -Force "$env:APPDATA\VRCVideoCacher\youtube_cookies.txt"

# 2) 删掉 vault（Bridge 侧）
Remove-Item -Force "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\vault.json"

# 3) 浏览器里打开一次 YouTube，等徽章变绿
```

不要只删 vault：目标文件还在，VRCVC 会继续用旧的。

---

## 3. VRCVC 升级后需要重装 CookieBridge 吗？

**不需要。** 理由：

1. Steam 更新只替换游戏目录里的 `VRCVideoCacher.exe` 和 `manifest.vrmanifest`，
   不碰 `%APPDATA%\VRCVideoCacher`（用户数据）也不碰 `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge`
   （CookieBridge 的全部状态）；
2. Chrome/Firefox 的 Native Host 注册在 `HKCU`，指向的是**我们自己的** exe，与 VRCVC 版本无关；
3. 契约点是 `youtube_cookies.txt` 这个路径，而它是 yt-dlp 的 `--cookies` 约定，跨版本稳定。

升级后正常应该**什么都不用做**。唯一需要看一眼的情况：

```powershell
# 如果装了进程内插件（仅自编译 fork），升级后检查一次反射层
Get-Content "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\state\hook-status.json" -Raw |
  ConvertFrom-Json | Select-Object vrcvcVersion, @{n='layers';e={$_.layers}}
```

某层变 `absent` → 看 [`COMPATIBILITY.md`](./COMPATIBILITY.md) §2 的降级行为，绝大多数情况不用管。

**真正需要改代码的只有一种情况**：上游不再用 `youtube_cookies.txt` 这个名字（会同时改
`YtdlManager.CookiesPath` 的初值）。判定与改法见 [`COMPATIBILITY.md`](./COMPATIBILITY.md) §4。

---

## 4. 会不会和官方扩展「VRCVideoCacher Cookies Exporter 2.0.1」冲突？同时装会怎样？

两者抓的东西一样（`youtube.com` 的 Cookie）、序列化格式一样（Netscape，7 字段，`ST-` 过滤），
**差别只在传输**：

| | 官方扩展 | CookieBridge 扩展 |
| --- | --- | --- |
| 传输 | `HTTP POST http://127.0.0.1:9696/youtube-cookies` | `chrome.runtime.sendNativeMessage("com.vrcvideocacher.cookiebridge", ...)` |
| 需要 VRCVC 在跑 | **是**（端口不存在就失败） | **否** |
| 谁写文件 | VRCVC 自己（`ApiController.ReceiveYoutubeCookies`，走 `YtdlCookieJar`） | Native Host 或 hook |
| 目标 | 同一个 `%APPDATA%\VRCVideoCacher\youtube_cookies.txt` | 同一个 |
| 额外副本 | 无 | 有 `vault.json`（密文）+ `.cookiebridge.bak` |

同时装的实际后果：

1. **不会死循环、不会互相清空**。两边都只是「写同一个文件」，后写的赢。
   CookieBridge 的 hook 在收到 VRCVC 自己的 `OnCookiesUpdated` 事件时**只写一行日志**
   （L6 仅记录），不会据此回写。
2. **进程内模式下可能互相"合并"**：hook 写文件前会 `CookieMerger.Merge(vault, 目标文件)`。
   若两边 `LOGIN_INFO` 相同，以目标文件（可能是官方扩展刚写的）为底，再把浏览器发来的覆盖上去；
   `LOGIN_INFO` 不同则浏览器这份直接赢。
3. **契约模式下（Steam 版）不会有合并**：host 直接原子写整个文件。
4. **实际建议：只装一个。** 两个都装的意义是零，而且会让"为什么 Cookie 又变了"变得难查。

一个现实差别：官方扩展要求 VRCVC 在运行才生效（否则 POST 失败），所以**VRCVC 启动前就准备好的
Cookie 只有 CookieBridge 能做到**。这也是本项目存在的理由。

---

## 5. 多浏览器 / 多账号怎么办？

### 事实

* `vault.json` **只有一个**，`youtube_cookies.txt` **只有一个**；
* 每次同步都是整份覆盖（进程内模式下会与目标文件合并，但 `LOGIN_INFO` 不同时浏览器这份赢）；
* 所以**最后发生同步的那个浏览器 / 那个账号赢**。

### 多浏览器（Chrome + Firefox 都装了扩展）

会互相顶。做法：

1. 只在**你实际用来登录 YouTube 的那个**浏览器里装扩展；
2. 另一个浏览器如果有扩展，移除或至少不要在那边登录 YouTube；
3. Native Host 注册支持 6 个浏览器（Chrome / Chromium / Edge / Brave / Vivaldi / Firefox），
   但**注册 ≠ 必须装扩展**；注册只是"允许这个浏览器连"，不装扩展就没人连。

### 多账号（同一浏览器多配置文件，或同一账号换号）

* Chrome 的每个**配置文件（profile）**是独立的 cookie store。`utils.mjs` 里
  `getCurrentCookieStoreId()` 会按当前标签页所在的 store 抓取，所以你在哪个配置文件里打开 YouTube，
  抓到的就是那个配置文件的 Cookie；
* 结论：**切账号 = 在对应配置文件里打开一次 YouTube**，然后等徽章变绿；
* 不想要"自动切换"的话，就别在另一个配置文件里打开 YouTube。

### 想把某个账号固定下来

```powershell
# 1) 用目标账号在浏览器里打开 YouTube，确认徽章绿
# 2) 复制一份目标文件做快照（注意：这是明文凭据，别放共享目录）
Copy-Item "$env:APPDATA\VRCVideoCacher\youtube_cookies.txt" "$env:USERPROFILE\yt-cookies-snapshot.txt"
# 3) 想让 VRCVC 用回那一份时
Copy-Item "$env:USERPROFILE\yt-cookies-snapshot.txt" "$env:APPDATA\VRCVideoCacher\youtube_cookies.txt" -Force
```

快照会随时间失效（token 轮换），不是长期方案。

---

## 6. 我在 VRCVC 里点了「清除 Cookie」，为什么不再自动写回了？

这是 hook 的**抑制逻辑**，故意设计的。对应
`bridge/CookieBridge.Hook/HookRuntime.cs`：

1. hook 监视目标文件（`StartTargetWatcher`）；
2. 文件被外部改动（不是 hook 自己写的，2 秒内的自写不算）后读取内容；
3. 若内容**已不可用**（不含 `youtube.com` + `LOGIN_INFO`）→ 判定为「用户在 VRCVC 里清除了 Cookie」；
4. 置 `_suppressUntilVaultChanges = true`，日志写
   `VRCVideoCacher's cookie file was cleared externally; pausing re-sync until a new vault update.`；
5. 之后所有**非强制**的同步都会被跳过（启动同步、因 `pending.sync` 触发的同步等）；
   只有 `reason == "vault-changed"` 或从管理器/管道发起的 `sync-now`（`force: true`）能穿过去。
   跳过时按 30 分钟限流写一条 DEBUG：`Skipping sync (...); waiting for a new vault update.`。

**为什么这样设计**：用户点"清除"是一个明示动作。如果立刻被写回来，这个按钮就等于不存在，
用户会觉得软件在跟他作对。

**怎么恢复**：让浏览器产生**新的 vault 更新**——在浏览器里打开一次 `https://www.youtube.com/`。
`vault.json` 变化后，`reason == "vault-changed"` 不受抑制影响，同步立刻恢复，并且成功后自动清掉
`_suppressUntilVaultChanges`。

只有 `vault.json` 变化能解除；单纯等时间不会解除。

**重要区别 —— 契约模式（Steam 版）没有这个抑制**：Steam 版没有 hook 在进程内，写文件的是
Native Host，它不知道 VRCVC 里发生了什么。所以：

* 你在 VRCVC 里点了"清除" → 文件被删；
* 浏览器下一次同步（页面加载 / cookie 变化）→ host 又把文件写回来。

想让"清除"在 Steam 版上真正长期生效：在浏览器里**移除扩展**，或在 VRCVC 里把
`YtdlpUseCookies` 关掉。

---

## 7. 日志在哪？

| 日志 | 路径 | 内容 |
| --- | --- | --- |
| CookieBridge 主日志 | `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\logs\bridge.log`（滚动：`.1` `.2` `.3`，单文件 ≤1 MiB） | host / vault / target / hook / sync / install / pipe |
| Hook 心跳与反射层 | `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\state\hook-status.json` | pid、版本、lastSyncUtc、syncCount、`layers`、lastError |
| 扩展侧最近错误 | `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\state\last-error.txt` | 机器码 + 时间 |
| 扩展状态页 | 点扩展图标 → 状态页（或 `chrome://extensions` → 详情 → 扩展程序选项） | 上次同步时间、条数、是否写入文件、错误码 |
| VRCVC 自己的日志 | `%APPDATA%\VRCVideoCacher\Logs\VRCVideoCacher*.log` | 含传给 yt-dlp 的完整参数（`--cookies "<路径>"`） |

```powershell
# 看最近 40 行
Get-Content "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\logs\bridge.log" -Tail 40

# 只看错误与警告
Get-ChildItem "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\logs\bridge.log*" |
  Select-String -Pattern '\[ERROR\]|\[WARN \]'

# 实时跟踪
Get-Content "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\logs\bridge.log" -Wait -Tail 0
```

一行日志的格式：

```
2026-11-01T00:30:12.456Z [INFO ] vault       Saved 37 cookies (login=True, sha=6f1c2a9b3d4e) from chrome.
│                        │       │           └ 消息
│                        │       └ source（host / vault / target / hook / sync / merge / reflect / pipe / install / manager / backup / lock / atomic）
│                        └ 级别（INFO / WARN / ERROR / DEBUG）
└ UTC 时间
```

日志里**不会**出现 Cookie 名-值对（`SecretRedactor` 兜底）。想把日志给别人看之前，
先跑一遍 [`SECURITY.md`](./SECURITY.md) §5 的自查命令。

---

## 8. 怎么确认它真的生效了？

四层证据，从弱到强：

> 想一次看完所有状态（vault / 目标文件 / VRCVC 版本与兼容等级 / hook 是否在线 / 已注册的浏览器）：
>
> ```powershell
> & "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\host\CookieBridge.NativeHost.exe" --status
> ```
>
> 退出码 `0` = vault 可读；`3` = vault 缺失或不可读。

### 证据 1：扩展徽章

`chrome://extensions`（或状态页）显示上次同步 `ok:true`、`targetWritten:true`，徽章 `OK` 绿色。
这只是"宿主说它写了"。

### 证据 2：vault 是密文且是新的
```powershell
$v = Get-Content "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\vault.json" -Raw | ConvertFrom-Json
$v.protection; $v.metadata.cookieCount; $v.metadata.loginCookiePresent; $v.updatedUtc
Select-String -Path "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\vault.json" -Pattern 'LOGIN_INFO' -SimpleMatch
```

期望：`dpapi-current-user`、条数 > 0、`True`、时间是刚才、最后一条命令**无输出**（没有明文）。

### 证据 3：目标文件确实被写

```powershell
$t = "$env:APPDATA\VRCVideoCacher\youtube_cookies.txt"
Get-Item $t | Select-Object Length, LastWriteTime
(Get-Content $t | Where-Object { $_ -and $_ -notlike '#*' -and ($_ -split "`t").Count -eq 7 }).Count
(Select-String -Path $t -Pattern 'LOGIN_INFO' -SimpleMatch).Count
```

期望：条数 > 0，`LOGIN_INFO` 命中 ≥ 1，`LastWriteTime` 与 vault 的 `updatedUtc` 接近。

### 证据 4（最硬）：VRCVC 真的把它交给了 yt-dlp

VRCVC 会把 yt-dlp 参数打进自己的日志（`VideoDownloader.cs:213`）：

```powershell
Get-ChildItem "$env:APPDATA\VRCVideoCacher\Logs\VRCVideoCacher*.log" |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
  ForEach-Object { Select-String -Path $_.FullName -Pattern '--cookies' | Select-Object -Last 3 }
```

看到 `--cookies "C:\Users\<你>\AppData\Roaming\VRCVideoCacher\youtube_cookies.txt"` = 真的在用。

> 若这一行**没有**出现，先查 `%APPDATA%\VRCVideoCacher\Config.json` 里 `YtdlpUseCookies`
> 是否为 `true`（默认 `true`），再查第 3 步的 `LOGIN_INFO`。

### 最终判据

放一个**需要登录才能看**的视频（例如年龄限制视频），不再出现
`Sign in to confirm you're not a bot`，即端到端生效。

---

## 9. 其他高频小问题

**Q：VRCVC 必须开着吗？**
不需要。这正是契约模式的意义：host 直接写文件，VRCVC 下次启动/下次 yt-dlp 调用就读到。
只有官方扩展那条 HTTP 路线才要求 VRCVC 在跑。

**Q：为什么默认不注册 `DOTNET_STARTUP_HOOKS`？**
因为对 Steam 版无效（见上一题），而它是**全用户共享**的变量：写进去后你机器上每个支持启动钩子的
.NET 进程启动时都会尝试加载这个 DLL。所以 `HookInstallation.Install(..., registerStartupHook: false)`
默认不写；脚本要加 `-RegisterStartupHook`，管理器要勾「注册启动钩子」。只给自编译 fork 用。

**Q：会占端口吗？**
不会。整条链路零监听端口、零 HTTP。命名管道 `VRCVideoCacherCookieBridge` 只用于本地管理器查询状态，
且是可选能力（裁剪宿主里可能不可用）。

**Q：会上传我的 Cookie 吗？**
不会。Native Host / Manager / Hook 只读写本地文件；扩展只读 `youtube.com` 的 cookie 并
`sendNativeMessage`，没有 `fetch`、没有远程资源（`browser-extension/tests/manifest.test.mjs` 校验）。

**Q：徽章一直是黄色 `!` 怎么办？**
黄色 = vault 已更新但宿主没写目标文件。常见两种情况：① 进程内模式（由 hook 落地，属正常，
看 `state\hook-status.json` 的 `lastSyncOutcome`）；② VRCVC 正在跑 yt-dlp，host 等了 10 秒仍忙
（日志 `yt-dlp still running; deferring the cookie write.`）→ 等播放结束再看。

**Q：为什么有两个 Cookie 文件副本？**
`youtube_cookies.txt.cookiebridge.bak` 是原子替换留下的上一版；`backups\<时间戳>\` 是管理器手动备份。
都是**明文**，不需要就删（见 [`SECURITY.md`](./SECURITY.md) §6）。

**Q：能不能在 Linux 上用？**
当前不设计支持：vault 加密用的是 Windows DPAPI（crypt32），`SecretProtector` 只有这一套实现。

**Q：卸载后 VRCVC 还能正常用吗？**
能。`youtube_cookies.txt` 是 VRCVC 本来就认的文件；删掉它 VRCVC 只是回到"没有 Cookie"的原生状态
（播放可能遇 bot 检查，和你安装前一样）。

**Q：会改 VRCVideoCacher 的安装目录吗？**
不会。Steam 更新能覆盖的目录里我们一个字节都不写。
