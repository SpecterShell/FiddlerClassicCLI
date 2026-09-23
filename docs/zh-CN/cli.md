# Fiddler Classic CLI

[English (en-US)](../en-US/cli.md) | **简体中文 (zh-CN)**

`fiddler-classic-cli` 可执行文件提供命令，用于检查和控制正在运行的 Fiddler Classic 5.x 或 6.x，并以可读文本输出结果。为元数据命令添加 `--json` 可获得机器可读输出。

## 后台守护进程

依赖桥接的命令向持久运行的后台守护进程发送请求。首次执行操作时，CLI 会通过内部守护进程入口启动自身的隐藏副本，等待命名管道就绪后发送操作。后续调用会复用同一个进程。

Fiddler Classic 仅支持 Windows。守护进程使用仅限当前 Windows 用户访问的 Windows 命名管道。守护进程不作为系统服务运行，也不会注册为开机启动项。已安装的 Fiddler 扩展会在加载时启动或发现守护进程，以便管理标签页显示实时状态。

以下命令用于显式管理守护进程：

```powershell
fiddler-classic-cli daemon start
fiddler-classic-cli daemon status
fiddler-classic-cli daemon stop
```

启动守护进程不会启动 Fiddler、挂接系统代理或改变证书信任。`daemon status --json` 会返回进程 ID、启动时间、管道名称、主程序版本、能力和托管 HTTP 状态。

## 命令用法

单独运行 `fiddler-classic-cli` 会显示顶层帮助。单独运行命令组（如 `fiddler-classic-cli sessions` 或 `fiddler-classic-cli autoresponder rules`）会显示该组的帮助。这些调用与 `--help` 相同，将帮助写入 stdout，退出码为 0，不执行操作。

缺少必需输入或发生其他解析错误时，CLI 会将相应命令的帮助和错误写入 stderr，退出码为 2，不执行操作。使用 `--json` 时，stderr 仅包含结构化错误，不含帮助文本。解析错误不会向 stdout 写入内容，包括指定了 `--output -` 的情况。

需要取值的选项一旦出现在命令中，就必须提供值。省略可选选项时，仍使用文档中说明的默认值。

各项功能的行为分类见[功能类型与原生兼容性](feature-types.md)。列表过滤、等待游标、比较和载荷分块属于自定义操作，处理 Fiddler 原生证据，不会修改 Fiddler 的 Filters、Compare 或 Inspector UI 状态。

```text
fiddler-classic-cli doctor [--output <absolute-path>] [--json]
fiddler-classic-cli status [--json]
fiddler-classic-cli app detect [--path PATH] [--json]
fiddler-classic-cli app open [--path PATH] [--json]
fiddler-classic-cli app close [--pid PID] [--timeout SECONDS] [--yes] [--json]
fiddler-classic-cli app restart [--pid PID | --path PATH] [--timeout SECONDS] [--yes] [--json]
fiddler-classic-cli capture start|stop
fiddler-classic-cli sessions list [过滤参数] [--summary]
fiddler-classic-cli sessions watch [过滤参数] [--after-id ID] [--timeout 秒] [--count N] [--jsonl]
fiddler-classic-cli sessions show <session-id>
fiddler-classic-cli sessions body <session-id> --direction request|response --output <路径|->
fiddler-classic-cli sessions clear [--yes]
fiddler-classic-cli sessions remove --ids 1 2 [--yes]
fiddler-classic-cli sessions save <绝对路径.saz> [--ids 1 2] [--overwrite] [--yes]
fiddler-classic-cli sessions load <绝对路径.saz>
fiddler-classic-cli sessions replay <session-id> [--unconditional] [--wait] [--timeout 秒]
fiddler-classic-cli sessions export [session-id] --format curl|raw-http|har --output <路径|->
fiddler-classic-cli sessions diff <左侧ID> <右侧ID>
fiddler-classic-cli sessions websocket <session-id> list|get
fiddler-classic-cli request send <url> [-X METHOD] [-H "Name: value"] [--body 文本|--body-file 路径] [--wait]
fiddler-classic-cli autoresponder status|configure
fiddler-classic-cli autoresponder rules list|add|update|move|remove|clear|save|load
fiddler-classic-cli breakpoints status|arms|arm|disarm|list|wait|show|update|resume|abort
fiddler-classic-cli bridge install|uninstall
fiddler-classic-cli daemon start|status|stop
fiddler-classic-cli mcp stdio|http
fiddler-classic-cli mcp service status|configure|enable|disable
fiddler-classic-cli mcp clients list|authorize|deauthorize
fiddler-classic-cli mcp connections list|disconnect
fiddler-classic-cli config token show|rotate
```

必需的标识符和路径使用位置参数，可选值使用选项。传递多个标头时，应重复使用 `-H` 或 `--header`。

## Fiddler 应用程序

`app` 命令管理 Fiddler Classic 应用程序本身，通过 Windows 文件、注册表和进程 API 执行操作，无需连接桥接或启动守护进程。这些命令仅在 CLI 中提供。Fiddler 加载时，已安装的扩展仍可能按原有行为启动守护进程。

`app detect` 依次检查当前用户和 Program Files 下的标准安装位置，以及 Windows App Paths 注册项，同时列出当前 Windows 会话中属于当前用户的 Fiddler 进程。检测只读取可执行文件版本，不会启动程序，并会标明各版本是否受支持。启动、关闭和重启操作仅支持 Classic 5.x 和 6.x。检查自定义安装时，可用 `--path` 指定名为 `Fiddler.exe` 的绝对路径。该选项替代安装路径搜索，仍会列出正在运行的进程。命令不会下载或运行安装程序。

```powershell
fiddler-classic-cli app detect --json
fiddler-classic-cli app open --path "C:\Tools\Fiddler\Fiddler.exe"
```

如果已有受支持的进程运行，`app open` 会保持其状态不变。否则使用 `-noattach` 启动选定的可执行文件，禁止启动时挂接系统代理。未指定 `--path` 时，使用找到的第一个受支持的安装。Windows 接受启动请求后，命令即返回。请在 Fiddler 加载后运行 `status` 或 `doctor` 检查桥接是否就绪。已运行进程的捕获设置保持不变。[Telerik 的 `-noattach` 选项说明](https://www.telerik.com/fiddler/fiddler-classic/documentation/knowledge-base/configure-fiddler-and-upstream-proxy-to-work-on-same-machine)。

执行 `app close` 或 `app restart` 前，请先保存需要的捕获记录。两者均需确认。非交互式调用必须传入 `--yes`（或 `-y`）。关闭会停止当前捕获，并可能丢失未保存的会话。CLI 请求正常关闭窗口并等待进程退出，不会保存捕获记录、强制终止 Fiddler 或处理其原生对话框。`--yes` 仅确认 CLI 操作。`--timeout` 默认为 10 秒，可设为 1 到 60 秒。模态对话框可能阻止关闭请求或使进程继续运行。请在 Fiddler 中处理对话框，再运行 `app detect` 确认状态后重试。已发出的关闭请求在超时或取消后仍可能完成。[Windows 正常关闭行为说明](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.closemainwindow?view=net-10.0)。

```powershell
fiddler-classic-cli app close --pid 1234 --timeout 30 --yes
fiddler-classic-cli app restart --yes
```

`app restart` 会先验证运行中进程的可执行文件，待进程退出后再从同一路径重新启动。超时或取消时不会启动替代进程。新进程使用 `-noattach`，不会恢复捕获记录或先前的捕获状态。如果 Fiddler 已停止，restart 会打开选定的安装。没有进程运行时，`app close` 成功返回且不作更改。显式指定的 PID 不存在时会返回错误。

有多个 Fiddler 进程时，请用 `--pid` 为 close 或 restart 选择目标。restart 的 `--pid` 与 `--path` 不能同时使用。已有一个安装运行时，命令会拒绝打开或重启另一个安装。请先显式关闭已有进程。无法验证进程所有者或可执行文件元数据时，命令返回错误，不会自行提权。同一用户已有启动、关闭或重启命令持有操作锁时，其他此类命令会被拒绝。关闭 Fiddler 不会停止其守护进程或托管 MCP HTTP 监听器。

使用 `--json` 时，检测结果包含 `installations` 和 `processes` 数组。两者均含 `executablePath`、`version` 和 `supported`，进程记录另含 `processId`。启动、关闭和重启的结果含 `action`、`changed`、`running`、`processId` 和 `executablePath`。启动成功时报告 `running: true`。此结果不保证进程持续运行或桥接可用。

检测仅在两个数组均为空时返回退出码 3，否则返回 0。启动、关闭和重启操作中，无效选项、路径或不支持的版本返回 2。缺少安装、进程不可访问或启动失败返回 4。缺少确认、冲突或 PID 不存在返回 5。等待退出超时返回 6。指定 `--json` 后，参数解析和运行时错误均以常规 JSON 错误对象写入 stderr。

## 抓包与代理路由

`capture start` 将 Fiddler 挂接为运行它的 Windows 主机的系统代理，`capture stop` 取消挂接。两者均保留证书信任和 HTTPS 解密设置。

无论系统代理抓包是否挂接，显式路由的流量都可到达正在运行的 Fiddler 代理监听器。本机客户端可使用监听器的回环地址和代理端口。远程客户端需要在 Fiddler 中配置远程访问，使其监听可达接口或所有接口。IPv4 `0.0.0.0` 表示绑定所有 IPv4 接口，客户端应使用具体的主机地址和配置的代理端口。路由和防火墙规则也必须允许连接。

Fiddler 代理监听器与 MCP HTTP 监听器的绑定地址和端口分别配置。更改 `mcp service` 设置不会配置 Fiddler 代理监听器，也不会将客户端流量路由到它。CLI 不会添加网络路由或防火墙规则。要停止显式路由的流量，需要在获得授权后更改客户端路由或停止 Fiddler 代理监听器。

## 过滤与实时监视

会话列表、监视和 HAR 导出使用相同的过滤参数，支持按 ID、方法、主机、URL、状态码、响应 MIME 类型、进程、标头精确名称和值、耗时、HTTP 协议、正文总大小和错误状态筛选。

`--body-contains` 在 `--body-direction` 指定的请求或响应正文前缀中精确搜索 UTF-8 字节。`--body-search-bytes` 默认为 65,536，每个会话最多 1,048,576 字节。仅在提供 `--body-contains` 时搜索正文。

未提供 `--after-id` 时，`sessions watch` 从当前最新 ID 之后开始。命令持续输出已完成的匹配会话，直至达到 `--count`、触发 1-60 秒的无活动超时或进程被取消。`--jsonl` 每行输出一个会话对象。

## 有界会话汇总

`sessions list --summary` 和 `summarize_network_requests` 按现有过滤条件和排序汇总一个元数据页。数量上限默认为 100，可设为 1 到 1,000。此上限仅限制返回记录数，桥接的元数据过滤扫描不受此限制。汇总模式拒绝标头名称、标头值和正文内容搜索。

```powershell
fiddler-classic-cli sessions list --host example.test --limit 200 --summary --json
```

结果包含 `scope: "returned_sessions"`、数量上限、匹配数、返回数、ID 范围和 `truncated`。所有计数和总数仅覆盖返回记录。`byHost` 按主机名分组，不区分大小写。`byStatus` 将缺失状态码的记录单独归入 null 组。请求和响应字节总数统计已捕获的正文，包括未完成会话的正文，不包含标头或传输开销。

耗时统计以返回的已完成会话为样本，仅采用有限且非负的耗时，提供样本数、最小值、最大值、中位数和最近秩法 p95，单位为毫秒。无样本时统计值为 null。MCP 序列化可能省略 null 字段。汇总不读取标头或正文，也不获取后续页面。主机名和活动计数仍可能是敏感信息。

## 诊断文件

```powershell
fiddler-classic-cli doctor --output C:\Temp\fiddler-diagnostics.json --json
```

指定 `--output` 后，`doctor` 会创建新的 JSON 文件，包含数字形式的组件版本、预期协议版本、已知能力、监听状态，以及预定义的错误和处理建议。监听器元数据包含白名单内的 `bindMode`、`startupMode` 和 `authenticationMode` 字符串。报告不包含选定 IP、端点 URL、适配器名称、流量、标头、正文、凭据、客户端身份、本地路径、原始错误文本或任意版本后缀。导出不会启动守护进程或创建配置。守护进程停止时，报告无法确定已保存的监听设置。

请指定现有可写目录中普通文件的绝对路径。命令拒绝已有文件、标准输出 (`-`)、设备路径和备用数据流，并以原子方式发布完整报告。即使报告记录了组件不可用，导出成功仍返回退出码 0。`--json` 输出包含路径和格式的回执。无效路径返回 2，无法创建文件返回 5。不指定 `--output` 时，命令使用原有健康检查和退出码。分享前请检查报告内容。

## 修改与关联

`sessions remove --ids ...` 只删除选定会话。只要任一 ID 不存在，操作就会失败。其交互式确认行为与 `sessions clear` 相同。

重放和请求构造操作通常在请求被接受后立即返回。添加 `--wait` 后，命令会等待首个已完成请求，要求其 ID 位于操作前的基准 ID 之后，且方法和 URL 与该操作匹配。等待超时不会撤销已接受的请求。

## 导出、比较与 WebSocket

`sessions export <id> --format curl|raw-http` 复现单个敏感请求。原始 HTTP 保留二进制正文字节。如果二进制正文无法安全表示为单条 Shell 命令，cURL 导出会拒绝执行。`sessions export --format har` 将过滤后的会话集合导出到绝对 `.har` 路径。覆盖现有文件需要 `--overwrite` 和明确确认。

`sessions diff` 报告元数据、保持原始顺序的请求/响应头、耗时以及请求/响应正文 SHA-256 哈希的差异，不输出正文。`sessions websocket ... list` 返回帧方向、操作码、时间戳、长度、续帧和结束帧状态。`get` 命令流式输出完整帧载荷，可指定起始字节偏移。

## AutoResponder

检查并配置当前引擎：

```powershell
fiddler-classic-cli autoresponder status
fiddler-classic-cli autoresponder configure --enable --permit-fallthrough --accept-connects --use-latency
```

规则使用 Fiddler 原生匹配和操作字符串。桥接为每个已加载的规则分配运行时 ID，并保留从零开始的求值顺序：

```powershell
fiddler-classic-cli autoresponder rules list
fiddler-classic-cli autoresponder rules add "EXACT:https://example.test/api" "*drop"
fiddler-classic-cli autoresponder rules update <rule-id> --action "*reset" --comment "temporary"
fiddler-classic-cli autoresponder rules move <rule-id> 0
fiddler-classic-cli autoresponder rules remove <rule-id> --yes
```

`--disable-on-match` 会在首次匹配后禁用规则。只有启用引擎的延迟开关时，每条规则的 `--latency-ms` 才会生效。规则对象处于加载状态期间，其 ID 保持稳定。加载 FARX 文件后应重新列出规则。

FARX 路径必须是绝对路径。导入会追加规则。替换属于破坏性操作：

```powershell
fiddler-classic-cli autoresponder rules save C:\captures\rules.farx
fiddler-classic-cli autoresponder rules save C:\captures\rules.farx --overwrite --yes
fiddler-classic-cli autoresponder rules load C:\captures\rules.farx
fiddler-classic-cli autoresponder rules load C:\captures\rules.farx --replace --yes
```

桥接将原生操作原样传递给 Fiddler。这些操作可以重定向、生成、延迟、丢弃或重置流量，也可以从当前用户有权访问的本地路径读取响应。

## 断点

触发器在请求或响应阶段匹配后续流量。默认只触发一次，并在 30 秒后自动恢复托管暂停的会话。使用 `--persistent` 保留触发器，使用 `--hold 1..300` 修改超时秒数。

```powershell
fiddler-classic-cli breakpoints arm request --method POST --host example.test --hold 45
fiddler-classic-cli breakpoints arm response --status 500 --content-type json
fiddler-classic-cli breakpoints arms
fiddler-classic-cli breakpoints disarm <arm-id>
```

两个阶段使用相同的方法、主机、URL、进程和当前阶段标头过滤条件。状态码与内容类型仅适用于响应触发器。`disarm` 只阻止后续匹配，不会恢复已暂停的会话。

使用单调递增的序列号作为等待游标：

```powershell
fiddler-classic-cli breakpoints list
fiddler-classic-cli breakpoints wait --after-sequence 0 --timeout 30
fiddler-classic-cli breakpoints show <breakpoint-id>
```

`show` 返回元数据和原始标头，不返回正文字节。`sessions body` 命令可以通过会话 ID 读取暂停中的会话。每次修改都会检查当前阶段：

```powershell
fiddler-classic-cli breakpoints update <breakpoint-id> --method PUT --url https://example.test/new --set-header "X-Test: yes" --body-file .\body.bin
fiddler-classic-cli breakpoints update <breakpoint-id> --status 201 --reason Created --remove-header Content-Encoding --body "done"
fiddler-classic-cli breakpoints resume <breakpoint-id>
fiddler-classic-cli breakpoints abort <breakpoint-id> --yes
```

请求暂停可修改方法、URL、请求头和请求正文。响应暂停可修改状态码、原因短语、响应头和响应正文。正文更新会替换完整正文，最大 4 MiB。列表也包含 Fiddler UI 中手动创建的断点。自动恢复超时仅适用于托管触发器产生的暂停。

## MCP 协议

`mcp stdio`、前台 `mcp http` 和守护进程托管的 HTTP 监听器均使用官方 C# SDK 2.2.0。两种传输支持协议版本 `2026-07-28`，回归测试也覆盖 `2025-11-25` 和 `2025-06-18` 的握手流程。各版本共用 36 个工具、确认参数和结构化结果，载荷上限均为 64 KiB。

`2026-07-28` 允许直接调用，无需 `initialize`。可通过 `server/discover` 查询功能及所支持的现代协议版本。每次请求都在 `params._meta` 中提供 `io.modelcontextprotocol/protocolVersion` 和 `io.modelcontextprotocol/clientCapabilities`，并建议提供 `io.modelcontextprotocol/clientInfo`。兼容的 SDK 会构造这些元数据。

HTTP 请求每次都须发送 `MCP-Protocol-Version` 和 `Mcp-Method`，`tools/call` 还须发送 `Mcp-Name`，标头值必须与正文一致。`Accept` 须同时包含 `application/json` 和 `text/event-stream`。端点采用无状态模式，不生成协议会话 ID，也不提供独立的 GET 事件流或 DELETE 会话路由。TCP 连接仍会显示在管理标签页中。

现代协议的普通结果包含 `resultType: "complete"`。发现结果和工具列表使用 `ttlMs: 0` 与 `cacheScope: "private"`，这些提示不允许缓存抓包流量。关闭 HTTP 响应流会取消待处理工作，包括桥接通信。已经派发的操作仍可能已完成。

| HTTP 响应 | 含义 |
| --- | --- |
| 400，JSON-RPC `-32020` | 现代协议要求的标头缺失或与正文不一致。 |
| 400，JSON-RPC `-32022` | 不支持所请求的版本。`error.data` 包含 `requested` 和 `supported`。 |
| 404，JSON-RPC `-32601` | RPC 方法未知。 |
| 401 | 服务要求身份验证，但 Bearer 凭据缺失或无效。 |
| 403 | 请求包含 `Origin` 标头，或匿名托管请求未通过 Host 校验。 |
| 405 | HTTP 方法没有对应的端点路由，包括 GET 和 DELETE。 |

这些传输层响应与 CLI 退出码相互独立。旧版客户端继续使用 `initialize` 和协商版本对应的请求格式。协议细节见 [MCP 规范](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http)和 [C# SDK 版本说明](https://csharp.sdk.modelcontextprotocol.io/v2/versioning.html)。

## 托管 MCP HTTP

前台 `mcp http` 服务器只绑定回环地址，始终要求 Bearer 身份验证。如果端口已被其他监听器占用，会返回明确的绑定错误并退出。托管设置不影响前台服务器的身份验证或绑定。守护进程管理的监听器可持久运行，默认禁用、仅绑定回环地址，身份验证模式为 `non-loopback`。查询已保存的状态不会启动已经停止的守护进程：

```powershell
fiddler-classic-cli mcp service status
fiddler-classic-cli mcp service configure --bind loopback --port 8877 --authentication non-loopback
fiddler-classic-cli mcp service enable
fiddler-classic-cli mcp service disable
```

`mcp service configure` 接受以下选项，省略的选项保留原有设置：

| 选项 | 值与行为 |
| --- | --- |
| `--bind` | `loopback` 绑定 `127.0.0.1`，`all` 绑定 IPv4 `0.0.0.0`，`selected` 绑定通过 `--address` 显式指定的地址。 |
| `--address` | 可重复指定选定的活动本地 IPv4 地址，最多 16 个互不重复的点分十进制单播地址。要求同时指定 `--bind selected`，或已保存的模式为 `selected`。配置保存具体的 IP 地址。 |
| `--port` | 所有配置地址共用的端口，范围为 1 至 65535。 |
| `--startup` | `enabled`、`disabled` 或默认值 `last-state`，控制启动时的服务状态。 |
| `--authentication` | `required` 检查每个请求，`non-loopback`（默认值）仅豁免套接字远端和本地 IP 都是回环地址的连接，`none` 跳过 Bearer 检查。仅适用于托管监听器。 |
| `--yes` | 确认安全警告，跳过交互提示。stdin 被重定向时，此类更改必须提供该选项。 |

新建或未设置的配置使用 `non-loopback`。配置文件中已有的显式旧字段 `HttpRequireAuthentication` 在加载时将 `true` 转为 `required`，将 `false` 转为 `none`。配置文件保存字符串 `HttpAuthenticationMode`；传输协议和 CLI 服务 JSON 使用 `authenticationMode`。

`non-loopback` 豁免依据实际套接字的两个 IP，判断前会将 IPv4 映射地址转换为 IPv4。即使请求来自本机，访问局域网地址仍须提供 Bearer 凭据。任一地址未知时都要求身份验证，`Host`、`Forwarded` 和 `X-Forwarded-For` 不能获得豁免。豁免请求为匿名请求，拥有完整 MCP 权限，即使携带 Bearer 令牌也不会归属到任何凭据。

更改绑定模式、选定地址、端口或身份验证前，必须禁用服务。仅修改启动策略时，可保持服务运行。例如，先用 `mcp service status --json` 查看 `availableInterfaces`，再选择本机实际存在的地址：

```powershell
fiddler-classic-cli mcp service configure --bind selected --address 127.0.0.1 --address 192.168.1.10 --port 8877
fiddler-classic-cli mcp service enable --yes
```

请将示例中的局域网地址替换为活动的本地 IPv4 地址。指定 `--bind selected` 时，必须提供至少一个 `--address`。已保存选定模式时，`--address` 会替换原地址列表。切换到 `loopback` 或 `all` 会清除该列表。服务会逐一绑定选定地址。如果启动时某个地址不可用，会报告绑定错误，守护进程仍可供管理操作使用。服务不会替换为其他地址，也不会回退为监听所有接口。DHCP 或适配器发生变化后，应检查所选地址。

启用远程访问需要在交互式警告中确认或提供 `--yes`。使用 `required` 或 `non-loopback` 时，警告会说明 Bearer 凭据通过明文 HTTP 传输，被截获后可重复使用。使用 `none` 时，警告会说明所有可达客户端都将获得完整 MCP 权限。仅绑定回环地址时，保存或启用 `non-loopback` 无需访问风险确认。保存或启用 `none` 即使仅绑定回环地址也需要确认。即使守护进程已停止，禁用操作仍会更新已保存的状态。有活动连接时禁用服务需要确认。

启动策略在守护进程启动时，以及 Fiddler 加载扩展时生效，也适用于当时已在运行的守护进程。`enabled` 启动监听器，`disabled` 停止监听器，`last-state` 保留已保存的预期启用状态。新安装保持禁用。更改策略会保存设置，供下一次启动使用，当前服务状态不变。需要立即更改时，请使用 `service enable` 或 `service disable`。只要配置更新后的策略为 `enabled`，且允许远程访问或使用 `none` 模式，该次更新就需要确认：

```powershell
fiddler-classic-cli mcp service configure --startup enabled --yes
fiddler-classic-cli mcp service configure --startup disabled
fiddler-classic-cli mcp service configure --startup last-state
```

要通过 `none` 跳过所有托管 Bearer 检查，请先禁用服务，再明确确认风险：

```powershell
fiddler-classic-cli mcp service disable --yes
fiddler-classic-cli mcp service configure --authentication none --yes
fiddler-classic-cli mcp service enable --yes
```

所有能连接到 `none` 模式监听器的客户端都可以读取抓包流量并调用全部 MCP 工具，包括修改操作。本机进程（包括其他 Windows 账户下的进程）也可通过默认 `non-loopback` 的回环豁免获得相同权限。命名管道仍仅允许当前 Windows 用户访问。通过回环连接的本地中继或反向代理会被视为回环对端。如需回环调用方或中继也验证身份，请选择 `required`。各工具仍按文档要求接收确认参数，但任何已连接客户端都可提供这些参数。如需每个请求都提供凭据，请先禁用服务，配置 `--authentication required`，再重新启用。使用 `--authentication non-loopback` 可恢复默认豁免。所有模式都会保留命名凭据和默认凭据，但凭据不会限制匿名访问，也不会用于标识匿名请求。所有模式都拒绝携带 Origin 的请求。匿名请求（包括回环豁免请求）还必须使用与接收套接字实际 IP 地址和端口匹配的 Host 标头，回环连接也可使用 `localhost` 加相应端口。匿名请求不得使用自定义 DNS 名称。本项目不会启用 CORS，也不配置 TLS 或防火墙规则。

只有连接尝试未得到应答且能够独占守护进程所有权管道时，才允许离线管理。已连接或繁忙的守护进程超时会报告错误（`timeout`，退出码 6）。在这种情况下，管理操作不会将已保存的状态当作实时状态读取，也不会离线写入配置或启动另一个守护进程。进一步操作前请重试状态查询。响应格式错误或对端连接中断也会报告错误。

无效模式、端口或地址列表返回退出码 2。缺少确认或在服务启用时尝试更改监听设置返回 5。启用时绑定失败返回 4，此时可能出现 `enabled: true`、`running: false`。请检查 `lastError`，禁用服务后再修改设置。启动时绑定失败也会保留守护进程，供状态查询和配置使用。指定 `--json` 时，失败通过 stderr 输出结构化错误，成功则返回服务状态对象。

默认 CLI 令牌通过 `config token show|rotate` 管理。命名客户端使用独立的 256 位令牌：

```powershell
fiddler-classic-cli mcp clients list
fiddler-classic-cli mcp clients authorize --name "Local agent"
fiddler-classic-cli mcp clients deauthorize <client-id> --yes
```

命令只显示一次授权令牌，配置中仅保存其 SHA-256 哈希。名称长度为 1 至 64 个字符，按不区分大小写的规则检查唯一性，命名客户端上限为 64 个。取消 `default` 客户端的授权会轮换默认 CLI 令牌。取消授权需要确认，并会中止使用该凭据的活动连接。已经分派的操作可能已经完成。

前台请求和需要身份验证的托管请求使用同一份配置中的凭据。轮换和撤销会在后续请求中生效，无需重启监听器。连接检查和强制断开仅适用于守护进程管理的监听器。撤销凭据不会中止已经分派的前台操作。撤销凭据无法阻止通过 `none` 或 `non-loopback` 回环豁免获得的匿名访问。

连接检查仅返回运行元数据：

```powershell
fiddler-classic-cli mcp connections list
fiddler-classic-cli mcp connections disconnect <connection-id> --yes
```

记录包含连接 ID、远程端点、授权和客户端状态、连接及活动时间，以及活动和累计请求数。记录不会包含 URL、标头、令牌或抓包流量。`none` 模式的请求和 `non-loopback` 中豁免的回环请求均记录为 `anonymous`，不会归属到令牌客户端。断开连接需要确认，并会中止选定的传输连接。

**Fiddler Classic CLI** 标签页包含原生 **MCP**、**Named pipes** 和 **Settings** 子标签页，Tools 菜单入口可切换到该面板。MCP 顶部为 **Enable MCP HTTP** 复选框，其后依次为 Bind、Port 和 Apply/Refresh。按钮下方的 **MCP addresses** 分组框集中显示回环和局域网 URL，**Authorized clients** 和 **Active connections** 各有独立的分组框。选择 **selected** 绑定模式后，可通过带有适配器名称的本地 IPv4 地址列表勾选多个地址。这些控件管理 MCP HTTP，与 Fiddler 抓包代理相互独立。

Settings 包含启动策略和身份验证下拉框，各选项下方都有独立说明。身份验证选项为 **Require for all**（`required`，每个请求均须凭据）、**Non-loopback only**（默认值 `non-loopback`，仅回环连接豁免）和 **No authentication**（`none`，不检查 Bearer 凭据）。点击 **Save settings** 应用更改。其中的 **Versions** 分组框显示 Fiddler、桥接、协议、所配置主程序和运行中守护进程的版本，**Documentation** 分组框提供项目与文档链接。每次刷新都会重新读取所配置主程序和运行中守护进程的版本，包括守护进程重启后的版本。Named pipes 直接在子标签页中显示只读[管道诊断](installation.md#命名管道)，不使用外围分组框。

扩展在加载时启动或发现守护进程，并应用启动策略，即使用户从未打开标签页也会执行。卸载会取消扩展的后台工作，守护进程继续运行。

MCP HTTP 启用时，Bind 和 Port 仍可编辑。刷新会保留待应用的修改，URL 继续显示实际应用的设置。绑定有变化时，点击 **Apply** 会停止已启用的监听器、保存设置，再重新启动。停止前，面板会要求确认远程访问或 `none` 模式的风险，以及断开活动客户端连接的影响。取消确认会保留监听器原状。任一步骤失败都会停止后续操作，保留编辑内容并显示错误，重试前请检查复选框和错误信息。绑定未变时点击 Apply 不会重启监听器。CLI 配置仍要求先显式禁用服务，身份验证修改也需要先禁用服务，启动策略则可随时编辑。刷新会保留表格选中项和滚动位置。如果选中的客户端或连接已不存在，标签页会清除选择并禁用对应的操作按钮。

后台刷新只更新有变化的单元格，并根据客户端和连接的变化增删行。未排序时，已有行保持原有位置。读取期间仍可点击操作按钮。点击时会记住操作对象和输入，等待当前读取完成后执行，并在操作结束前暂时禁用其他操作按钮。刷新按钮在读取期间保持禁用。

标签页使用带白色 `>_` 提示符的自定义终端图标，按 Fiddler 的标签页图标尺寸绘制，无需外部图片文件。服务标签和值按列对齐，Bind 和 Port 各占一行。接口选择列表设置 `UseCompatibleTextRendering=false`，使文字渲染与周围原生控件一致。操作按钮共用按字体计算的高度和间距，授权对话框也采用相同规则。表格行高随字体调整，背景、文字和选中项使用 Windows 系统颜色。面板变窄时，按钮以及较长的状态和版本文本会自动换行。各区域超出可用高度时，可向下滚动查看。扩展不会更改 Fiddler 的 DPI 兼容性设置。

标签页控制请求和守护进程启动各有十秒的时限。超时后，请先检查服务状态再决定是否重试，因为操作可能已经完成。配置事务会等待其他 CLI 或守护进程完成更新，最多等待五秒。超时仍未取得配置锁时会报告 `timeout`（CLI 退出码 6）。

每个显示的 URL 旁都有复制按钮。`loopback` 和 `all` 模式提供回环地址，`selected` 模式仅在选中回环时提供。在 `all` 模式下，面板还显示最多八个不同的 IPv4 URL 提示，来自活动的非回环适配器。`selected` 模式下的局域网 URL 仅对应选定地址。这些提示未经可达性测试，路由和 Windows 防火墙仍由用户管理。服务禁用时仍可能显示地址提示。

服务 JSON 通过 `bindAddresses` 和 `endpoints` 数组报告监听器绑定，单值字段 `bindAddress` 和 `endpoint` 表示第一个绑定。`availableInterfaces` 最多列出 64 项可选接口，每项包含 `address` 和 `adapterName`。运行期间，绑定、端口和 `authenticationMode` 描述监听器实际应用的设置，未运行时则描述已保存的设置。`startupMode` 和预期的 `enabled` 状态始终反映已保存的偏好。选定绑定不含回环时，`loopbackEndpoint` 为空，`lanEndpoints` 包含适用的局域网 URL。`all` 模式中的 `0.0.0.0` 是绑定元数据，连接时应使用客户端 URL 字段中的具体地址。

请通过面板或 `mcp service configure` 更改监听设置。直接编辑配置文件不会重新配置运行中的监听器。应先禁用服务，应用所需更改后再启用。

控件具有无障碍名称、明确的 Tab 顺序和键盘助记键。使用 Tab/Shift+Tab 在控件之间移动，Alt+B 选择绑定模式，Alt+P 选择端口，按钮中带下划线的字母用于执行相应操作。布局测试覆盖窄面板和放大字体，不会修改 Fiddler 的 DPI 设置。

URL 和命名管道地址使用只读文本框。可拖动鼠标选中部分地址，或按 Tab 聚焦后按 Ctrl+A 全选，再按 Ctrl+C 复制。调整面板大小时会保留选区，刷新后内容未变时也会保留。旁边的复制按钮始终复制完整地址。

## 输出

元数据命令默认输出简洁的可读文本：

```powershell
fiddler-classic-cli sessions list --host example.test --limit 20
```

脚本应使用 `--json`：

```powershell
fiddler-classic-cli sessions list --host example.test --limit 20 --json
```

`sessions body` 和 `sessions websocket ... get` 将完整原始字节写入 `--output` 指定的路径，此选项为必需项。使用 `--output -` 时，stdout 只包含载荷字节，诊断信息写入 stderr。

## 故障排查

首次安装桥接后，如果标签页没有出现或桥接命令无法连接，请检查 Fiddler 是否弹出了 "Caution: Unverified Extension Detected" 窗口。桥接 DLL 和协议 DLL 可能分别触发提示。请逐一检查文件并手动处理提示，再重试连接。详见[扩展授权说明](installation.md#fiddler-桥接)。

客户端无法协商 MCP 时，请检查其配置的可执行文件和运行中的守护进程是否使用预期的已安装构建。升级后须重启相应的服务器进程。使用 `2026-07-28` 时，应先查看 400 响应中的 JSON-RPC 错误再重试。缺失标头的错误应按该版本的请求要求处理。遇到 HTTP 403 时，原生客户端应省略 `Origin`，并使用界面显示的监听器 URL 和匹配的 Host 标头。`none` 和 `non-loopback` 豁免均不会绕过 Origin 或匿名 Host 检查，也不应通过启用 CORS 绕过限制。

网络变化后选定绑定失败时，请对照已保存的 `bindAddresses` 与 `availableInterfaces`。禁用服务，选择预期的活动本地地址并应用，再重新启用。监听器不会静默扩大访问范围。如果服务状态在 Fiddler 或守护进程启动时改变，请检查 **Settings** 或 `startupMode`。使用 `last-state` 可保留上次的预期状态。

命令无法连接时，检查守护进程、服务和桥接状态：

```powershell
fiddler-classic-cli daemon status
fiddler-classic-cli mcp service status
fiddler-classic-cli doctor
```

CLI 守护进程管道失效时，只重启守护进程：

```powershell
fiddler-classic-cli daemon stop
fiddler-classic-cli daemon start
```

如果 Fiddler 标签页报告缺少 `managed-http-v3` 能力，请重启旧版守护进程。守护进程封装协议仍为版本 1，桥接协议仍为版本 3。只有 `doctor` 报告扩展桥接未连接时才重启 Fiddler Classic。即使 Fiddler 已关闭，守护进程和托管监听器也可以继续运行。桥接操作会返回不可用或超时错误，并附有处理建议。
