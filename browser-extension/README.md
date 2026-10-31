# VRCVideoCacher CookieBridge — 浏览器扩展

把 **YouTube 登录 Cookie** 通过 **Native Messaging** 直接交给本地 CookieBridge 原生宿主，
由宿主加密落盘并写入 VRCVideoCacher 的 Cookie 文件
（`%APPDATA%\VRCVideoCacher\youtube_cookies.txt`，Netscape 格式）。

* 不需要 VRCVideoCacher 在运行，也不需要本地 HTTP 服务（不监听任何端口）
* 不依赖构建步骤：纯 ES module + 原生 `chrome.*` API，目录即可加载
* **扩展 ID（本仓库生成）**：`fdjmcdfolkgicljalacnlemepfhmnmfp`
  → `chrome-extension://fdjmcdfolkgicljalacnlemepfhmnmfp/`
* Firefox 扩展 ID：`vrexcookiebridge@local`（由 `browser_specific_settings.gecko.id` 固定）

契约：`docs/CONTRACT.md`（§4 Netscape 格式、§5 Native Messaging 协议）。任何协议改动先改契约。

---

## 1. 目录结构

| 文件 | 作用 |
| --- | --- |
| `manifest.json` | MV3 清单；含 Chrome 扩展 ID 所需的 `key`、Firefox 的 `gecko.id` |
| `background.mjs` | Service worker：监听 Cookie 变化 / 页面消息 / 安装，写状态、画徽章 |
| `utils.mjs` | Cookie 抓取 + Netscape 序列化 + Native Messaging 发送（上游逻辑复用） |
| `content.js` | 上游原样保留：YouTube 页面加载时请求一次同步 |
| `options.html` / `options.js` | 状态页：上次同步时间、Cookie 条数、是否已写入文件、错误信息、"立即同步" |
| `tools/genkey.mjs` | 生成 RSA-2048 keypair，输出 manifest `key` 与扩展 ID |
| `tests/` | `node:test` 单元测试（Netscape 格式、消息构造、传输错误、清单一致性） |
| `extension-id.txt` | 生成结果留档（ID / origin / key），供 Native Host 注册时对照 |

## 2. 安装

### Chrome / Edge（加载已解压）

1. 打开 `chrome://extensions`（Edge：`edge://extensions`），打开右上角 **开发者模式**。
2. **加载已解压的扩展程序** → 选择本目录 `browser-extension/`。
3. 列表里的 ID 必须等于 `fdjmcdfolkgicljalacnlemepfhmnmfp`。
   若不同，说明 `manifest.json` 里的 `key` 被改过或丢失（见第 3 节）。
4. Native Host 需已注册，且其 `allowed_origins` 包含 `chrome-extension://fdjmcdfolkgicljalacnlemepfhmnmfp/`
   （尾部斜杠不能少）。

### Firefox 138+

临时载入（开发用，重启浏览器后失效）：

1. `about:debugging#/runtime/this-firefox`
2. **临时载入附加组件** → 选择本目录下的 `manifest.json`
3. Firefox 侧不看 Chrome 的 `key`，只看 `browser_specific_settings.gecko.id` = `vrexcookiebridge@local`，
   原生宿主的 `allowed_extensions` 必须是 `["vrexcookiebridge@local"]`。

永久安装需要签名：

```powershell
# 目录打包（zip 内以 manifest.json 为根，不能多一层文件夹）
Compress-Archive -Path browser-extension\* -DestinationPath cookiebridge-extension.zip
# 用 web-ext（若已安装）或 about:addons 的临时/未签名流程；正式分发走 addons.mozilla.org 签名
```

> 未签名的打包 XPI 无法在正式版 Firefox 永久安装，这是浏览器的策略，与本扩展无关。
> 临时载入每天足够用，配合宿主端的 `allowed_extensions` 即可。

## 3. 扩展 ID 是怎么来的（以及怎么换）

Chrome 为**未打包**扩展分配 ID 的规则是确定性的：

```
extension_id = SHA256(DER SubjectPublicKeyInfo of manifest.key)
               取前 16 字节，每个字节的两个 4-bit 半字节映射到 'a'..'p'
```

所以 `manifest.json` 里的 `key` 一旦固定，ID 就固定，`allowed_origins` 才不会随重装漂移。

重新生成一套身份（会得到**新 ID**，必须重新注册原生宿主）：

```powershell
node browser-extension/tools/genkey.mjs            # 只打印 key / id / origin
node browser-extension/tools/genkey.mjs --write    # 写回 manifest.json 的 key 与 extension-id.txt
node browser-extension/tools/genkey.mjs --json     # 机器可读输出
```

* 私钥只在内存里生成、**不落盘、不打印**：加载已解压扩展只需要公钥。
  只有将来要打 `.crx` 才需要保留私钥（自行 `openssl`/`node` 另存，别提交到仓库）。
* 改 `key` 后：`chrome://extensions` 里重新加载，ID 变化 → 更新原生宿主注册。

## 4. 与 Native Host 的对应关系

宿主名（契约 §5）：`com.vrcvideocacher.cookiebridge`

| 浏览器 | 注册位置 | 匹配字段 | 本扩展取值 |
| --- | --- | --- | --- |
| Chrome / Edge | `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.vrcvideocacher.cookiebridge`（默认值 = 宿主 manifest 绝对路径） | `allowed_origins` | `chrome-extension://fdjmcdfolkgicljalacnlemepfhmnmfp/` |
| Firefox | `HKCU\Software\Mozilla\NativeMessagingHosts\com.vrcvideocacher.cookiebridge` | `allowed_extensions` | `vrexcookiebridge@local` |

宿主 manifest 的关键字段（具体文件由 `bridge/` 侧管理器生成，这里只列形状）：

```json
{
  "name": "com.vrcvideocacher.cookiebridge",
  "description": "VRCVideoCacher CookieBridge native host",
  "path": "C:\\path\\to\\CookieBridge.Host.exe",
  "type": "stdio",
  "allowed_origins": ["chrome-extension://fdjmcdfolkgicljalacnlemepfhmnmfp/"],
  "allowed_extensions": ["vrexcookiebridge@local"]
}
```

协议（契约 §5，4 字节 little-endian 长度前缀 + UTF-8 JSON，单条 ≤ 1 MiB）：

```jsonc
// 扩展 → 宿主
{ "type": "cookie.update", "browser": "chrome", "extensionVersion": "1.0.0",
  "cookies": "# Netscape HTTP Cookie File\n...", "capturedUtc": "2026-11-01T00:30:00Z" }

// 宿主 → 扩展（成功）
{ "ok": true, "vaultUpdated": true, "cookieCount": 37, "applied": "vault+cookie-file",
  "targetWritten": true, "message": "ok" }

// 宿主 → 扩展（失败，错误字符串不得含 Cookie 数据）
{ "ok": false, "error": "invalid-cookie-format" }
```

## 5. 运行时行为

* **触发点**：YouTube 页面加载（content script）、`cookies.onChanged`（5s 防抖）、
  浏览器安装/更新、状态页"立即同步"。
* **抓取**：沿用上游 `getAllYouTubeCookies()`，同时取普通 store 与 `partitionKey` 下的 Cookie，
  并处理 Firefox `tab.cookieStoreId` / Chrome `getAllCookieStores()` / split incognito。
* **序列化**：上游逻辑逐字保留（7 个 `\t` 分隔字段、domain 强制 `.` 前缀、
  `TRUE` 表示 includeSubdomains、`Math.floor(expirationDate || 0)`），
  另外按契约 §4 **丢弃 `ST-` 开头的 Cookie**（Firefox 会话令牌会破坏 yt-dlp 的 YouTube 校验）。
* **徽章**：

  | 徽章 | 颜色 | 含义 |
  | --- | --- | --- |
  | `OK` | 绿 | 宿主返回 `ok:true` 且 `targetWritten:true`（已写入 Cookie 文件） |
  | `!` | 黄 | 宿主接受，但未写入 Cookie 文件（`targetWritten:false`） |
  | `ERR` | 红 | 失败（宿主缺失、超时、格式错误……），详情见状态页 |

  徽章由**单个**模块级定时器在 60s 后清除，重复同步只会重置该定时器，不会堆叠。

* **状态存储**：`chrome.storage.local["syncStatus"]`，字段白名单为
  `lastSyncUtc / capturedUtc / ok / targetWritten / vaultUpdated / cookieCount /
  exportedCookieRecords / loginCookiePresent / applied / error / errorDetail / elapsedMs / browser / nativeHost`。
  **不含任何 Cookie 名或值**；`errorDetail` 截断 200 字符。

## 6. 状态页

点击工具栏图标，或 `chrome://extensions` → 详情 → **扩展程序选项**：

* 上次同步时间（含实际抓取时刻）
* Cookie 条数
* 是否已写入 VRCVideoCacher Cookie 文件（绿/黄/红点 + 文字）
* 错误信息（机器码 + 截断后的宿主说明）
* **立即同步** 按钮
* **打开安装说明** 链接（打开本 README）＋ 内联"安装速览"

样式全部内联，不引用任何外部资源（清单测试会校验这一点）。

## 7. 权限说明

| 权限 | 原因 |
| --- | --- |
| `cookies` | 读取 `youtube.com` 域的 Cookie，并查询 cookie store |
| `nativeMessaging` | 与本地宿主通信的唯一通道 |
| `storage` | 保存上次同步状态供状态页回显（不存 Cookie） |
| `host_permissions: *://*.youtube.com/*` | 注入 content script，仅限 YouTube |

没有 `tabs`、没有 `<all_urls>`、没有 `webRequest`。

## 8. 测试

无依赖、无构建，Node 24 自带 `node:test`：

```powershell
cd browser-extension
node --test            # 5 个测试文件，56 条用例

# 或在仓库根目录用 glob（Node 24 不接受裸目录参数）
node --test "browser-extension/tests/**/*.test.mjs"
```

覆盖：

* `netscape.test.mjs`：字段数 = 7、domain 以 `.` 开头、`ST-` 过滤、secure/expiry 映射、
  `null`/非数组/malformed 输入不抛异常、`IsCookiesValid` 语义
* `native-message.test.mjs`：`cookie.update` 消息构造（字段集合、秒级 UTC）、
  宿主响应解析（成功 / `ok:false` / 空响应 / 非对象 / 超长错误截断）
* `send-native.test.mjs`：浏览器 stub 下的成功、宿主错误、`lastError` 映射、
  超时、抛异常、无 runtime；断言错误字段里不出现 Cookie 文本
* `integration.test.mjs`：stub 掉整个 WebExtension API，跑通
  `cookies.getAll`（含 partitionKey / cookie store）→ 序列化 → `sendNativeMessage`
  → `storage.local` 记录 → 徽章颜色，并断言落盘记录不含 Cookie 值
* `manifest.test.mjs`：MV3、权限集合、gecko id、`key` → 扩展 ID 推导一致、
  引用文件齐全、options 页无远程资源、代码里没有 HTTP 传输

测试数据全部为合成 Cookie（`FAKE_*`），不含任何真实账号信息。

## 9. 故障排查

| 现象 / 错误码 | 处理 |
| --- | --- |
| `ERR` + `native-host-not-found` | 宿主未注册，或注册表指向的 manifest 路径不存在。重跑管理器安装。 |
| `ERR` + `native-host-forbidden` | 宿主 `allowed_origins` 与当前扩展 ID 不一致（注意 `/` 结尾）。 |
| `ERR` + `native-host-disconnected` | 宿主进程启动即崩溃/被关闭：看 `<根>\logs\bridge.log`。 |
| `ERR` + `native-timeout` | 宿主 20s 未响应（锁竞争、磁盘慢）。看日志与 `state\`。 |
| 黄 `!` | 宿主已存 vault，但写 `youtube_cookies.txt` 失败（VRCVC 文件被占用等）。 |
| Cookie 条数为 0 | 未登录 YouTube，或当前是隐私容器（Firefox 容器标签请在该标签内打开一次 YouTube）。 |
| ID 与 README 不符 | `manifest.json` 的 `key` 被改动，或目录里混入了别的清单。 |

## 10. 约束

* 不引入任何 npm 依赖、构建工具；Node 仅用于 `tools/genkey.mjs` 与测试。
* 不写死任何真实 Cookie / 账号信息。
* 日志只打印条数与错误码，**不打印 Cookie 名称-值对**（契约 §8）。

> **注意**：实际实现会写出**两份**宿主 manifest —— Chromium 系只含 `allowed_origins`
> （`chrome-extension://fdjmcdfolkgicljalacnlemepfhmnmfp/`），Firefox 只含
> `allowed_extensions`（`vrexcookiebridge@local`）。见 `NativeHostRegistration`。
