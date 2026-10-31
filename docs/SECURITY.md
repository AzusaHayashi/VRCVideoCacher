# CookieBridge 安全模型

本文只写当前代码实际做到的事，以及做不到的事。所有结论都能在代码或文件里核对；
无法确认的地方明确标注「未验证」。

CookieBridge 处理的是**可登录的 YouTube 会话凭据**（Netscape Cookie 里的 `LOGIN_INFO`、`SID`、
`__Secure-*PSIDTS` 等）。拿到它们等于拿到该 Google 账号的 YouTube 会话。所以下面按
「能被谁拿到、怎么拿到、怎么减少暴露」来组织。

---

## 1. 资产清单

| 资产 | 位置 | 形态 |
| --- | --- | --- |
| Cookie 明文（Netscape 文本） | 内存；`%APPDATA%\VRCVideoCacher\youtube_cookies.txt`；`...\.cookiebridge.bak`；`%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\backups\<时间戳>\youtube_cookies.txt` | **明文** |
| Cookie 密文 | `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\vault.json` | DPAPI(CurrentUser) 密文 + base64 |
| 非敏感元数据 | `vault.json` 的 `metadata`、`state\hook-status.json`、`install.json` | 明文（无 Cookie 名/值） |
| 浏览器侧凭据 | 浏览器自身的 cookie 数据库 | 浏览器加密 |
| 安装/开关状态 | 注册表 HKCU、`%LOCALAPPDATA%` 下若干 json/flag 文件 | 明文 |

`vault.json` 的 `metadata` 只允许出现：来源浏览器、扩展版本、Cookie 条数、
是否存在登录 Cookie、最早/最晚过期时间、错误类型名。**不允许**出现任何 Cookie 名或值
（`docs/CONTRACT.md` §3、§8）。

---

## 2. 威胁模型

### T1 同一 Windows 用户下的其他进程（最主要的威胁）

同一用户身份运行的任意程序（含恶意软件、被投毒的 npm/pip 包、另一个"看起来无害"的工具）都能：

* 读 `%APPDATA%\VRCVideoCacher\youtube_cookies.txt` —— **明文，无需任何特殊能力**；
* 调用 `CryptUnprotectData` 解开 `vault.json` —— DPAPI 的 `CurrentUser` 作用域是**用户边界**，
  不是进程边界；
* 读 `backups\` 与 `.cookiebridge.bak` —— 明文。

也就是说：**DPAPI 防的是"文件被别人拿走"，不防"同一用户下的其他程序"**。
这是 DPAPI 的设计前提，不是实现缺陷。CookieBridge 在这条威胁上能做的只有
「缩小副本数量 + 让所有副本路径固定可审计」，见 §4。

### T2 同机器的其他 Windows 用户 / 非管理员

* 其他用户默认无权进入本用户的 `%LOCALAPPDATA%` / `%APPDATA%`（NTFS 用户配置文件隔离）；
* `vault.json` 另外做了显式 DACL 收窄（best-effort，见 §3.2）；
* 即使拿到 `vault.json`，另一个用户身份调用 `CryptUnprotectData` 会失败（DPAPI 主密钥属于原用户 SID）。

结论：**跨用户隔离成立**，前提是文件系统权限没被手动放宽、且没有把文件复制到共享目录。

### T3 备份 / 拷贝泄露

用户或第三方工具把 `%APPDATA%\VRCVideoCacher` 或 `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge`
整个拷贝到网盘、U 盘、另一台机器、云同步目录：

* `vault.json` —— 换机器/换账号后解不开（DPAPI 绑定用户+机器）；
* `youtube_cookies.txt` / `.bak` / `backups\*\youtube_cookies.txt` —— **明文，直接可用**。

结论：**vault 可以安全拷贝，目标文件与备份不能**。文档要求在卸载/迁移时优先删除明文副本。

### T4 日志泄露

需要防的是「错误信息把 Cookie 值带进日志」这一类事故（第三方异常消息里带请求头、带文件内容）。
防线见 §3.5。残留风险：脱敏是**基于已知形状**的，不是语义分析。

### T5 浏览器侧

* 扩展持有 `cookies` 权限 → 能读 `youtube.com` 域下的全部 Cookie（包含与登录无关的分析类 cookie），
  以及 `cookies.getAllCookieStores()` 返回的存储分区信息；
* 扩展持有 `nativeMessaging` 权限 → 能拉起并只与 `com.vrcvideocacher.cookiebridge` 通信，
  宿主侧用 `allowed_origins` / `allowed_extensions` 白名单反向约束「只有这个扩展 ID 能连」；
* 扩展**没有** `tabs`、`webRequest`、`<all_urls>`；`host_permissions` 只有 `*://*.youtube.com/*`。
  `utils.mjs` 里的 `chrome.tabs.query({active:true,currentWindow:true})` 只用来取 tab id 与
  Firefox 的 `cookieStoreId`，不读取 URL/标题，也不需要 `tabs` 权限；
* 传输只走 Native Messaging（无 HTTP、无端口、无 CORS）。

结论：扩展的能力边界是「youtube.com 的 cookie」+「写本地 vault / 目标文件」，超出这个范围它做不到。
真正的风险是**用户信任了伪造的扩展**（见 T6）。

### T6 假冒 / 冲突的第三方组件

* 假冒的扩展：Chrome 未打包扩展的 ID 由 manifest 的 `key` 决定；只要 `key` 固定，ID 就固定
  `fdjmcdfolkgicljalacnlemepfhmnmfp`。用户必须核对 `chrome://extensions` 里显示的 ID；
* 假冒的 Native Host：注册表指向的 manifest 路径是 HKCU 下用户可写的位置，
  任何同用户进程都能改写它（见 T1 的同类问题）；
* 与上游官方扩展「VRCVideoCacher Cookies Exporter 2.0.1」并存：两者都会写同一个目标文件
  （官方扩展走 `POST http://127.0.0.1:9696/youtube-cookies`，需要 VRCVC 在跑）。
  见 `docs/FAQ.md` 的「同时装会怎样」。

### 不在威胁模型内

* 远程网络攻击者（没有本地代码执行）；
* 具有管理员/SYSTEM 权限的对手（能读进程内存、能改 DACL、能装驱动）；
* 能物理接触并已登录该用户会话的对手；
* 浏览器自身被攻破；
* 上游 VRCVideoCacher 自身的行为（它本来就要把 Cookie 交给 yt-dlp 和 YouTube）。

---

## 3. 每一层做了什么

### 3.1 加密：Windows DPAPI，CurrentUser 作用域

`bridge/CookieBridge.Core/Win32Interop.cs` 的 `SecretProtector`：

* `CryptProtectData` / `CryptUnprotectData`，`CRYPTPROTECT_UI_FORBIDDEN`（永不弹 UI，
  在无人值守的 host 进程里也不会卡住）；
* 作用域 = 当前用户（**没有** `CRYPTPROTECT_LOCAL_MACHINE`），即密文绑定
  (用户 SID + 机器)；
* 固定附加熵：`BridgePaths.VaultEntropy = "VRCVideoCacher-CookieBridge/vault/v1"`。
  熵不是密钥，但它让「同一用户下另一个也用 DPAPI 的程序」不能在无差别遍历时顺手解开
  本产品的 blob；
* `vault.json` 里同时存 `payloadSha256`，读取顺序是：
  `formatVersion == 1` → `payloadSha256` 校验 → DPAPI 解密 → 可用性校验
  （`bridge/CookieBridge.Core/CookieVault.cs` `Load()`）；
* 明文在托管内存里用 `CryptographicOperations.ZeroMemory` 清零；
  DPAPI 的输入缓冲在释放前逐字节写 0（`FreeBlob`）。

能在 `vault.json` 上做的验证：

```powershell
# 1) 确认没有明文泄露
Select-String -Path "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\vault.json" -Pattern "LOGIN_INFO|youtube.com\s" -SimpleMatch

# 2) 确认 protection 字段
(Get-Content "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\vault.json" -Raw | ConvertFrom-Json).protection
# 期望：dpapi-current-user
```

### 3.2 ACL：vault 只给当前用户

`FileAcl.TryRestrictToCurrentUser`（`Win32Interop.cs`）在 `AtomicFile.WriteAllText(..., restrictAcl: true)`
时对**临时文件**设置一个只含当前用户 SID、`PROTECTED_DACL` 的 DACL，然后才 `File.Replace` 换入
（换入后 DACL 随文件走）。

* 只在 **vault** 上启用（`CookieVault.Save`）；
* 目标文件 `youtube_cookies.txt` **刻意不收窄**（`restrictAcl: false`）：它必须让 VRCVC / yt-dlp
  在同一用户会话里正常读写，收窄不会带来任何安全收益（同用户本来就能读）；
* 失败**不致命**：只写一条 `ACL hardening skipped: ...` 警告，vault 仍处于 DPAPI 加密之下。

```powershell
# 查看 vault 的 DACL 是否只剩自己
icacls "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\vault.json"
```

### 3.3 原子写：任何时刻读到的都是完整文件

`bridge/CookieBridge.Core/AtomicFile.cs`：

1. 写入同目录临时文件 `.<name>.<guid>.tmp`，`FileOptions.WriteThrough` + `Flush(flushToDisk: true)`；
2. （可选）对临时文件设置 ACL；
3. `File.Replace(tmp, dest, backupPath, ignoreMetadataErrors: true)`；跨卷/异形文件系统失败时
   退回 `File.Move(overwrite: true)`（NTFS 上仍是一次替换）；
4. `finally` 里删除残留临时文件。

为什么重要：VRCVC 用 `File.ReadAllText` 在任意时刻读这个文件。没有原子替换，就会有一个窗口
读到半截 jar —— 那会直接变成「Sign in to confirm you're not a bot」。

代价：每次写入都会留下一个 `.cookiebridge.bak`（明文，见 §5）。

### 3.4 并发：两级锁

* **进程内**（进程内模式）：`YtdlCookieJar.AcquireAsync()` —— VRCVC 自己用来串行化所有 yt-dlp
  调用的信号量（`SabrExtractor`、`VideoDownloader`、`VideoId`、`ApiController` 四处持有它）。
  拿到它就等于「此刻没有 yt-dlp 在读写这个文件」。
* **跨进程**（契约模式）：命名互斥量 `Local\VRCVideoCacherCookieBridge.CookieWrite`，
  `AbandonedMutexException` 视为可接管（原子替换保证文件本身一致）。
* host 在 VRCVC 正跑但没有 hook 时，额外 `YtdlpActivity.WaitUntilIdle`（最多 10 秒），
  宁可不写，也不与 yt-dlp 的 token 轮换抢文件。

锁不保护机密性，但保护**会话可用性**：一次写坏的 jar 会让 YouTube 认为会话异常。

### 3.5 日志脱敏

`bridge/CookieBridge.Core/BridgeLog.cs`：

* 调用侧纪律：只传条数、路径、字节数、耗时、异常类型名。契约 §8 明文禁止传 Cookie 内容；
* 兜底 `SecretRedactor.Redact()`：
  * 一行里出现 ≥3 个 `\t`（典型 Netscape 行）→ 在第一个 `\t` 处截断并标 `<redacted-cookie-row>`；
  * 命中已知凭据名（`LOGIN_INFO`、`SID`、`HSID`、`SSID`、`APISID`、`SAPISID`、
    `__Secure-1/3PSID`、`__Secure-1/3PAPISID`、`__Secure-1/3PSIDTS`、`SIDCC`、
    `__Secure-1/3PSIDCC`、`YSC`、`PREF`）后紧跟 `\t` → 把该行剩余部分替换为 `<redacted>`；
  * 异常消息（含最多 3 层 inner exception）同样过一遍脱敏；
* 日志滚动：单文件 ≤ 1 MiB，最多 3 份（`.1` `.2` `.3`），超限即轮转，不会无限增长；
* 扩展侧：`errorDetail` 截断 200 字符；`chrome.storage.local["syncStatus"]` 字段白名单内
  **没有** Cookie 名/值；`console.log` 只打印条数和事件类型。

### 3.6 最小权限安装

| 动作 | 作用域 | 是否需要管理员 |
| --- | --- | --- |
| Native Host 注册 | `HKCU\Software\...\NativeMessagingHosts\com.vrcvideocacher.cookiebridge` | 否 |
| 插件环境变量（**opt-in**，默认不写） | HKCU 用户环境变量 `DOTNET_STARTUP_HOOKS`（追加，不覆盖别人的 hook） | 否 |
| 文件 | 只在 `%LOCALAPPDATA%` / `%APPDATA%` 下 | 否 |
| VRCVC 安装目录 | **完全不碰** | — |
| 服务 / 驱动 / 计划任务 | **不安装** | — |

没有 `HKLM` 写入、没有 `C:\Program Files` 写入、没有开机自启项。
（VRCVideoCacher 自身的自启动/托盘行为属于上游，与此无关。）

> `DOTNET_STARTUP_HOOKS` 是**全用户共享**的变量：一旦写入，该用户启动的每个支持启动钩子的 .NET 进程
> 都会尝试加载这个 DLL。所以 `HookInstallation.Install(..., registerStartupHook)` 的默认值是 `false`，
> 安装器的 `-RegisterStartupHook`、管理器的「注册启动钩子」都是显式开关。Steam 版不需要它
> （单文件宿主不加载启动钩子），这也是把它做成 opt-in 的主要原因。

### 3.7 网络

CookieBridge 的组件不发起任何外发网络请求：

* `CookieBridge.NativeHost` 只读写本地文件；
* `CookieBridge.Manager` 只读写本地文件与注册表；
* `CookieBridge.Hook` 只读写本地文件 + 命名管道 + 在进程内调用 VRCVC 的公开成员；
* 浏览器扩展只 `chrome.cookies.getAll` + `sendNativeMessage`，没有 `fetch`，没有远程资源
  （`browser-extension/tests/manifest.test.mjs` 会校验这一点）。

不会上传、不会中继、不使用任何公共 Cookie 服务。

---

## 4. 残留风险（明确列出，不掩饰）

| # | 残留风险 | 为什么存在 | 缓解 |
| --- | --- | --- | --- |
| R1 | **同用户进程可解开 DPAPI** | DPAPI 的作用域是用户，不是进程 | 无技术缓解。只能靠「同用户下不跑不可信程序」；这也是所有 Windows 凭据管理工具的共性 |
| R2 | `youtube_cookies.txt` 是明文 | yt-dlp 的 `--cookies` 要求明文 Netscape 文件；VRCVC 用它，我们不能改 | 该文件本来在 VRCVC 启动后就会存在；CookieBridge 只是让它「更早就位」。不要把它拷贝出本机 |
| R3 | `.cookiebridge.bak` 与 `backups\` 是明文 | 原子替换/手动备份的副作用 | 定期删除；卸载时按 §6 清理；不要备份到云同步目录 |
| R4 | 浏览器扩展权限 `cookies` 能读 youtube.com 下**全部** Cookie | 技术上无法只读登录相关项 | 只在需要的浏览器里装；不用的浏览器不装；核对扩展 ID |
| R5 | 假冒扩展 / 被改写的 HKCU 注册项 | HKCU 与扩展 ID 都由用户会话控制 | 核对 `chrome://extensions` 的 ID 与 `extension-id.txt`；核对注册表指向的 manifest 路径在 `%LOCALAPPDATA%` 下 |
| R6 | 日志脱敏是形状匹配，不是语义分析 | 未知形状的第三方错误消息可能带着值 | 报问题时先自查日志里有没有 `\t`；见 §5 的检查命令 |
| R7 | 备份不由本产品加密 | 设计上就是"给用户一份能直接用的副本" | 管理器删除备份即可；或自己加密后另存 |
| R8 | DPAPI 主密钥可被域/管理员级手段恢复 | Windows 平台特性（域漫游配置文件、密码重置代理、离线 SYSTEM） | 非管理员威胁模型外的场景；无法在本产品内缓解 |
| R9 | VRCVC 会在自己的日志里打印 yt-dlp 参数 | `VideoDownloader.cs` 打印 `Arguments`，其中含 `--cookies "<路径>"` | 只泄露**路径**，不含 Cookie 值；路径本身不是秘密，但会暴露用户名 |
| R10 | 进程内模式要求同用户可写 `%LOCALAPPDATA%` 下的 hook DLL | 设计使然 | `install.json` 记录 DLL 的 SHA256，管理器会比对并在不一致时报告「插件 DLL 与安装记录不一致（可能被替换）」 |

---

## 5. 自查：确认没有意外泄露

```powershell
$root = "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge"

# 1) 日志里不该出现 Netscape 行（含 >=3 个制表符）
Get-ChildItem "$root\logs\bridge.log*" | ForEach-Object {
    Select-String -Path $_.FullName -Pattern "`t.*`t.*`t" | ForEach-Object {
        "{0}:{1}: {2}" -f $_.Filename, $_.LineNumber, $_.Line
    }
}
# 期望：没有输出

# 2) 日志里不该出现已知凭据名后面直接跟制表符
Get-ChildItem "$root\logs\bridge.log*" | Select-String -Pattern "(LOGIN_INFO|SIDCC|__Secure-1PSIDTS)`t"
# 期望：没有输出

# 3) vault 不含明文
Select-String -Path "$root\vault.json" -Pattern "LOGIN_INFO" -SimpleMatch
# 期望：没有输出

# 4) 明文副本清单（这些本来就是明文，只是让你知道它们在哪）
Get-Item "$env:APPDATA\VRCVideoCacher\youtube_cookies.txt",
        "$env:APPDATA\VRCVideoCacher\youtube_cookies.txt.cookiebridge.bak" -ErrorAction SilentlyContinue |
    Select-Object FullName, Length, LastWriteTime
Get-ChildItem "$root\backups" -Recurse -Filter youtube_cookies.txt -ErrorAction SilentlyContinue |
    Select-Object FullName, Length, LastWriteTime

# 5) 谁注册了 Native Host（默认值就是 manifest 路径，必须指向你自己的目录）
Get-ItemProperty "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.vrcvideocacher.cookiebridge" -ErrorAction SilentlyContinue
Get-ItemProperty "HKCU:\Software\Mozilla\NativeMessagingHosts\com.vrcvideocacher.cookiebridge" -ErrorAction SilentlyContinue
```

---

## 6. 彻底清理（卸载 + 抹除 + 回滚）

顺序很重要：**先卸载注册，再删文件**，否则浏览器还会去找已经不存在的 host。

### 步骤 1：注销 Native Messaging Host

三种方式任选：

```powershell
# 方式 A：管理器 GUI → Native Host 面板 → 注销
# 方式 B：直接删注册表（当前用户，无需管理员）
$hosts = @(
  "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.vrcvideocacher.cookiebridge",
  "HKCU:\Software\Chromium\NativeMessagingHosts\com.vrcvideocacher.cookiebridge",
  "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.vrcvideocacher.cookiebridge",
  "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.vrcvideocacher.cookiebridge",
  "HKCU:\Software\Vivaldi\NativeMessagingHosts\com.vrcvideocacher.cookiebridge",
  "HKCU:\Software\Mozilla\NativeMessagingHosts\com.vrcvideocacher.cookiebridge"
)
foreach ($p in $hosts) { Remove-Item -Path $p -Recurse -Force -ErrorAction SilentlyContinue }
```

### 步骤 2：清理插件环境变量（只删本插件那一项，不动别人的 hook）

```powershell
$dll = "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\hook\CookieBridge.Hook.dll"
$cur = [Environment]::GetEnvironmentVariable("DOTNET_STARTUP_HOOKS", "User")
if ($cur) {
    $kept = ($cur -split ';' | Where-Object {
        $_ -and ($_.TrimEnd('\') -ine $dll.TrimEnd('\')) -and
        ($_ -notlike "*\VRCVideoCacher-CookieBridge\hook\*")
    }) -join ';'
    if ($kept) { [Environment]::SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", $kept, "User") }
    else       { [Environment]::SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", $null, "User") }
}
# 验证
[Environment]::GetEnvironmentVariable("DOTNET_STARTUP_HOOKS", "User")
```

### 步骤 3：删除 Bridge 目录（vault、hook、host、日志、备份、状态）

```powershell
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge"
```

这会把 `vault.json`、`backups\*`、`logs\*`、`hook\*`、`host\*`、`state\*`、`install.json`、
`disabled.flag` 一起删掉。

### 步骤 4：删除 VRCVC 侧的明文 Cookie 文件

```powershell
Remove-Item -Force "$env:APPDATA\VRCVideoCacher\youtube_cookies.txt" -ErrorAction SilentlyContinue
Remove-Item -Force "$env:APPDATA\VRCVideoCacher\youtube_cookies.txt.cookiebridge.bak" -ErrorAction SilentlyContinue
```

> 这一步只删 Cookie，不影响 VRCVC 的配置、缓存与数据库。想做「让 VRCVC 自己清」的等价操作：
> 打开 VRCVC → 设置里的 Cookie 面板 → 清除 Cookie（效果相同，且会触发 hook 的抑制逻辑）。

### 步骤 5：移除浏览器扩展

* Chrome / Edge / Brave / Vivaldi：`chrome://extensions`（或对应 `edge://extensions`）→ 移除
  `VRCVideoCacher CookieBridge`（ID `fdjmcdfolkgicljalacnlemepfhmnmfp`）；
* Firefox：`about:addons` → 移除，或重启浏览器（临时载入的扩展重启即消失）。

### 步骤 6：回滚到「完全没装过」的状态

| 想回到 | 做法 |
| --- | --- |
| 只用官方扩展（HTTP 9696） | 完成步骤 1–5，然后按上游 README 装官方扩展；VRCVC 行为完全回到原生 |
| 只停用、不删除 | 建一个空文件 `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\disabled.flag`（hook 变被动；host 仍会写目标文件）。要连 host 也停：卸载扩展或注销 Host |
| 连 vault 都想销毁 | 步骤 3 已包含。只想销毁凭据、保留安装：删除 `vault.json` 即可 |

### 检查点：确认清理干净

```powershell
Test-Path "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge"                            # False
Test-Path "$env:APPDATA\VRCVideoCacher\youtube_cookies.txt"                          # False（若你选择删）
[Environment]::GetEnvironmentVariable("DOTNET_STARTUP_HOOKS","User")                 # 不含 CookieBridge
Get-ChildItem "HKCU:\Software\*\NativeMessagingHosts" -ErrorAction SilentlyContinue  # 不含 com.vrcvideocacher.cookiebridge
```
