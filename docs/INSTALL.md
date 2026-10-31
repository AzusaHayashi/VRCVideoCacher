# 安装、验证与卸载

从零到「VRCVC 用上你的 YouTube Cookie」，大约 5 分钟。全程**不需要管理员权限**，
不改 `C:\Program Files` 下的任何东西，不改 `VRCVideoCacher.exe`。

前置条件：

* Windows 10/11 x64；
* 已安装 Steam 版 VRCVideoCacher（`VRCVideoCacher.exe` + `manifest.vrmanifest`）；
* Chrome / Edge / Chromium / Brave / Vivaldi 之一，或 Firefox 138+；
* 你的 VRCVC 配置里 `YtdlpUseCookies = true`（出厂默认 `true`，见
  `VRCVideoCacher/ConfigManager.cs:135`）。这一项为 `false` 时，即使 Cookie 文件写好了
  VRCVC 也不会传给 yt-dlp。

本文所有路径都是完整路径，所有命令都可直接复制执行（PowerShell 5.1 或 7 均可）。

---

## 0. 三种安装方式对照

| 方式 | 适合谁 | 需要什么 | 覆盖范围 |
| --- | --- | --- | --- |
| **A. 管理器 GUI** | 大多数用户 | `CookieBridge.Manager.exe` + `host\` + `hook\` 目录 | Native Host 注册、扩展 ID、插件安装/卸载、备份恢复、状态查看 |
| **B. 一键脚本** | 想用命令行、不想开 GUI | `tools\Install-CookieBridge.ps1` + PowerShell 5.1 | Native Host 注册、文件落位、卸载/清理（`-Uninstall [-Purge]`） |
| **C. 纯手动** | 想完全知道每一处改了什么 | 记事本 + `reg` | 同上，逐条手写 registry 与 JSON |

三种方式的结果完全一致：`%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\host\` 下两个 manifest，
`HKCU` 下 6 个注册表项，可选地装进程内插件。**选一种即可，不要混着重装多次**
（重复注册是幂等的，但混装容易分不清 `path` 指向哪份 exe）。

---

## 1. 第 ① 步：生成 / 确认扩展 ID

扩展 ID 由 `browser-extension/manifest.json` 里的 `key` 决定（RSA 公钥 → SHA256 → 前 16 字节
映射到 `a`..`p`）。本仓库已固定：

| 浏览器 | ID | 原生宿主匹配字段 |
| --- | --- | --- |
| Chrome / Edge / Chromium / Brave / Vivaldi | `fdjmcdfolkgicljalacnlemepfhmnmfp` | `allowed_origins: ["chrome-extension://fdjmcdfolkgicljalacnlemepfhmnmfp/"]` |
| Firefox | `vrexcookiebridge@local` | `allowed_extensions: ["vrexcookiebridge@local"]` |

### 确认（不要跳过）

```powershell
Get-Content "browser-extension\extension-id.txt"
```

应看到 `extension_id=fdjmcdfolkgicljalacnlemepfhmnmfp` 与 `gecko_id=vrexcookiebridge@local`。

### 如果你要换一套身份（会得到新 ID）

```powershell
# 只打印，不改文件
node browser-extension/tools/genkey.mjs

# 写回 manifest.json 的 key 与 extension-id.txt（会改变扩展 ID！）
node browser-extension/tools/genkey.mjs --write
```

换 ID 之后**必须重新注册 Native Host**（`allowed_origins` 要跟着改），否则扩展会收到
`native-host-forbidden`。私钥不落盘、不打印；加载已解压扩展只需要公钥。

---

## 2. 第 ② 步：安装 Native Host

### 需要落位的三个文件

| 从 | 到 | 说明 |
| --- | --- | --- |
| `dist\host\CookieBridge.NativeHost.exe` | `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\host\CookieBridge.NativeHost.exe` | 本地写入程序；被浏览器按需拉起 |
| （由脚本/管理器生成） | `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\host\native-host-chromium.json` | Chromium 系 manifest |
| （由脚本/管理器生成） | `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\host\native-host-firefox.json` | Firefox manifest |

> `CookieBridge.NativeHost.exe` 的路径一旦写进 manifest 就固定了。移动它 = 必须重新注册。
> 发布命令（csproj 里已固定 `PublishSingleFile` + `SelfContained` + `RuntimeIdentifier=win-x64`，
> 所以不用再加参数）：
>
> ```powershell
> dotnet publish bridge/CookieBridge.NativeHost/CookieBridge.NativeHost.csproj -c Release -o dist/host
> # 产物：dist\host\CookieBridge.NativeHost.exe
> ```
>
> 管理器（`CookieBridge.Manager.exe`）是 **WPF + framework-dependent**，需要目标机器装好
> **.NET 10 Desktop Runtime**；从源码树直接跑时要先 `dotnet build`。
> 注意：管理器的自动查找只认 `<管理器目录>\host\CookieBridge.NativeHost.exe`
> （`ManagerServices.FindNativeHostExe()` 里的候选路径写的是 `net10.0`，而项目实际输出在
> `net10.0-windows\win-x64\`），所以**请把 exe 复制到管理器目录的 `host\` 下**，否则面板会显示
> 「找不到 CookieBridge.NativeHost.exe」。

### 注册表位置（当前用户，6 项，全部指向 manifest 的绝对路径）

| 浏览器 | 注册表键 | 值名 | 值 |
| --- | --- | --- | --- |
| Chrome | `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.vrcvideocacher.cookiebridge` | `(默认)` | Chromium manifest 绝对路径 |
| Chromium | `HKCU\Software\Chromium\NativeMessagingHosts\com.vrcvideocacher.cookiebridge` | `(默认)` | 同上 |
| Edge | `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.vrcvideocacher.cookiebridge` | `(默认)` | 同上 |
| Brave | `HKCU\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.vrcvideocacher.cookiebridge` | `(默认)` | 同上 |
| Vivaldi | `HKCU\Software\Vivaldi\NativeMessagingHosts\com.vrcvideocacher.cookiebridge` | `(默认)` | 同上 |
| Firefox | `HKCU\Software\Mozilla\NativeMessagingHosts\com.vrcvideocacher.cookiebridge` | `(默认)` | Firefox manifest 绝对路径 |

### 方式 A：管理器 GUI

1. 把文件放到管理器目录下（和 `CookieBridge.Manager.exe` 同级）：
   * `host\CookieBridge.NativeHost.exe` ← 来自 `dist\host\`（**必需**）
   * `hook\CookieBridge.Hook.dll` ← 来自 `dist\hook\`（部署插件时才需要；Steam 版不用）
2. 运行 `CookieBridge.Manager.exe`（WPF，framework-dependent → 需要 .NET 10 Desktop Runtime）；
3. **Native Host** 面板 → 确认「扩展 ID」已从 `browser-extension\extension-id.txt` 自动读出
   （`fdjmcdfolkgicljalacnlemepfhmnmfp`，Firefox 为 `vrexcookiebridge@local`；
   也可直接粘贴 `chrome-extension://fdjmcdfolkgicljalacnlemepfhmnmfp/`，管理器会自动剥前缀和尾斜杠）；
4. 确认「写入程序路径」已找到 `host\CookieBridge.NativeHost.exe`（找不到时面板会提示：
   `找不到 CookieBridge.NativeHost.exe。请先发布本地写入程序…`）；
5. 点 **注册 Native Host** → 期望提示
   `Native Messaging Host 已注册: Google Chrome, Chromium, Microsoft Edge, Brave, Vivaldi, Firefox`；
6. （可选，自编译 fork）**插件** 面板 → **安装插件**。默认**只部署 DLL、不写环境变量**，期望提示
   `插件已部署到本机。Steam 版因单文件发布不加载启动钩子，Cookie 走契约模式（Native Host 写入 youtube_cookies.txt）。`
   只有显式勾选「注册启动钩子」（`InstallHook(registerStartupHook: true)`）才会写用户级
   `DOTNET_STARTUP_HOOKS`，那时提示变成
   `插件已部署，并已注册到 DOTNET_STARTUP_HOOKS（用户级，仅对支持启动钩子的宿主生效）。`

管理器做的事情等价于方式 B 的脚本，逐项可在日志
`%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\logs\bridge.log` 里核对。

### 方式 B：一键脚本 `tools\Install-CookieBridge.ps1`

仓库里已经提供该脚本（`tools\Install-CookieBridge.ps1`，163 行，纯 PowerShell 5.1+，无外部依赖）。
它做的事和方式 A（管理器 GUI）完全一样，只是走命令行。

```powershell
# 安装（默认扩展 ID 就是本仓库的 ID；部署 host + hook + browser-extension，并注册 6 个 HKCU 项）
powershell -ExecutionPolicy Bypass -File .\tools\Install-CookieBridge.ps1

# 安装 + 同时注册用户级 DOTNET_STARTUP_HOOKS（只有自编译 fork / 框架依赖宿主才需要）
powershell -ExecutionPolicy Bypass -File .\tools\Install-CookieBridge.ps1 -RegisterStartupHook

# 卸载：移除注册 + 环境变量项 + hook 目录，保留 vault 与日志
powershell -ExecutionPolicy Bypass -File .\tools\Install-CookieBridge.ps1 -Uninstall

# 彻底清理：连 vault / 日志 / 备份一起删
powershell -ExecutionPolicy Bypass -File .\tools\Install-CookieBridge.ps1 -Uninstall -Purge
```

参数：

| 参数 | 默认 | 作用 |
| --- | --- | --- |
| `-Source <目录>` | 脚本自身所在目录（`$PSScriptRoot`） | 从哪里取 `host\`、`hook\`、`browser-extension\` |
| `-ChromiumExtensionId` | `fdjmcdfolkgicljalacnlemepfhmnmfp` | 写进 `allowed_origins` |
| `-FirefoxExtensionId` | `vrexcookiebridge@local` | 写进 `allowed_extensions` |
| `-RegisterStartupHook` | 关 | 是否写用户级 `DOTNET_STARTUP_HOOKS`（opt-in，见本节末尾的警告） |
| `-Uninstall` | 关 | 走卸载分支 |
| `-Purge` | 关 | 与 `-Uninstall` 同用时删除整个 `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge` |

它具体做 6 件事（按脚本顺序）：

1. 建 `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\{host,hook}`；
2. 把 `dist\host\*` 复制到 `...\host\`、`dist\hook\CookieBridge.Hook.dll` 复制到 `...\hook\`、
   `dist\browser-extension\` 复制到 `...\browser-extension\`
   （**两者缺一会直接 throw**：`Native host not found at ...` / `Hook plugin not found at ...`
   —— 即便 Steam 版用不到 hook DLL，脚本仍然要求它在 `dist\hook\` 里存在）；
3. 调 `...\host\CookieBridge.NativeHost.exe --register-host --chromium-id <ID> --firefox-id <ID>`
   完成 manifest 生成 + 6 个 HKCU 注册（逻辑在 `NativeHostRegistration.Register`，与管理器一致）；
4. 打印 `[!] DOTNET_STARTUP_HOOKS not touched (not needed for the Steam build — contract mode is used).`
   —— 除非你显式加了 `-RegisterStartupHook`；
5. 调 `...\host\CookieBridge.NativeHost.exe --status` 打印自检结果；
6. 打印下一步提示（扩展目录在 `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\browser-extension`，
   以及 VRCVC 消费的 Cookie 文件路径）。

期望输出（照 `docs/VERIFICATION.md` §2 的实测记录）：

```
[*] Installing CookieBridge into C:\Users\<你>\AppData\Local\VRCVideoCacher-CookieBridge
[+] files deployed
[*] Registering the native-messaging host (HKCU)
[+] registered for chrome-extension://fdjmcdfolkgicljalacnlemepfhmnmfp/ and vrexcookiebridge@local
[!] DOTNET_STARTUP_HOOKS not touched (not needed for the Steam build — contract mode is used).

[*] Verification
bridge root      : C:\Users\<你>\AppData\Local\VRCVideoCacher-CookieBridge
vault            : vault-missing (0 cookies, login=False, updated=)
cookie file      : C:\Users\<你>\AppData\Roaming\VRCVideoCacher\youtube_cookies.txt
  exists=False valid=False cookies=0 written=
vrcvc            : version=2026.10.29 (已验证版本), running=False
in-process hook  : not loaded
native host reg  : Google Chrome, Chromium, Microsoft Edge, Brave, Vivaldi, Firefox
[+] Install complete.
```

> 首次安装时 `vault : vault-missing`、`exists=False` 是正常的 —— 那时还没有浏览器推送过 Cookie。
> 第 ④ 步验证之后再看这个 `--status`，应当变成 `vault: ok (N cookies, login=True, ...)`。

### 附：NativeHost 的命令行（排障用）

`CookieBridge.NativeHost.exe` 是**一个二进制两种模式**：不带参数 → Native Messaging（浏览器拉起）；
带参数 → CLI。CLI 走的是和浏览器完全相同的处理函数，所以很适合手工验证。

```powershell
$hostExe = "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\host\CookieBridge.NativeHost.exe"

& $hostExe --status          # 自检：vault / 目标文件 / VRCVC 版本与兼容等级 / hook 是否在线 / 已注册浏览器
& $hostExe --version         # 版本号
& $hostExe --register-host   # 重新注册（默认用内置扩展 ID）
& $hostExe --unregister-host # 注销全部 6 个 HKCU 注册项

# 把一份 Netscape 文件当成浏览器推送进来（走 NativeMessageHandler 的同一条路径）
& $hostExe --send "C:\path\to\youtube_cookies.txt"
# 或从管道读
Get-Content "C:\path\to\youtube_cookies.txt" -Raw | & $hostExe --send -
```

`--status` 的退出码：`0` = vault 可读，`3` = vault 缺失/不可读，`1` = 其它失败。
`--send` 的退出码：`0` = `ok:true`，`1` = 被拒（并把 `{ok:false,error:...}` 打到 stdout）。

> **`-RegisterStartupHook` / 管理器「注册启动钩子」默认关闭是刻意的**：Steam 版单文件宿主
> 不加载托管启动钩子（见 [`ARCHITECTURE.md`](./ARCHITECTURE.md) 第 9 节），而用户级
> `DOTNET_STARTUP_HOOKS` 是**全用户共享**的 —— 写进去意味着你机器上每个支持启动钩子的 .NET 进程
> 启动时都会尝试加载这个 DLL。只在自编译 fork / 框架依赖宿主上才打开它。

### 方式 C：纯手动（每一步的确切内容）

```powershell
# C-1 建目录
$root = "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge"
New-Item -ItemType Directory -Force -Path "$root\host","$root\hook","$root\logs","$root\state" | Out-Null

# C-2 放写入程序
Copy-Item -Force ".\dist\host\CookieBridge.NativeHost.exe" "$root\host\CookieBridge.NativeHost.exe"

# C-3 写 Chromium manifest（注意：路径里的 \ 在 JSON 里要写成 \\）
$chromiumManifest = "$root\host\native-host-chromium.json"
@"
{
  "name": "com.vrcvideocacher.cookiebridge",
  "description": "VRCVideoCacher CookieBridge local cookie writer",
  "path": "$($root -replace '\\','\\')\\host\\CookieBridge.NativeHost.exe",
  "type": "stdio",
  "allowed_origins": ["chrome-extension://fdjmcdfolkgicljalacnlemepfhmnmfp/"]
}
"@ | Set-Content -Path $chromiumManifest -Encoding UTF8

# C-4 写 Firefox manifest
$firefoxManifest = "$root\host\native-host-firefox.json"
@"
{
  "name": "com.vrcvideocacher.cookiebridge",
  "description": "VRCVideoCacher CookieBridge local cookie writer",
  "path": "$($root -replace '\\','\\')\\host\\CookieBridge.NativeHost.exe",
  "type": "stdio",
  "allowed_extensions": ["vrexcookiebridge@local"]
}
"@ | Set-Content -Path $firefoxManifest -Encoding UTF8

# C-5 注册 6 个键（reg.exe，/ve = 默认值；/d 必须是绝对路径）
$chromiumManifest = "$root\host\native-host-chromium.json"
$firefoxManifest  = "$root\host\native-host-firefox.json"

$keys = @(
  'Software\Google\Chrome\NativeMessagingHosts\com.vrcvideocacher.cookiebridge',
  'Software\Chromium\NativeMessagingHosts\com.vrcvideocacher.cookiebridge',
  'Software\Microsoft\Edge\NativeMessagingHosts\com.vrcvideocacher.cookiebridge',
  'Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.vrcvideocacher.cookiebridge',
  'Software\Vivaldi\NativeMessagingHosts\com.vrcvideocacher.cookiebridge'
)
foreach ($k in $keys) {
    reg add "HKCU\$k" /ve /t REG_SZ /d "$chromiumManifest" /f
}
reg add "HKCU\Software\Mozilla\NativeMessagingHosts\com.vrcvideocacher.cookiebridge" /ve /t REG_SZ /d "$firefoxManifest" /f
```

> **路径必须写绝对路径。** 浏览器按注册表默认值去 `CreateProcess` 那个字符串，**不会**替你展开
> `%LOCALAPPDATA%` 之类的环境变量。若你更习惯 cmd 的写法，也请在 PowerShell 里用 `$env:` 拼出
> 完整路径（如上），而不要写 `reg add ... /d "%LOCALAPPDATA%\..."` —— 那会把字面量
> `%LOCALAPPDATA%` 写进注册表，浏览器随后报 `native-host-not-found`。

### manifest 的确切形状（照抄即可）

`%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\host\native-host-chromium.json`：

```json
{
  "name": "com.vrcvideocacher.cookiebridge",
  "description": "VRCVideoCacher CookieBridge local cookie writer",
  "path": "C:\\Users\\<你的用户名>\\AppData\\Local\\VRCVideoCacher-CookieBridge\\host\\CookieBridge.NativeHost.exe",
  "type": "stdio",
  "allowed_origins": ["chrome-extension://fdjmcdfolkgicljalacnlemepfhmnmfp/"]
}
```

`%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\host\native-host-firefox.json`：

```json
{
  "name": "com.vrcvideocacher.cookiebridge",
  "description": "VRCVideoCacher CookieBridge local cookie writer",
  "path": "C:\\Users\\<你的用户名>\\AppData\\Local\\VRCVideoCacher-CookieBridge\\host\\CookieBridge.NativeHost.exe",
  "type": "stdio",
  "allowed_extensions": ["vrexcookiebridge@local"]
}
```

两条硬规则：

1. **Chromium 与 Firefox 是两份文件**，不能把 `allowed_origins` 和 `allowed_extensions` 混在一份里
   （Chrome 会因未知字段/缺失字段拒绝；Firefox 会忽略 `allowed_origins`）。这是
   `NativeHostRegistration.Register()` 的实际行为。
2. `allowed_origins` 里的 ID **必须带尾部 `/`**，写成 `chrome-extension://<id>/`。

### 可选：装进程内插件（只有自编译 fork 需要）

Steam 版的单文件宿主不加载托管启动钩子（实测见 [`ARCHITECTURE.md`](./ARCHITECTURE.md) 第 9 节
与 [`VERIFICATION.md`](./VERIFICATION.md)），所以在 Steam 版上装它**没有效果**。

> **默认不要给 Steam 版装。** 除了无效之外，用户级 `DOTNET_STARTUP_HOOKS` 是**全用户共享**的：
> 一旦写进去，你机器上**每一个**支持启动钩子的 .NET 程序启动时都会尝试加载这个 DLL（本 DLL 会自己
> 判断宿主并保持被动，但这是无谓的风险面）。所以 `HookInstallation.Install(..., registerStartupHook)`
> 的默认值是 `false`，脚本对应 `-RegisterStartupHook`，管理器对应「注册启动钩子」勾选框。
> 只想在 Steam 版上用 CookieBridge → **只做第 ② 步，跳过这一节**。

自编译 fork 的装法（可选）——用脚本最省事：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Install-CookieBridge.ps1 -RegisterStartupHook
```

手工等价操作：

```powershell
$root = "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge"
New-Item -ItemType Directory -Force -Path "$root\hook" | Out-Null
Copy-Item -Force ".\dist\hook\CookieBridge.Hook.dll" "$root\hook\CookieBridge.Hook.dll"

# 追加到用户环境变量，绝不覆盖别人已有的 hook
$dll = "$root\hook\CookieBridge.Hook.dll"
$cur = [Environment]::GetEnvironmentVariable('DOTNET_STARTUP_HOOKS','User')
$list = @()
if ($cur) { $list = $cur -split ';' | Where-Object { $_ -and ($_.TrimEnd('\') -ine $dll.TrimEnd('\')) } }
$new = (@($list) + $dll) -join ';'
[Environment]::SetEnvironmentVariable('DOTNET_STARTUP_HOOKS', $new, 'User')
[Environment]::GetEnvironmentVariable('DOTNET_STARTUP_HOOKS','User')
```

> 环境变量改动对**已运行**的进程无效；`explorer.exe` 需要收到 `WM_SETTINGCHANGE` 才会刷新用户
> 环境块。`HookInstallation.Install(..., registerStartupHook: true)` 会自己广播这条消息；手动安装时，
> 注销重登一次最稳。对 Steam 版来说这一项本来就不生效，可以完全跳过。

---

## 3. 第 ③ 步：加载浏览器扩展

### Chrome / Edge / Chromium / Brave / Vivaldi

1. 打开 `chrome://extensions`（Edge 为 `edge://extensions`）；
2. 打开右上角 **开发者模式**；
3. **加载已解压的扩展程序** → 选择扩展目录。三处都行，内容相同：
   * 仓库里的 `browser-extension\`
   * 仓库里的 `dist\browser-extension\`
   * `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\browser-extension\`（安装脚本会复制一份到这里，
     好处是卸载时一起清掉）
4. 列表里显示的 ID 必须是 `fdjmcdfolkgicljalacnlemepfhmnmfp`。不是 → `manifest.json` 的 `key`
   被改过（或目录里混进了别的清单），回到第 ① 步；
5. 点扩展图标 → 「状态页」能打开即正常。

### Firefox 138+

临时载入（重启浏览器后失效，日常够用）：

1. `about:debugging#/runtime/this-firefox`；
2. **临时载入附加组件** → 选 `browser-extension\manifest.json`；
3. Firefox 只看 `browser_specific_settings.gecko.id` = `vrexcookiebridge@local`，
   与 Chrome 的 `key` 无关。

永久安装需要签名（这是 Mozilla 的策略，与扩展本身无关）：

```powershell
Compress-Archive -Path browser-extension\* -DestinationPath cookiebridge-extension.zip
# zip 内必须以 manifest.json 为根，不能多一层文件夹
```

---

## 4. 第 ④ 步：验证（四道检查，逐条过）

### 4.1 访问 YouTube，扩展徽章变绿

1. 确保浏览器里**已登录** YouTube（不是无痕，不是容器标签 —— Firefox 容器标签请在该容器内
   打开一次 YouTube）；
2. 打开 `https://www.youtube.com/`；
3. 等约 5 秒，看扩展徽章：

| 徽章 | 含义 |
| --- | --- |
| `OK`（绿） | 宿主返回 `ok:true` 且 `targetWritten:true`（Cookie 文件已写入） |
| `!`（黄） | 宿主收下了（vault 已更新），但没写 Cookie 文件。进程内模式和「yt-dlp 正在跑」时会这样 |
| `ERR`（红） | 失败；点图标看状态页的错误码 |

徽章 60 秒后自动清除，重复同步只重置计时器。

错误码对照：

| 错误码 | 处理 |
| --- | --- |
| `native-host-not-found` | 注册表项不存在，或 manifest 路径指向不存在的文件 → 重做第 ② 步 |
| `native-host-forbidden` | `allowed_origins` 与实际扩展 ID 不一致（注意尾部 `/`） |
| `native-host-disconnected` | 宿主进程启动即崩 → 看 `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\logs\bridge.log` |
| `native-timeout` | 宿主 20 秒未响应（锁竞争/磁盘慢） |
| `invalid-cookie-format` | 抓到的 jar 不含 `youtube.com` + `LOGIN_INFO`（通常=没登录） |

### 4.2 vault 存在并且是密文

```powershell
$vault = "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\vault.json"
Test-Path $vault                                     # True
(Get-Content $vault -Raw | ConvertFrom-Json) |
    Select-Object formatVersion, protection, payloadSha256,
                  @{n='cookieCount';e={$_.metadata.cookieCount}},
                  @{n='login';e={$_.metadata.loginCookiePresent}},
                  @{n='updatedUtc';e={$_.updatedUtc}}
Select-String -Path $vault -Pattern 'LOGIN_INFO' -SimpleMatch   # 期望：无输出
```

期望：`formatVersion=1`、`protection=dpapi-current-user`、`cookieCount` > 0、`login=True`、
`updatedUtc` 是刚才。

### 4.3 检查 Cookie 文件的条数

```powershell
$t = "$env:APPDATA\VRCVideoCacher\youtube_cookies.txt"
Test-Path $t                                                    # True
(Get-Item $t).Length                                            # 字节数，通常几 KB
(Get-Content $t | Where-Object { $_ -and $_ -notlike '#*' -and ($_ -split "`t").Count -eq 7 }).Count   # 条数
(Select-String -Path $t -Pattern 'LOGIN_INFO' -SimpleMatch).Count                                      # 期望 1
Get-Item $t | Select-Object LastWriteTime
```

只数条数、只看是否存在 `LOGIN_INFO` —— **不要把内容贴到任何地方**。

进程内模式（黄徽章）时，如果 hook 还没跑，这个文件可能还是旧的或不存在；
启动 VRCVC 后再看一次。

### 4.4 日志

```powershell
Get-Content "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\logs\bridge.log" -Tail 30
```

一次成功同步应包含类似行（`source` 在第 2 列，时间戳是 UTC）：

```
2026-11-01T00:30:12.345Z [INFO ] host        Native messaging host started.
2026-11-01T00:30:12.456Z [INFO ] vault       Saved 37 cookies (login=True, sha=6f1c2a9b3d4e) from chrome.
2026-11-01T00:30:12.470Z [INFO ] target      Wrote VRCVC cookie file (37 cookies, login=True).
2026-11-01T00:30:12.480Z [INFO ] host        Native messaging host finished.
```

日志里**不会**出现任何 Cookie 名-值对（自查命令见 `docs/SECURITY.md` §5）。

---

## 5. 第 ⑤ 步：启动 VRCVC 播放 YouTube 视频

1. 从 Steam 启动 VRCVideoCacher；
2. 打开 VRChat，进一个有视频播放器的世界，放一个 YouTube 链接；
3. 期望：不再出现 `Sign in to confirm you're not a bot`。

可选的直接证据：VRCVC 自己的日志里会打印传给 yt-dlp 的完整参数
（`VRCVideoCacher/YTDL/VideoDownloader.cs:213`）：

```powershell
# 在最新的 VRCVC 日志里找 --cookies
Get-ChildItem "$env:APPDATA\VRCVideoCacher\Logs\VRCVideoCacher*.log" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
    ForEach-Object { Select-String -Path $_.FullName -Pattern '--cookies' | Select-Object -Last 3 }
```

看到 `--cookies "C:\Users\<你>\AppData\Roaming\VRCVideoCacher\youtube_cookies.txt"` 就说明
VRCVC 确实在读它。**没有**这一行 → 检查 `Config.json` 里 `YtdlpUseCookies` 是否为 `true`，
以及 Cookie 文件是否同时含 `youtube.com` 与 `LOGIN_INFO`。

> 若你装的是自编译 fork：VRCVC 启动时 `Program.Main` 第一行就调
> `CookieBridgePlugin.TryLoad()`，随后 hook 会做一次 `SyncOnce("startup")`。检查
> `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\state\hook-status.json` 里的 `layers`。

---

## 6. 卸载与回滚

### 首选：跑脚本（一条命令）

```powershell
# 移除注册 + 环境变量项 + hook 目录，保留 vault 与日志
powershell -ExecutionPolicy Bypass -File .\tools\Install-CookieBridge.ps1 -Uninstall

# 连 vault / 日志 / 备份一起删（真正"抹干净"）
powershell -ExecutionPolicy Bypass -File .\tools\Install-CookieBridge.ps1 -Uninstall -Purge
```

`-Uninstall` 会：优先调 `CookieBridge.NativeHost.exe --unregister-host`；宿主 exe 已不在时直接删 6 个
HKCU 键；从 `DOTNET_STARTUP_HOOKS` 里**只**摘掉以 `CookieBridge.Hook.dll` 结尾的项（别人的 hook 保留）；
删 `...\hook\` 与 `install.json`；最后按 `-Purge` 决定是否删整个根目录。

它**不动** VRCVC 侧的 `youtube_cookies.txt`（那是 VRCVC 自己的文件），要删见下面的步骤 4。

### 等价的手动步骤

完整步骤（含逐条检查命令）见 [`docs/SECURITY.md` 第 6 节](./SECURITY.md#6-彻底清理卸载--抹除--回滚)。
摘要：

```powershell
# 1) 注销 Native Host
$hostExe = "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\host\CookieBridge.NativeHost.exe"
if (Test-Path $hostExe) { & $hostExe --unregister-host }
$hosts = @(
  "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.vrcvideocacher.cookiebridge",
  "HKCU:\Software\Chromium\NativeMessagingHosts\com.vrcvideocacher.cookiebridge",
  "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.vrcvideocacher.cookiebridge",
  "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.vrcvideocacher.cookiebridge",
  "HKCU:\Software\Vivaldi\NativeMessagingHosts\com.vrcvideocacher.cookiebridge",
  "HKCU:\Software\Mozilla\NativeMessagingHosts\com.vrcvideocacher.cookiebridge")
foreach ($p in $hosts) { Remove-Item -Path $p -Recurse -Force -ErrorAction SilentlyContinue }

# 2) 移除插件环境变量项（不动别人的 hook）
$cur = [Environment]::GetEnvironmentVariable('DOTNET_STARTUP_HOOKS','User')
if ($cur) {
    $kept = ($cur -split ';' | Where-Object { $_ -and ($_ -notlike '*VRCVideoCacher-CookieBridge\hook\*') }) -join ';'
    if ($kept) { [Environment]::SetEnvironmentVariable('DOTNET_STARTUP_HOOKS', $kept, 'User') }
    else       { [Environment]::SetEnvironmentVariable('DOTNET_STARTUP_HOOKS', $null, 'User') }
}

# 3) 删除 Bridge 目录（vault、hook、host、日志、备份、状态、browser-extension 副本）
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge"

# 4) 删除 VRCVC 侧明文 Cookie
Remove-Item -Force "$env:APPDATA\VRCVideoCacher\youtube_cookies.txt" -ErrorAction SilentlyContinue
Remove-Item -Force "$env:APPDATA\VRCVideoCacher\youtube_cookies.txt.cookiebridge.bak" -ErrorAction SilentlyContinue

# 5) 浏览器里移除扩展（chrome://extensions 或 about:addons）
```

### 回滚到「没装过」的状态

```powershell
Test-Path "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge"                  # False
Test-Path "$env:APPDATA\VRCVideoCacher\youtube_cookies.txt"                # False（若你选择删）
[Environment]::GetEnvironmentVariable('DOTNET_STARTUP_HOOKS','User')       # 不含 CookieBridge
reg query "HKCU\Software\Google\Chrome\NativeMessagingHosts\com.vrcvideocacher.cookiebridge" 2>$null  # 报错=已清掉
```

### 只停用，不删除

| 想停用什么 | 做法 | 效果 |
| --- | --- | --- |
| 停用进程内插件 | 新建空文件 `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\disabled.flag` | hook 变被动（`disabled.flag present — plugin stays passive.`）；host 仍会写目标文件 |
| 停用整条链路 | 移除/禁用浏览器扩展 | 不再有新的 vault 更新；已有的 Cookie 文件保持不动（VRCVC 继续用它） |
| 暂停一小会儿 | 管理器里点「禁用插件」 | 只写/删 `disabled.flag`，其它状态不变 |

### 恢复到旧版本（CookieBridge 自身降级）

管理器 → **备份** 面板 → 选一个 `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge\backups\<时间戳>\`
→ 恢复。它会用 `CookieTargetWriter.Write`（原子写 + 互斥量）还原 Cookie 文件，并重装备份里的
hook DLL 与 `install.json`。

---

## 7. 常见安装错误

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 脚本报 `Native host not found at ...\host\CookieBridge.NativeHost.exe` | `dist\host\` 里没有 exe（还没 publish） | `dotnet publish bridge/CookieBridge.NativeHost/CookieBridge.NativeHost.csproj -c Release -o dist/host` |
| 脚本报 `Hook plugin not found at ...\hook\CookieBridge.Hook.dll` | 即使 Steam 版用不到 hook，脚本仍要求它存在 | `dotnet build bridge/CookieBridge.Hook/CookieBridge.Hook.csproj -c Release` 后把 DLL 放进 `dist\hook\` |
| 管理器「写入程序路径」为空 | 管理器目录下没有 `host\CookieBridge.NativeHost.exe` | 从 `dist\host\` 复制到管理器目录的 `host\` 下（管理器自动查找只认这一处，见第 ② 步的注） |
| 管理器打不开 / 闪退 | 缺 .NET 10 **Desktop** Runtime（管理器是 framework-dependent 的 WPF） | 装 .NET 10 Desktop Runtime，或自己 publish 成 self-contained |
| 注册后浏览器仍报 `native-host-not-found` | 注册表写的是含 `%LOCALAPPDATA%` 的字面量，或 manifest 里的 `path` 不存在 | 用 `--register-host` 重新注册；再 `Get-ItemProperty` 核对默认值 |
| `native-host-forbidden` | 扩展 ID 与 `allowed_origins` 不一致（少了尾部 `/` 也会） | 核对 `extension-id.txt` |
| 徽章黄 `!` 一直不变绿 | 进程内模式，或 VRCVC 正在跑 yt-dlp（host 等了 10 秒仍忙） | 等播放结束再看；或直接看目标文件 `LastWriteTime` |
| `invalid-cookie-format` | 未登录 YouTube，或只登录了无痕/容器 | 在普通窗口登录后打开一次 YouTube |
| Cookie 条数为 0 | 同上；或 cookie 全被 `ST-` 过滤（Firefox 罕见情况） | 换 Firefox 普通窗口 / Chrome 再试 |
| 目标文件一直不出现 | `%APPDATA%\VRCVideoCacher` 权限异常，或目标被占用 | 看 `bridge.log` 里 `target` 行的错误类型；跑一次 `CookieBridge.NativeHost.exe --status` |
| 一切看起来正常但播放仍被 bot 检查拦 | VRCVC 用了一份过期 Cookie，或 `YtdlpUseCookies=false` | 见 [`FAQ.md`](./FAQ.md) 第 2 题与第 8 题 |
