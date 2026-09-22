# Fiddler Classic CLI 与 MCP 服务器

[English (en-US)](README.md) | **简体中文 (zh-CN)**

Fiddler Classic CLI 为 Fiddler Classic 5.x 和 6.x 提供非官方的 CLI 与 MCP 集成，仅支持 Windows。它通过轻量级进程内扩展、当前用户专用的命名管道和共享的 .NET 主程序提供脚本化访问，抓包仍由 Fiddler Classic 负责。

本项目与 Progress Telerik 没有关联，也不受其官方支持。项目使用 Fiddler Classic 文档中的 [.NET 扩展接口](https://www.telerik.com/fiddler/fiddler-classic/documentation/extend-fiddler/interfaces)，并遵循已发布 [Fiddler 插件](https://www.telerik.com/fiddler/add-ons)的部署约定。

每项公开功能都归类为原生、原生适配、自定义、混合或主程序功能。完整分类表及与 Fiddler 原生行为保持一致的要求见[功能类型与原生兼容性](docs/zh-CN/feature-types.md)。

## 系统要求

- Windows x64
- Fiddler Classic 5.x 或 6.x
- 执行抓包和会话操作时，Fiddler 必须正在运行
- 仅从源码构建时需要 .NET SDK 10；发布版本已包含运行时

桥接项目在构建时引用本机安装的 `Fiddler.exe`，该文件不会提交到仓库或包含在发布包中。

## 安装

### 发布包

每个发布版本包含 `fiddler-classic-win-x64.zip` 和 `SHA256SUMS`。请先根据清单校验 ZIP，再解压，并在解压目录中运行随附的当前用户安装脚本：

```powershell
./install.ps1
fiddler-classic --version
```

安装脚本会将自包含程序复制到 `%LOCALAPPDATA%\Programs\FiddlerClassicCLI\<版本>`，并更新当前用户的 `PATH`。Agent 工作流也可用它安装本地发布 ZIP，或从可信 GitHub 仓库下载发布版本并校验。本地包用法和私有仓库认证方式见[安装指南](docs/zh-CN/installation.md)。

安装 Fiddler 扩展、重启 Fiddler Classic，然后验证连接：

```powershell
fiddler-classic bridge install
fiddler-classic doctor
fiddler-classic status
```

### 从源码构建

运行构建脚本，完成源码构建、测试和发布：

```powershell
./scripts/build.ps1
```

脚本将自包含发布文件写入 `artifacts/publish/win-x64`。使用以下命令为当前用户安装：

```powershell
./artifacts/publish/win-x64/install.ps1
```

`bridge install` 命令只会将以下文件复制到 `%USERPROFILE%\Documents\Fiddler2\Scripts`：

- `FiddlerClassic.Bridge.dll`
- `FiddlerClassic.Protocol.dll`

该命令还会在 `%LOCALAPPDATA%\FiddlerClassicCLI\bridge-host.json` 中记录已安装主程序的可执行文件和版本。重启 Fiddler 后，可通过 **Fiddler Classic CLI** 标签页或 Tools 菜单中的快捷入口管理 MCP HTTP 访问。使用 `bridge uninstall` 删除扩展文件和启动记录。卸载命令会在交互式终端中要求确认；stdin 被重定向时必须传递 `--yes`。

## CLI

```text
fiddler-classic doctor
fiddler-classic status [--json]
fiddler-classic app detect|open|close|restart
fiddler-classic capture start|stop
fiddler-classic sessions list [过滤参数] [--summary]
fiddler-classic sessions watch [过滤参数] [--after-id ID] [--timeout 秒] [--count N] [--jsonl]
fiddler-classic sessions show <session-id>
fiddler-classic sessions body <session-id> --direction request|response --output <路径|->
fiddler-classic sessions clear [--yes]
fiddler-classic sessions remove --ids 1 2 [--yes]
fiddler-classic sessions save <绝对路径.saz> [--ids 1 2] [--overwrite] [--yes]
fiddler-classic sessions load <绝对路径.saz>
fiddler-classic sessions replay <session-id> [--unconditional] [--wait] [--timeout 秒]
fiddler-classic sessions export [session-id] --format curl|raw-http|har --output <路径|->
fiddler-classic sessions diff <左侧ID> <右侧ID>
fiddler-classic sessions websocket <session-id> list|get
fiddler-classic request send <url> [-X METHOD] [-H "Name: value"] [--body 文本|--body-file 路径] [--wait]
fiddler-classic autoresponder status|configure
fiddler-classic autoresponder rules list|add|update|move|remove|clear|save|load
fiddler-classic breakpoints status|arms|arm|disarm|list|wait|show|update|resume|abort
fiddler-classic bridge install|uninstall
fiddler-classic daemon start|status|stop
fiddler-classic mcp stdio|http
fiddler-classic mcp service status|configure|enable|disable
fiddler-classic mcp clients list|authorize|deauthorize
fiddler-classic mcp connections list|disconnect
fiddler-classic config token show|rotate
```

用 `app detect` 查找已有的 Fiddler 安装和进程。`app open` 使用 `-noattach` 启动 Fiddler，已有实例则保持不变。`app close` 和 `app restart` 需要确认（脚本需传入 `--yes`），执行前请保存需要的捕获记录。命令请求正常关闭，不会强制终止 Fiddler。自定义路径、PID 选择和超时说明见[应用程序命令](docs/zh-CN/cli.md#fiddler-应用程序)。

需要桥接的 CLI 命令会自动启动持续运行的后台守护进程，并通过仅限当前用户访问的 Windows 命名管道与其通信。安装后的扩展也会在 Fiddler 加载时启动或发现该守护进程，后续客户端复用同一个进程。可使用 `daemon start`、`daemon status` 和 `daemon stop` 显式管理守护进程，详见 [CLI 指南](docs/zh-CN/cli.md)。

`sessions list`、`sessions watch` 和 HAR 导出共用过滤参数，可按 ID、方法、主机、URL、状态码、响应 MIME 类型、进程、标头名称和值、耗时、HTTP 协议、正文总大小、错误状态，以及请求或响应正文内容筛选。正文搜索必须显式启用，按 UTF-8 字节精确匹配。默认检查每个会话的前 64 KiB，最多检查 1 MiB。列表默认返回 100 个会话，最多 1,000 个。

`sessions show` 返回元数据和原始标头名称/值，保留原始顺序，不返回正文。`sessions body` 以 256 KiB 分块流式输出完整原始载荷。输出到 `-` 时，stdout 只包含正文字节，状态信息和错误写入 stderr。

`sessions watch` 持续输出刚完成的会话。`sessions replay --wait` 和 `request send --wait` 会在操作前的基准 ID 之后等待首个匹配的已完成会话，并将其与操作关联。`sessions remove` 只删除显式指定的 ID。导出和比较结果保留敏感证据：cURL 与原始 HTTP 用于复现单个请求，HAR 用于导出过滤后的集合，diff 比较元数据、原始标头、耗时和正文哈希。WebSocket 载荷与帧元数据分开，以分块方式流式输出。

`autoresponder` 控制 Fiddler 正在运行的 AutoResponder 引擎。规则使用运行时 ID，保留 Fiddler 的求值顺序，并接受原生匹配与操作字符串。FARX 导入会追加规则；`--replace --yes` 会替换整个列表。保存时必须使用绝对 `.farx` 路径，替换现有文件需要 `--overwrite --yes`。

`breakpoints arm request|response` 可按方法、主机、URL、标头、进程，以及仅适用于响应的状态码/内容类型条件暂停后续流量。触发器默认为一次性，传递 `--persistent` 后可持续生效。托管暂停默认在 30 秒后自动继续，保持时间可配置为 1-300 秒。待处理断点可以检查、修改当前阶段允许的字段、继续或显式中止。

`sessions list --summary` 按主机和状态码汇总一个有条数上限的元数据页，并提供已捕获的正文字节总数及已完成请求的耗时统计。`doctor --output C:\Temp\fiddler-diagnostics.json` 会创建仅含元数据的诊断报告，不会启动守护进程。统计范围、排除字段和输出规则见 [CLI 指南](docs/zh-CN/cli.md)。

### 退出码

| 代码 | 含义 |
| ---: | --- |
| 0 | 成功 |
| 1 | 未预期的失败 |
| 2 | 无效输入或协议版本不匹配 |
| 3 | 未安装 Fiddler |
| 4 | Fiddler、桥接或 CLI 守护进程不可用 |
| 5 | 操作被拒绝、目标不存在、发生冲突或缺少确认 |
| 6 | 桥接、CLI 守护进程或等待 Fiddler 应用程序退出超时 |

## MCP

主程序使用官方 C# SDK 2.2.0，通过 stdio 和 Streamable HTTP 支持 MCP 协议版本 `2026-07-28`，同时接受 `2025-11-25` 和 `2025-06-18` 的初始化握手。MCP 协议以日期标识版本，"v2" 是 SDK 的主版本号。请求要求和错误说明见[协议兼容性](docs/zh-CN/cli.md#mcp-协议)。

### 标准输入输出

MCP 客户端配置示例：

```json
{
  "mcpServers": {
    "fiddler-classic": {
      "command": "C:\\path\\to\\fiddler-classic.exe",
      "args": ["mcp", "stdio"]
    }
  }
}
```

stdio 传输只将 JSON-RPC 协议消息写入 stdout，主程序诊断信息写入 stderr。

### Streamable HTTP

```powershell
./fiddler-classic.exe mcp http
```

前台服务器采用无状态模式，仅绑定回环地址。两种 HTTP 模式均对包含 `Origin` 标头的请求返回 HTTP 403，不允许浏览器访问，也不启用 CORS。默认端点为 `http://127.0.0.1:8877/mcp`，请求必须携带以下授权头：

```http
Authorization: Bearer <token>
```

使用 `config token show` 查看默认令牌，使用 `config token rotate` 替换令牌。前台和托管 HTTP 监听器都接受默认凭据与命名客户端凭据，并在凭据轮换或撤销后的后续请求中重新加载。守护进程可以运行持续提供服务的托管监听器：

```powershell
fiddler-classic mcp service configure --bind loopback --port 8877
fiddler-classic mcp service enable
fiddler-classic mcp clients authorize --name "Local agent"
fiddler-classic mcp connections list
```

命名客户端令牌只显示一次，配置中只保存其 SHA-256 哈希。将托管服务绑定到 `0.0.0.0` 必须明确确认，因为 Bearer 凭据通过明文 HTTP 传输，任何监听到凭据的人都可以重复使用。本项目不会配置 TLS、防火墙规则或 CORS。配置保存在 `%LOCALAPPDATA%\FiddlerClassicCLI\config.json`，由仅允许当前用户访问的 ACL 保护。

管理标签页带有终端图标。面板变窄时按钮会自动换行，刷新时会保留编辑内容和选中项。控件支持键盘操作，回环地址和可用局域网地址提示各有独立的复制按钮。[托管 HTTP 控制](docs/zh-CN/cli.md#托管-mcp-http)说明了如何应用设置和处理超时，并列出地址限制。

### 检查工具

MCP 接口采用可组合的小型列表和详情调用，沿用 [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp) 的模式，并适配 Fiddler 的进程级会话模型。工具工作流和差异说明见[设计文档](docs/zh-CN/design.md)。

- `get_status`
- `start_capture`
- `stop_capture`
- `list_network_requests`
- `summarize_network_requests`
- `wait_for_network_request`
- `get_network_request`
- `get_network_request_body`
- `clear_network_requests`
- `remove_network_requests`
- `save_network_archive`
- `load_network_archive`
- `replay_network_request`
- `send_request`
- `diff_network_requests`
- `list_websocket_messages`
- `get_websocket_message`

还有更多匹配项时，`list_network_requests` 会返回稳定的 `nextMaxRequestId` 或 `nextMinRequestId` 续传边界。`wait_for_network_request` 最多等待 60 秒，只匹配指定请求 ID 之后的请求，不含该 ID。请求详情默认不包含标头，只有 `includeHeaders=true` 时才返回。HTTP 正文和 WebSocket 载荷只能通过对应的限长载荷工具读取，每次 MCP 调用最多返回 64 KiB，并明确提供文本或 Base64 编码元数据。清空、选择性删除和替换归档必须传递明确的确认参数。

### AutoResponder 工具

- `get_autoresponder_status`
- `configure_autoresponder`
- `list_autoresponder_rules`
- `add_autoresponder_rule`
- `update_autoresponder_rule`
- `move_autoresponder_rule`
- `remove_autoresponder_rule`
- `clear_autoresponder_rules`
- `save_autoresponder_rules`
- `load_autoresponder_rules`

规则修改工具带有开放世界注解，因为原生 Fiddler 操作可以重定向、生成、延迟或丢弃流量，也可以从本地文件读取响应。删除、清空、文件替换和完整列表替换带有破坏性注解，并要求明确的确认参数。

### 断点工具

- `list_network_breakpoint_arms`
- `arm_network_breakpoint`
- `disarm_network_breakpoint`
- `list_pending_network_breakpoints`
- `wait_for_network_breakpoint`
- `get_network_breakpoint`
- `update_network_breakpoint`
- `resume_network_breakpoint`
- `abort_network_breakpoint`

断点列表和详情不包含正文。通过 `get_network_request_body` 读取暂停中的载荷，仅在需要替换正文时向 `update_network_breakpoint` 传递完整的 Base64 正文。中止操作要求 `confirm=true`。

## Agent Skills

源码仓库和发布目录包含英文 Agent Skill 包 `skills/fiddler-classic-cli`。该包遵循 `SKILL.md` 约定，`agents/openai.yaml` 包含发现该 Skill 所需的元数据。Skill 随项目发布时会定位根目录中共享的 `install.ps1`，并指引 CLI 任务查阅简短的参考文档，内容包括诊断与应用程序启动、关闭和重启、流量检查、会话操作、AutoResponder 和断点命令。

## 安全

抓包流量是原始证据。桥接不会脱敏、标准化或静默转换标头与正文。显式输出可能包含密码、Cookie、Bearer 令牌、API 密钥和个人数据。请将终端输出、MCP 对话记录、正文文件和 SAZ 归档作为敏感证据保护。

- 桥接和 CLI 守护进程的命名管道仅允许当前 Windows 用户访问。
- MCP HTTP 默认禁用并仅绑定回环地址。绑定到 IPv4 `0.0.0.0` 前必须明确确认明文凭据风险。
- 每个 HTTP 请求都必须携带 256 位 Bearer 凭据。命名客户端令牌只保存 SHA-256 哈希。默认 CLI 令牌为兼容而保留，仍可在当前用户 ACL 保护下读取。
- 输入校验会检查标头名称和值，并拒绝 CRLF 注入。
- 协议帧、请求正文、结果数量和 MCP 正文分块均有上限。
- 托管断点触发器默认为一次性，并在有限等待时间后自动继续暂停的流量。
- 桥接会原样保留 AutoResponder 操作字符串。这些操作可以影响网络流量或访问当前用户有权读取的路径。
- `capture start|stop` 只负责将 Fiddler 挂接为系统代理或取消挂接。
- 本项目不会安装或信任根证书。

操作指南见 [SECURITY.zh-CN.md](SECURITY.zh-CN.md)。

## 开发

```powershell
dotnet build ./FiddlerClassicCLI.slnx
dotnet test ./tests/FiddlerClassic.Tests/FiddlerClassic.Tests.csproj
dotnet publish ./src/FiddlerClassic.Host/FiddlerClassic.Host.csproj -c Release -p:PublishProfile=win-x64
./scripts/package.ps1
```

如果 Fiddler 安装在其他目录，请传递 `-p:FiddlerInstallDir="C:\path\to\Fiddler"`。

## 许可证

Copyright 2026 SpecterShell。本项目采用 [Apache License 2.0](LICENSE) 许可证。
