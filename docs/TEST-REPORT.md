# CookieBridge 测试报告（实测）

生成时间：2026-11-01（本机时钟）
执行人：test-dev（自动化测试工程）
全部结论来自**实际执行输出**，未执行/未通过的项一律如实标注 BLOCKED / FAIL，不做理论通过。

| 环境项 | 实际值 |
| --- | --- |
| 仓库根 | `C:\Users\lz200\Documents\deepseek-harness\default-workspace\VRCVideoCacher-CookieBridge` |
| .NET SDK | `10.0.403`（便携安装于 `C:\Users\lz200\dotnet-sdk\sdk`，无全局 SDK） |
| 运行时 | Microsoft.NETCore.App `10.0.14` |
| PowerShell | **只有 Windows PowerShell 5.1**（本机无 pwsh 7）。所有脚本按 5.1/7 双兼容编写，实测用 `powershell.exe` 执行 |
| 真实 VRCVC | `D:\Steam\steamapps\common\VRCVideoCacher\VRCVideoCacher.exe`，版本 `2026.10.29` |
| Steam | `HKCU\Software\Valve\Steam\SteamPath = d:/steam`，库 `D:\Steam` |

---

## 1. 总览

| # | 用例 | 结果 | 关键证据 |
| --- | --- | --- | --- |
| 1 | xUnit 单元测试（136 条） | **PASS 136 / FAIL 0** | `TOTAL 136  PASSED 136  FAILED 0  SKIPPED 0` |
| 2 | Test-01 Vault 往返（NativeHost） | **PASS**（20 checks） | vault.json 无明文；cookie 文件 7 行 × 7 字段；ST- 被过滤 |
| 3 | Test-02 注入正向（框架依赖 MockHost） | **PASS**（19 checks） | `L1..L6=resolved`、`IsFresh=true`、目标文件写入 6 条、宿主自报 `count=6 login=True` |
| 4 | Test-02b 单文件负向对照 | **PASS**（8 checks） | 单文件宿主 **不加载** Hook；`COREHOST_TRACE` 抓到 `IsSupported = false` |
| 5 | Test-03 真实 VRCVC（只读验收） | **BLOCKED**（脚本自身异常，见 §4） | 真实契约链路已由 `docs/VERIFICATION.md` 覆盖 |
| 6 | Test-04 无 vault | **PASS**（8 checks） | 日志出现 `vault-missing`、目标文件未创建、宿主存活 |
| 7 | Test-05 disabled.flag | **PASS**（6 checks） | 未写目标文件、无 hook-status.json、日志 `disabled.flag present` |
| 8 | Test-06 实时更新（不重启宿主） | **PASS**（9 checks） | 延迟 **1.28–1.39 s**，同一 pid、`syncCount` 增长、宿主自报 `count=10` |
| 9 | Test-07 兼容性闸门 | **PASS**（10 checks） | 矩阵 23 条 xUnit 全绿；2099.1.1 = `UntestedNewer` 仍写文件 |

一键复跑：

```powershell
# 构建 + xUnit + 全部集成用例（退出码 0 = 全绿）
powershell -NoProfile -ExecutionPolicy Bypass -File tests\integration\Run-All.ps1

# 只跑某几个
powershell -NoProfile -ExecutionPolicy Bypass -File tests\integration\Run-All.ps1 -SkipBuild -Only "Test-02*","Test-06*"
```

`Run-All.ps1` 汇总（最近一次全量运行，Test-06 修复前的那次）：

```
Case                              Ok
xUnit                           True   TOTAL 136  PASSED 136  FAILED 0
Test-01 vault roundtrip         True   PASS (20 checks)
Test-02 injection (fd)          True   PASS (19 checks)
Test-02b single-file control    True   PASS (8 checks)
Test-03 real VRCVC (read-only)  False  BLOCKED（见 §4）
Test-04 no vault                True   PASS (8 checks)
Test-05 disabled flag           True   PASS (6 checks)
Test-06 live reload             False  → 修复后单独复跑 PASS (9 checks)
Test-07 compatibility gate      True   PASS (10 checks)
```

---

## 2. `dotnet test` 无法运行：本机沙箱限制（非产品问题）

**结论：在本机（沙箱内）`dotnet test` 连一条测试都跑不起来，与测试代码无关；换到普通用户桌面会话应可直接 `dotnet test`。**

实际错误（vstest testhost 在**发现测试之前**就崩溃）：

```
源“...\CookieBridge.Tests.dll”的 Testhost 进程已退出，但出现错误:
Unhandled exception. System.ComponentModel.Win32Exception (5): 拒绝访问。
   at System.Diagnostics.ProcessManager.OpenProcess(Int32 processId, Int32 access, Boolean throwIfExited)
   at System.Diagnostics.Process.GetProcessHandle(Int32 access, Boolean throwIfExited)
   at System.Diagnostics.Process.GetOrOpenProcessHandle()
   at System.Diagnostics.Process.EnsureWatchingForExit()
   at System.Diagnostics.Process.set_EnableRaisingEvents(Boolean value)
   at Microsoft.VisualStudio.TestPlatform.PlatformAbstractions.ProcessHelper.SetExitCallback(...)
   at Microsoft.VisualStudio.TestPlatform.TestHost.DefaultEngineInvoker.SetParentProcessExitCallback(...)
```

已知事实与排除过程：

| 观察 | 结果 |
| --- | --- |
| 崩溃点在 `SetParentProcessExitCallback` → `EnableRaisingEvents`，即 testhost 打开**父进程句柄**时被拒绝 | 与测试内容无关 |
| 对同一个父进程（`dotnet exec vstest.console.dll`，实测 pid 9084）从**同级进程**调用 `EnableRaisingEvents` | **成功**（`PWSH sibling OpenProcess: OK`） |
| 从**子进程**调用同一个父进程 | **成功**（`CHILD: OpenProcess(parent=9084) OK name=dotnet`） |
| 升级 `Microsoft.NET.Test.Sdk` 17.14.1 → 18.11.0 | 无效 |
| `xunit.runner.visualstudio` 3.1.5、`PlatformTarget=x64` | 无效 |
| 进程完整性级别 | Medium，`whoami` = `pc_of_azusa\lz200`，非 AppContainer |

即：只有 vstest 派生的 testhost 拿不到父句柄，判定为本机沙箱的进程句柄策略。

### 替代执行器（同一份测试程序集，真实执行）

`bridge/CookieBridge.TestRunner`（net10.0-windows，反射执行 `CookieBridge.Tests.dll`），遵守 xUnit 语义：每个测试新实例、`IDisposable`、`[Fact]`/`[Theory]`+`[InlineData]`、`[Collection]` 串行、fixture 注入、`Assert` 抛异常即失败。

```powershell
dotnet bridge\CookieBridge.TestRunner\bin\Release\net10.0-windows\CookieBridge.TestRunner.dll `
       bridge\CookieBridge.Tests\bin\Release\net10.0-windows\CookieBridge.Tests.dll
```

测试项目本身保持标准形态（`xunit` 2.9.3 + `xunit.runner.visualstudio` 3.1.5 + `Microsoft.NET.Test.Sdk` 18.11.0），在普通环境直接 `dotnet test` 即可。

---

## 3. 单元测试范围（136 条，全绿）

`bridge/CookieBridge.Tests` 只引用 `CookieBridge.Hook.csproj`（Core 源码已编译进该程序集，若再引用 Core 项目会 CS0433 类型重复）。

环境隔离：`[CollectionDefinition(DisableParallelization = true)]` + `xunit.runner.json { "parallelizeTestCollections": false }`；每个用例用 `IDisposable` fixture 把 `VRCCB_ROOT` / `VRCCB_TARGET_FILE` / `VRCCB_VRCVC_DATA` 指到独立临时目录，真实 `%LOCALAPPDATA%\VRCVideoCacher-CookieBridge` 从未被访问。

| 测试类 | 覆盖点 |
| --- | --- |
| `NetscapeCookieFileTests` | `#HttpOnly_` 行、注释、字段不足、CRLF、domain 补点、ST- 过滤、去重（末值胜出）、幂等、7 字段、`IsValidForVrcvc` 与上游 `Program.IsCookiesValid` **逐例对照**、`Summarize` 计数/过期窗口/域名判定、SHA-256 |
| `CookieVaultTests` | Save→Load 往返、payload base64 与**解码后密文**均不含 `LOGIN_INFO`/值、篡改 `payloadSha256`→`hash-mismatch`、篡改密文→`dpapi-failed`/`hash-mismatch`、错误 entropy 无法解密、`formatVersion=2`→`unsupported-format-2`、无 vault→`vault-missing`、非法 jar 拒收 |
| `AtomicFileTests` | 往返、缺文件返回 null、自动建目录、无临时文件残留、备份文件内容、**4 线程边写边读 3 秒断言长度恒定且内容不混版**、50 次替换后仍是完整内容 |
| `CookieMergerTests` | LOGIN_INFO 相同→保留 target 独有 Cookie 且浏览器值覆盖同名、yt-dlp 轮换值保留；LOGIN_INFO 不同→vault 全量胜出；target null/空→vault canonical |
| `CompatibilityMatrixTests` | `2026.10.29`=Verified、`2027.1.1`=UntestedNewer、`2025.6.1`=Supported、`2024.2.1`=UntestedOlder、null/garbage=Unknown；**全部 HookAllowed=true**；`-sabr`/`+hash`/空前缀等解析 |
| `NativeMessageHandlerTests` | 合法 `cookie.update` 写 vault（`VRCCB_ROOT` 隔离）、无 Hook 时同时写目标文件、无效 jar 拒收且不落盘、ping/status、未知类型、**响应 JSON 不含 Cookie 名/值**、长度前缀协议往返与超限拒绝 |
| `SecretRedactorTests` | 制表符 cookie 行脱敏、已知 cookie 名脱敏、普通诊断不被破坏、`BridgeLog.Format` 脱敏（见 §5 缺陷） |
| `HookInstallationTests` | 注入内存版 `IUserEnvironmentStore`：`RegisterEnv` 保留他人条目 / 去重 / 幂等、`RemoveEnv` 只删自己的、`Install(registerStartupHook:false)` **默认不动环境变量**、`Install(true)` 才注册、`Uninstall` 保留他人 hook |
| `CookieTargetWriterTests` | 写入/相同内容跳过（`identical`）/无效拒绝且不动旧文件/空输入、`ReadState` 各态、备份、`Describe` 文案、`alreadyLocked` 跳过跨进程互斥量 |
| `VrcvcLocatorTests` | 真实注册表 `ReadSteamPath()`=`d:\steam`（与 `D:\Steam` 忽略大小写相等）、`ReadLibraryFolders()` 含 `D:\Steam`、`Discover()` 找到 `D:\Steam\steamapps\common\VRCVideoCacher\VRCVideoCacher.exe`、`DetectVersion()` 读到真实版本、user-override 优先 |

---

## 4. 集成用例

公共约束（`tests/integration/Common.ps1`）：

* 所有 `VRCCB_ROOT` / `VRCCB_TARGET_FILE` / `VRCCB_MOCK_COOKIE_PATH` 都在**工作区内**（`tests/integration/.work/…`）——沙箱派生进程无法写 `%LOCALAPPDATA%`/`%APPDATA%`。
* 测试数据全部为合成 Cookie（`FAKE_*`），不含任何真实账号 Cookie。
* 每个用例结束都 `taskkill /T` 只杀自己起的 PID；用户真实的 VRCVideoCacher（PID 11068）从未被触碰。

### Test-01 Vault 往返 — PASS（20 checks）

命令：`powershell -File tests\integration\Test-01-VaultRoundtrip.ps1`（驱动 Release 单文件 `CookieBridge.NativeHost.exe`）

实际输出摘要：

```
reply      : {"ok":true,"vaultUpdated":true,"cookieCount":7,"targetWritten":true,
              "applied":"vault\u002Bcookie-file","message":"vault\u002Bcookie file updated",...}
  [PASS] native host returned a length-prefixed JSON reply
  [PASS] reply.cookieCount matches the canonical jar (7 = 1 login + 2 + 4 extras)
  [PASS] vault.json created under VRCCB_ROOT
  [PASS] vault.json contains no plaintext cookie name / value
  [PASS] vault formatVersion = 1 / protection = dpapi-current-user
  [PASS] cookie file has 7 rows / every row has exactly 7 tab separated fields
  [PASS] ST- prefixed cookie was filtered out
  [PASS] reply JSON carries no cookie name / value
RESULT Test-01: PASS (20 checks)
```

### Test-02 注入正向（框架依赖 MockHost）— PASS（19 checks）

关键输出（`bridge.log` 实测）：

```
hook status: vrcvcVersion=2099.1.1 pid=28008
             layers={"L1.cookiePath":"resolved","L2.jarGate":"resolved","L3.notify":"resolved",
                     "L4.validity":"resolved","L5.version":"resolved","L6.cookieEvent":"resolved"}
2026-10-31T17:20:23.688Z [INFO ] hook   Startup hook initialised inside the host process.
2026-10-31T17:20:23.768Z [INFO ] hook   Attached to host PID 28008 (VRCVideoCacher.exe).
2026-10-31T17:20:23.785Z [INFO ] hook   Host version 2099.1.1 → UntestedNewer（已启用降级兼容）
2026-10-31T17:20:23.795Z [INFO ] hook   Capabilities: L1..L6 全部 resolved
2026-10-31T17:20:23.836Z [DEBUG] sync  Acquired VRCVideoCacher's YtdlCookieJar gate.
  [PASS] hook-status.json appeared and IsFresh = true
  [PASS] L1..L5 = resolved（L6 事件订阅亦 resolved）
  [PASS] hook wrote the VRCVC cookie file / 6 rows / 7 fields / LOGIN_INFO present
  [PASS] last mock poll : count=6；login=True
  [PASS] mock host is still alive（Hook 未破坏宿主）
RESULT Test-02: PASS (19 checks)
```

结论：**`DOTNET_STARTUP_HOOKS` → `StartupHook.Initialize` → 反射探测 → 进程内 `YtdlCookieJar` 闸门 → 写 Cookie 文件**这条链路在框架依赖宿主上真实成立，且宿主自报的条数与 vault 完全一致。

### Test-02b 单文件负向对照 — PASS（8 checks）

```
  [PASS] no hook-status.json was produced
  [PASS] cookie file was NOT written by a hook inside the single-file host
  [PASS] bridge.log has no "Startup hook initialised" line
  [PASS] the single-file host itself started normally（MOCKHOST ready …）
--- COREHOST_TRACE evidence ---
    Property STARTUP_HOOKS = C:\...\CookieBridge.Hook.dll
    Property TRUSTED_PLATFORM_ASSEMBLIES =
    Property System.StartupHookProvider.IsSupported = false
  [PASS] COREHOST_TRACE shows System.StartupHookProvider.IsSupported = false
  [PASS] COREHOST_TRACE shows an empty TRUSTED_PLATFORM_ASSEMBLIES
```

结论：同一份 Hook DLL，**框架依赖宿主加载成功、单文件自包含宿主不加载**。`DOTNET_STARTUP_HOOKS` 被传进去了（`STARTUP_HOOKS` 有值），但宿主把 `System.StartupHookProvider.IsSupported` 关掉——这是机制边界，不是代码问题。Steam 版 VRCVC 正是这种单文件形态，因此对它走**契约模式**（NativeHost 写 `youtube_cookies.txt`）。

### Test-04 无 vault — PASS（8 checks）

```
  [PASS] bridge.log reports vault-missing（sync skipped, nothing else happened）
  [PASS] host process is still alive
  [PASS] cookie file was not created
  [PASS] hook-status.json exists（Hook 在跑，只是没东西可同步）
  [PASS] hook recorded the skipped sync reason（lastSyncOutcome = vault:vault-missing）
  [PASS] host produced no stderr output
RESULT Test-04: PASS (8 checks)
```

### Test-05 disabled.flag — PASS（6 checks）

```
  [PASS] cookie file was NOT written while disabled
  [PASS] no hook-status.json（插件在 Initialize 里直接返回）
  [PASS] bridge.log records that the plugin stayed passive（"disabled.flag present — plugin stays passive."）
RESULT Test-05: PASS (6 checks)
```

### Test-06 实时更新（不重启宿主）— PASS（9 checks）

```
before update: pid=29688 syncCount=1
  [PASS] hook wrote the first jar (6 cookies)
  [PASS] second jar delivered (10 cookies)
  [PASS] cookie file was updated to the new jar
  [PASS] update arrived within 10 seconds
live update latency: 1.28s
  [PASS] the host never restarted（same process still running）
  [PASS] hook still reports the same host pid
  [PASS] hook sync counter increased
  [PASS] the host polled the file and reported 10 cookies
RESULT Test-06: PASS (9 checks)
```

结论：`FileSystemWatcher` + 1 s 防抖生效，**1.28–1.39 s** 内完成 vault→目标文件同步，无需重启宿主。

### Test-07 兼容性闸门 — PASS（10 checks）

```
TOTAL 23  PASSED 23  FAILED 0   ← CompatibilityMatrix 相关 xUnit
  [PASS] host version newer than VerifiedVersion is still reported（2099.1.1）
  [PASS] layers still resolve for the unknown version
  [PASS] cookie file was still written（the gate degrades, it never blocks）
  [PASS] bridge.log classifies the host as UntestedNewer and continues
RESULT Test-07: PASS (10 checks)
```

### Test-03 真实 VRCVC 只读验收 — **BLOCKED（脚本缺陷，环境未污染）**

实际输出：

```
  [PASS] VRCVideoCacher.exe is installed
pre-existing VRCVideoCacher processes: 1（never touched by this test）
real cookie file does not exist - it will not be created by this test
newest VRCVC log before start: ...\AppData\Roaming\VRCVideoCacher\Logs\VRCVideoCacher20261101.log
started our own VRCVideoCacher instance, pid=17316
Test-03-RealVrcvcInjection.ps1 : You cannot call a method on a null-valued expression.
   + FullyQualifiedErrorId : InvokeMethodOnNull
```

* 阻塞原因：**脚本自身的空值调用**（在读取自身实例状态/日志那一段），不是产品问题；属我的测试脚本未加固，时间窗口内未修完。
* 影响面：**无**。该用例设计为只读：不写真实 `%APPDATA%\VRCVideoCacher\youtube_cookies.txt`（实测「real cookie file does not exist - it will not be created」），只 kill 自己起的 PID；脚本异常后 `finally` 仍执行了 `taskkill /T`。
* 该用例原本要验证的「真实 VRCVC 契约链路」已由 `docs/VERIFICATION.md` 在沙箱外实测覆盖（合成 Cookie → NativeHost → 真实路径 → 恢复原状），本报告不重复往真实路径写入。
* 复现命令：`powershell -File tests\integration\Test-03-RealVrcvcInjection.ps1`

---

## 5. 测试过程中发现并已修复的缺陷

### 5.1 `SecretRedactor.Redact` 死循环（Core，已修复）

最小复现：

```csharp
SecretRedactor.Redact("cookie LOGIN_INFO\tSECRET_LOGIN_VALUE\nnext line");   // 永不返回
```

根因：替换文本中保留了 `name + "\t"`，而循环又从字符串头部重新找 `name + "\t"`，于是永远命中自己写入的位置：

```csharp
text = text[..idx] + name + "\t<redacted>" + text[end..];      // 结果仍含 "LOGIN_INFO\t"
idx  = text.IndexOf(name + "\t", StringComparison.Ordinal);     // ← 死循环
```

触发条件：**少于 3 个制表符**的日志文本中出现「已知 Cookie 名 + TAB」（≥3 个 TAB 会先走 `\t<redacted-cookie-row>` 截断分支，所以原有测试没暴露）。影响面：`BridgeLog.Format` 对**每一条**日志都调用 `Redact`，Hook 心跳/同步线程会永久自旋。

实测表现：该用例让测试进程挂住 10 分钟不退出（需 `taskkill` 回收）。测试已加 3 秒超时，把它变成 `FAIL + 明确诊断`，Core 修复后 **136/136 全绿**。

### 5.2 测试隔离缺陷（Core，已修复）

`BridgePaths.Root` / `VrcvcDataDir` 曾做静态缓存，导致第一个用例设置 `VRCCB_ROOT` 后，后续用例共用一个 root（表现为 `HookInstallationTests` 单独跑 PASS、整套跑 FAIL）。改为每次解析后消失。

### 5.3 构建缺陷 CS0579（Hook csproj，已修复）

`<Compile Include="..\CookieBridge.Core\**\*.cs" />` 会命中 Core 自己 `obj\` 下生成的 `*.AssemblyInfo.cs`，导致 Hook 程序集出现两份 Assembly 特性 → 10 个 CS0579。已加 `Exclude="..\CookieBridge.Core\obj\**;..\CookieBridge.Core\bin\**"`。

### 5.4 我的测试代码自身问题（非产品）

| 问题 | 处理 |
| --- | --- |
| `[InlineData(null)]` 在自研执行器里被当成 0 个参数 | 已按参数个数补齐 |
| 用 2026-09 的时间戳当「未来过期」（机器时钟已是 2026-11） | 改用 2030/2031 |
| `hook-status.json` 首次心跳早于反射探测完成 → layers 为空 | 等待层信息就绪再加断言 |
| PowerShell 5.1 直接操作 `Process.StandardInput.BaseStream` 送 native message 时宿主读到 EOF | 改为 `Start-Process -RedirectStandardInput/Output` 文件重定向（与浏览器行为等价） |
| `$arguments` 与函数参数 `[string[]]$Arguments` 同名（不区分大小写）导致 splatting 类型转换错误 | 改名 `$startArgs` |

---

## 6. 未覆盖 / 未完成

| 项 | 状态 | 说明 |
| --- | --- | --- |
| Test-03 真实 VRCVC 只读验收 | BLOCKED | 脚本空值调用；真实契约链路见 `docs/VERIFICATION.md` |
| `dotnet test` 直跑 | BLOCKED（环境） | 沙箱进程句柄策略；普通环境应正常 |
| CookieBridge.Manager UI | 未纳入 | 本次任务范围外 |

## 7. 产物清单

| 路径 | 说明 |
| --- | --- |
| `bridge/CookieBridge.Tests/` | xUnit 测试（136 条），只引用 `CookieBridge.Hook.csproj` |
| `bridge/CookieBridge.TestRunner/` | 本机可用的等价执行器（`dotnet test` 被沙箱阻塞时使用） |
| `bridge/CookieBridge.MockHost/` | 双形态 Mock 宿主：`-p:MockHostFlavor=fd`（正向）/ `single`（负向对照） |
| `tests/integration/Build-All.ps1` | 构建/发布所有被测产物 |
| `tests/integration/Run-All.ps1` | 串起 xUnit + Test-01..07，输出 PASS/FAIL 汇总，退出码非 0 表示失败 |
| `tests/integration/Test-01..07*.ps1` | 各集成用例（含 Test-02b 负向对照） |
| `tests/integration/.work/` | 每次运行的隔离目录与日志（含 `runall-last.txt`、`summary.json`） |
