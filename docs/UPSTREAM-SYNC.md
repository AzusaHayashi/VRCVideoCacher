# 与上游同步 / 发布 fork / 许可证

上游仓库：`https://github.com/EllyVR/VRCVideoCacher`（Steam AppID `4296960`）。
本仓库是它的 fork，额外加了 **CookieBridge**（YouTube Cookie 持久化桥）。

本文的每条命令都可直接执行；凡涉及"你自己的仓库地址"都用 `<YOUR_ACCOUNT>` 占位。

---

## 1. 当前仓库的实际状态（先确认，再动手）

```powershell
git remote -v
git branch -a
git rev-parse --is-shallow-repository
git status --short
```

本分支 `feature/cookie-persistence` 的现状：

| 项 | 值 |
| --- | --- |
| `origin` | `https://github.com/GITHUB_USER/VRCVideoCacher-CookieBridge.git`（**占位地址，需替换**） |
| `upstream` | `https://github.com/EllyVR/VRCVideoCacher.git` |
| 本地分支 | `main`、`feature/cookie-persistence`（当前） |
| 远端跟踪 | 只有 `upstream/main`；`origin` 尚未 fetch |
| 浅克隆 | **是**（`--is-shallow-repository` = `true`，只有 1 个提交可见） |
| 未提交改动 | `M .gitignore`、`M VRCVideoCacher/Program.cs`；新增未跟踪 `VRCVideoCacher/Utils/CookieBridgePlugin.cs`、`bridge/`、`browser-extension/`、`docs/` |

浅克隆必须先补全历史，否则 `rebase` 会因为没有共同祖先而失败：

```powershell
git fetch --unshallow upstream
# 若 origin 也存在且需要历史：
git fetch --unshallow origin
```

---

## 2. fork 相对上游改了哪些文件（冲突面的全部来源）

```powershell
git diff --stat upstream/main -- .
```

当前输出（未提交的改动也算在内）：

```
 .gitignore                | 19 +++++++++++++++++++
 VRCVideoCacher/Program.cs |  4 ++++
```

加上两个**新增目录/文件**（上游完全不存在，因此不可能冲突）：

| 路径 | 性质 | 冲突可能 |
| --- | --- | --- |
| `VRCVideoCacher/Program.cs` | 在 `Main` 开头插入 4 行（注释 + `CookieBridgePlugin.TryLoad();`） | **高**（上游改 `Main` 就会冲突） |
| `VRCVideoCacher/Utils/CookieBridgePlugin.cs` | 新文件 | 无 |
| `.gitignore` | 末尾追加 CookieBridge 段 | 中（上游改 `.gitignore` 就会冲突） |
| `bridge/`（7 个子项目） | 全新目录 | 无 |
| `browser-extension/` | 全新目录 | 无 |
| `docs/`（CONTRACT/API/ARCHITECTURE/SECURITY/COMPATIBILITY/INSTALL/UPSTREAM-SYNC/FAQ） | 全新文件 | 无 |
| `README.md` | 顶部插入 Fork 说明区块 | **高**（上游每次都更 README 的可能性不小） |
| `README_*.md`（10 个翻译版） | **不动** | 无 |
| `VRCVideoCacher.sln`、`VRCVideoCacher.csproj` | **不动** | 无 |
| `VRCVideoCacher/Languages/*.loc.json` | **不动** | 无 |

这套布局是刻意的：**能新增就不修改，能改一行就不改十行**。上游 159 个文件里，我们只碰 3 个
（`Program.cs`、`.gitignore`、`README.md`），其余全部落在新目录里。

---

## 3. 同步上游（rebase 策略）

原则：**fork 的改动永远是"薄薄几层"叠在上游之上**，上游前进时把这几层挪上去，而不是反过来 merge。

```powershell
# 0) 干净起点：先把 fork 改动提交掉，工作区必须为空
git status --short          # 期望无输出
git checkout feature/cookie-persistence

# 1) 拉上游
git fetch upstream --tags

# 2) 看上游这段时间改了什么
git log --oneline HEAD..upstream/main
git diff --stat HEAD...upstream/main

# 3) rebase 到上游最新 main
git rebase upstream/main

# 4) 有冲突时逐个处理（三个热点文件见下表）
git status                 # 看冲突文件
#   编辑 → git add <file> → git rebase --continue
#   想放弃重来：git rebase --abort

# 5) 验证：构建 + 测试
dotnet build VRCVideoCacher.sln -c Release
dotnet test  bridge/CookieBridge.Tests/CookieBridge.Tests.csproj
node --test "browser-extension/tests/**/*.test.mjs"

# 6) 推回自己的 fork（rebase 会改写历史，必须 force-with-lease）
git push --force-with-lease origin feature/cookie-persistence
```

不要用 `git merge upstream/main`：它会在历史里留下一堆合并提交，下次 rebase 要重解一遍同样的冲突。

### 冲突热点与处理办法

| 文件 | 冲突形态 | 处理 |
| --- | --- | --- |
| `VRCVideoCacher/Program.cs` | `Main` 开头那 4 行（注释 `// CookieBridge (fork addition): ...` + `CookieBridgePlugin.TryLoad();`） | 保留我们的 4 行，**放在新版 `Main` 的最前面**（必须在 `LaunchArgs.SetupArguments` / `SteamAPI.RestartAppIfNecessary` 之前 —— 否则 `RestartAppIfNecessary` 会 `Environment.Exit(0)` 直接跳过插件加载） |
| `.gitignore` | 末尾 CookieBridge 段 vs 上游新增规则 | 两边都留：上游的放原位，CookieBridge 段保持在最末尾的独立小节 |
| `README.md` | 顶部 `> [!IMPORTANT] Fork notice` 区块 | 保留区块在最顶部，上游正文整体保留下移；`README_*.md` 不动，因此永远不会冲突 |
| `VRCVideoCacher/Utils/` | 上游新增同名文件（极小概率） | 我们的文件名是 `CookieBridgePlugin.cs`，与上游命名习惯不重叠；若真撞名，重命名为 `CookieBridgePluginLoader.cs` 并同步 `Program.cs` 里的一行 |

### 上游改了 Cookie 机制怎么办

`docs/COMPATIBILITY.md` §4 有完整判定表。判断顺序：

1. `YTDL/YtdlManager.cs` 里 `CookiesPath` 还等于 `Path.Join(Program.DataPath, "youtube_cookies.txt")` 吗？
   → 是：不用改任何代码。
2. `Program.IsCookiesValid` 还是「含 `youtube.com` + `LOGIN_INFO`」吗？
   → 变了：改 `bridge/CookieBridge.Core/NetscapeCookieFile.IsValidForVrcvc`，并更新 `docs/CONTRACT.md` §4。
3. `ApiController.FilterCookies` 的 `ST-` 规则变了吗？
   → 变了：同步改 `NetscapeCookieFile.Canonicalize` 与 `browser-extension/utils.mjs`。
4. `YtdlCookieJar.AcquireAsync` 还在吗？
   → 没了：不用改代码，L2 自动降级（见 `docs/COMPATIBILITY.md` §2）。

---

## 4. 发布自己的 fork

### 4.1 创建 GitHub fork

```powershell
# 用 gh（需要先 gh auth login）
gh repo fork EllyVR/VRCVideoCacher --clone=false
```

`--clone=false` 表示只在他的账号下建 fork、不clone —— 因为我们已经有本地仓库了。

没有 `gh` 的话，直接在网页上点 Fork，得到
`https://github.com/<YOUR_ACCOUNT>/VRCVideoCacher`。

### 4.2 把 origin 指向自己的 fork

```powershell
git remote set-url origin https://github.com/<YOUR_ACCOUNT>/VRCVideoCacher.git
git remote -v
# origin    https://github.com/<YOUR_ACCOUNT>/VRCVideoCacher.git (fetch/push)
# upstream  https://github.com/EllyVR/VRCVideoCacher.git        (fetch/push)
```

> 如果你想要一个**独立名字**的仓库（例如 `VRCVideoCacher-CookieBridge`），就在 GitHub 上新建一个
> 空仓库，然后把 `origin` 指过去。两种都行；名字带 CookieBridge 更容易让人一眼看懂这不是原版。

### 4.3 提交并推送分支

```powershell
git checkout -b feature/cookie-persistence     # 已存在则跳过
git add .gitignore VRCVideoCacher/Program.cs VRCVideoCacher/Utils/CookieBridgePlugin.cs
git add bridge browser-extension docs README.md
git status --short
git commit -m "CookieBridge: persist YouTube cookies without touching the VRCVC install"
git push -u origin feature/cookie-persistence
```

推送前自查（**必须**，仓库里不能有任何凭据）：

```powershell
# 1) 没有真实的 Cookie 文件/vault/日志被提交
git ls-files | Select-String -Pattern 'youtube_cookies|vault\.json|bridge\.log|\.cookiebridge\.bak'
# 期望：无输出

# 2) 测试数据只有合成 cookie
Select-String -Path browser-extension\tests\*.mjs -Pattern 'FAKE_'

# 3) 大文件（构建产物）
git ls-files | ForEach-Object { Get-Item $_ -ErrorAction SilentlyContinue } |
    Where-Object { $_.Length -gt 1MB } | Select-Object FullName, Length
```

### 4.4 回上游开 PR 的建议

**建议回上游的部分**（不改变别人的行为）：

* `bridge/` 与 `browser-extension/` 作为**独立可选组件**（全新目录，零侵入）；
* `docs/CONTRACT.md` 的契约描述（yt-dlp `--cookies` 契约 + Netscape 规则 + `ST-` 过滤理由）。

**建议不回上游、或必须做成 opt-in 的部分**：

* `Program.cs` 里那 4 行 `CookieBridgePlugin.TryLoad()` —— 会改变上游启动路径。若要提交，请：
  * 包在 `#if COOKIEBRIDGE` 之类的编译开关里，或
  * 做成默认关闭的配置项（例如 `Config.CookieBridgePluginEnabled`，默认 `false`），
  * 并在 PR 描述里说明「无插件时行为与现在完全一致，异常全部吞掉」。
* 对 `Program.IsCookiesValid` / `YtdlCookieJar` / `CookiesPath` 的任何改动 —— **不要提**。
  这些是 CookieBridge 依赖的稳定面，改动会让 fork 的反射层失去锚点。

PR 描述里最值得写清的三点：

1. `youtube_cookies.txt` 是当前唯一的 Cookie 入口，CookieBridge 只写这个文件；
2. 已实测 Steam 版单文件宿主 `System.StartupHookProvider.IsSupported = false`，
   所以没走 `DOTNET_STARTUP_HOOKS`（避免 reviewer 以为它靠注入）；
3. 不上传任何数据、不新开端口、不装服务。

---

## 5. 许可证与合规

### 5.1 事实（已核对，不是推测）

```powershell
# 本仓库
git ls-tree -r HEAD --name-only | Select-String -Pattern 'LICEN|COPYING|NOTICE'   # 无输出
# 上游
git ls-tree -r upstream/main --name-only | Select-String -Pattern 'LICEN|COPYING|NOTICE'  # 无输出
```

**上游仓库（159 个文件）和本 fork 的当前 HEAD 里都没有 `LICENSE` / `COPYING` / `NOTICE` 文件。**
因此在当前快照上无法引用"许可证类型与要点"——**没有可引用的许可证文本**。

按默认著作权规则，没有许可证 = 保留所有权利（all rights reserved）：技术上你有权 fork 在自己机器上
编译使用，但**对外分发二进制/源码前必须取得上游作者授权**，或等上游补上许可证。

> 上游 README 提到项目由 Elly / Natsumi / Haxy / Hauskaz / DubyaDude 共同创作
> （`Program.cs` 的 `Creator_*` 常量），并链接了 GitHub Releases 与 Steam 页面。
> 若上游在别的分支/Release 附件里给了许可证，请以那份为准；**这一点在本仓库内未验证**。

### 5.2 发布前必做

```powershell
# 1) 再从上游拉一次，确认真的没有许可证文件
git fetch upstream --tags
git ls-tree -r upstream/main --name-only | Select-String -Pattern 'LICEN|COPYING|NOTICE'

# 2) 如果上游补上了，把它取回仓库根目录并按其中条款执行
git show upstream/main:LICENSE | Set-Content -Path LICENSE -Encoding utf8
```

若确实没有许可证，公开发布 fork 之前至少做到：

1. 在 `README.md` 的 Fork notice 里写明「本项目是 `EllyVR/VRCVideoCacher` 的 fork，原项目未附许可证文件，
   fork 作者已尽力联系/标注」；
2. 保留全部原作者署名（`Program.cs` 的 `Creator_*`、README 里的原作者链接）；
3. 不开源发布、只自用（最省事）；
4. 或向上游作者取得书面许可（Issues / Discord 均可，把回复留存）。

### 5.3 第三方依赖

fork 自己新增的三个 .NET 项目没有引入任何新的 NuGet 包
（`bridge/Directory.Build.props` 只设编译属性；`CookieBridge.Hook` 把需要的 Core 源文件编译进来，
不额外引用 `CookieBridge.Core.dll`）。浏览器扩展零 npm 依赖、零构建步骤。
所以**不引入新的许可证义务**——义务仍然只来自上游原有的依赖（Avalonia、Serilog、EmbedIO、Sentry 等，
见 `VRCVideoCacher/VRCVideoCacher.csproj` 的 `PackageReference` 列表）。

### 5.4 数据合规

* 仓库里**不含任何真实 Cookie、账号、vault 或日志**；
* `browser-extension/tests/` 用的是合成 Cookie（`FAKE_*`）；
* `.gitignore` 显式排除 `vault.json`、`youtube_cookies.txt`、`*.cookiebridge.bak`、`disabled.flag`、
  `hook-status.json`、`pending.sync`、`backups/`、`dist/`、`bridge/**/bin|obj`。

发布前再跑一次 §4.3 的三条自查。

---

## 6. 上游同步的检查点清单

```powershell
# A. 与上游的差距只剩预期的三个文件 + 新目录
git diff --stat upstream/main -- .

# B. Program.cs 的 4 行仍在 Main 的最前面
Select-String -Path VRCVideoCacher\Program.cs -Pattern 'CookieBridgePlugin.TryLoad'

# C. 解决方案能编译
dotnet build VRCVideoCacher.sln -c Release

# D. CookieBridge 单元测试全绿
dotnet test bridge\CookieBridge.Tests\CookieBridge.Tests.csproj

# E. 扩展测试全绿
node --test "browser-extension/tests/**/*.test.mjs"

# F. 版本号仍是上游口径（YYYY.M.R）
Select-String -Path VRCVideoCacher\VRCVideoCacher.csproj -Pattern '<Version>'
```

`F` 若与 `docs/COMPATIBILITY.md` 的 `VerifiedVersion`（当前 `2026.10.29`）不一致，**先更新
`CompatibilityMatrix.VerifiedVersion` 并跑一遍实测**，再继续 rebase —— 否则矩阵会一直报「未验证（新版本）」。
