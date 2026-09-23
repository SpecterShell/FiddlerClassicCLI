# Fiddler Classic CLI 与 MCP 服务器

[English (en-US)](README.md) | **简体中文 (zh-CN)**

Fiddler Classic CLI 为 Fiddler Classic 5.x 和 6.x 提供非官方的 CLI 与 MCP 集成，仅支持 Windows。它通过轻量级进程内扩展、当前用户专用的命名管道和共享的 .NET 主程序提供脚本化访问，抓包仍由 Fiddler Classic 负责。

本项目与 Progress Telerik 没有关联，也不受其官方支持。项目使用 Fiddler Classic 文档中的 [.NET 扩展接口](https://www.telerik.com/fiddler/fiddler-classic/documentation/extend-fiddler/interfaces)，并遵循已发布 [Fiddler 插件](https://www.telerik.com/fiddler/add-ons)的部署约定。

每项公开功能都归类为原生、原生适配、自定义、混合或主程序功能。完整分类表及与 Fiddler 原生行为保持一致的要求见[功能类型与原生兼容性](docs/zh-CN/feature-types.md)。

## 系统要求

- Windows x64
- Fiddler Classic 5.x 或 6.x
- 执行抓包和会话操作时，Fiddler 必须正在运行
- 仅从源码构建时需要 .NET SDK 10。发布版本已包含运行时。

桥接项目在构建时引用本机安装的 `Fiddler.exe`，该文件不会提交到仓库或包含在发布包中。

## 安装

### 最新发布版本

在 PowerShell 中运行以下命令，下载并执行仓库的 `scripts/install.ps1`。请先检查[脚本内容](https://github.com/SpecterShell/FiddlerClassicCLI/blob/main/scripts/install.ps1)，仅使用可信来源：

```powershell
irm https://raw.githubusercontent.com/SpecterShell/FiddlerClassicCLI/main/scripts/install.ps1 | iex
```

引导脚本从 `SpecterShell/FiddlerClassicCLI` 的最新 GitHub 发布版本下载 `fiddler-classic-cli.exe`，根据 GitHub 提供的文件 SHA-256 摘要进行验证，并安装到固定目录 `%LOCALAPPDATA%\Programs\FiddlerClassicCLI`。摘要缺失或不匹配时，安装会停止。默认会更新当前用户的 `PATH` 并安装 Fiddler 桥接。安装不会启动 Fiddler、守护进程或 MCP 服务器，也不会安装 Agent Skills。

每个发布版本只提供一个文件：`fiddler-classic-cli.exe`。其中嵌入了 .NET 运行时、托管依赖、两个桥接 DLL 和 `LICENSE`。.NET 默认将原生运行库文件提取到 `%TEMP%\.net`。使用 `scripts/install.ps1` 安装 CLI。本地 EXE、ZIP 或目录来源、独立运行、版本选择、自定义目录和跳过桥接的用法见[安装指南](docs/zh-CN/installation.md)。

安装后，请显式启动或重启 Fiddler 以加载桥接。首次启动时，Fiddler 可能会为安装的两个 DLL 分别弹出 "Caution: Unverified Extension Detected" 窗口。请逐一检查提示，仅在信任文件来源时允许加载。各选项的说明见[首次启动授权](docs/zh-CN/installation.md#fiddler-桥接)。处理完提示后，再从新终端验证连接：

```powershell
fiddler-classic-cli doctor
fiddler-classic-cli status
```

### 从源码构建

运行构建脚本，完成源码构建、测试和发布：

```powershell
./scripts/build.ps1
```

脚本仅向 `artifacts/publish/win-x64` 写入 `fiddler-classic-cli.exe`。`scripts/package.ps1` 构建并验证该程序，再将它复制到 `artifacts/release`，作为唯一的发布文件。使用以下命令为当前用户安装本地构建：

```powershell
./scripts/install-local.ps1
```

该脚本使用所在仓库中已有的发布输出，并调用 `scripts/install.ps1`，不会下载发布版本或重新构建。支持 `-InstallDirectory`、`-NoPathUpdate`、`-SkipBridge` 和 `-Json`。隔离安装示例见[本地开发安装](docs/zh-CN/installation.md#本地开发)。

安装脚本通过 `bridge install` 将嵌入的 `FiddlerClassicCLI.Bridge.dll` 与 `FiddlerClassicCLI.Protocol.dll` 部署到 `%USERPROFILE%\Documents\Fiddler2\Scripts`，并在 `%LOCALAPPDATA%\FiddlerClassicCLI\bridge-host.json` 中记录主程序路径和版本。使用 `scripts/install.ps1 -SkipBridge` 可仅安装 CLI。也可单独运行 `fiddler-classic-cli bridge install` 安装或修复桥接。

重启 Fiddler 后，可通过 **Fiddler Classic CLI** 标签页或 Tools 菜单中的快捷入口管理 MCP HTTP 访问。`bridge uninstall` 删除扩展文件和启动记录。卸载命令会在交互式终端中要求确认，stdin 被重定向时必须传递 `--yes`。

## CLI

单独运行 `fiddler-classic-cli` 或命令组会显示相应帮助，与 `--help` 相同。缺少必需输入时，CLI 会显示命令用法，不执行操作。使用 `--json` 可获得结构化错误。输出流和退出码详见[命令用法](docs/zh-CN/cli.md#命令用法)。

```text
fiddler-classic-cli doctor
fiddler-classic-cli status [--json]
fiddler-classic-cli app detect|open|close|restart
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

用 `app detect` 查找已有的 Fiddler 安装和进程。`app open` 使用 `-noattach` 启动 Fiddler，已有实例则保持不变。`app close` 和 `app restart` 需要确认（脚本需传入 `--yes`），执行前请保存需要的捕获记录。命令请求正常关闭，不会强制终止 Fiddler。自定义路径、PID 选择和超时说明见[应用程序命令](docs/zh-CN/cli.md#fiddler-应用程序)。

需要桥接的 CLI 命令会自动启动持续运行的后台守护进程，并通过仅限当前用户访问的 Windows 命名管道与其通信。安装后的扩展也会在 Fiddler 加载时启动或发现该守护进程，后续客户端复用同一个进程。可使用 `daemon start`、`daemon status` 和 `daemon stop` 显式管理守护进程，详见 [CLI 指南](docs/zh-CN/cli.md)。

`capture start` 将 Fiddler 挂接为运行它的 Windows 主机的系统代理，`capture stop` 取消挂接。无论是否挂接，正在运行的 Fiddler 代理监听器都可以接收显式路由到它的流量。本机客户端可使用回环地址。远程客户端需要在 Fiddler 中配置远程访问，使其监听可达接口或所有接口，并确保网络路由和防火墙允许连接。IPv4 `0.0.0.0` 是监听所有接口的绑定地址，客户端应使用具体的回环地址或主机地址，并指定代理端口。这些 Fiddler 代理设置独立于 MCP HTTP 绑定。抓包命令保留证书信任和 HTTPS 解密设置。

`sessions list`、`sessions watch` 和 HAR 导出共用过滤参数，可按 ID、方法、主机、URL、状态码、响应 MIME 类型、进程、标头名称和值、耗时、HTTP 协议、正文总大小、错误状态，以及请求或响应正文内容筛选。正文搜索必须显式启用，按 UTF-8 字节精确匹配。默认检查每个会话的前 64 KiB，最多检查 1 MiB。列表默认返回 100 个会话，最多 1,000 个。

`sessions show` 返回元数据和原始标头名称/值，保留原始顺序，不返回正文。`sessions body` 以 256 KiB 分块流式输出完整原始载荷。输出到 `-` 时，stdout 只包含正文字节，状态信息和错误写入 stderr。

`sessions watch` 持续输出刚完成的会话。`sessions replay --wait` 和 `request send --wait` 会在操作前的基准 ID 之后等待首个匹配的已完成会话，并将其与操作关联。`sessions remove` 只删除显式指定的 ID。导出和比较结果保留敏感证据：cURL 与原始 HTTP 用于复现单个请求，HAR 用于导出过滤后的集合，diff 比较元数据、原始标头、耗时和正文哈希。WebSocket 载荷与帧元数据分开，以分块方式流式输出。

`autoresponder` 控制 Fiddler 正在运行的 AutoResponder 引擎。规则使用运行时 ID，保留 Fiddler 的求值顺序，并接受原生匹配与操作字符串。FARX 导入会追加规则。`--replace --yes` 会替换整个列表。保存时必须使用绝对 `.farx` 路径，替换现有文件需要 `--overwrite --yes`。

`breakpoints arm request|response` 可按方法、主机、URL、标头、进程，以及仅适用于响应的状态码/内容类型条件暂停后续流量。触发器默认为一次性，传递 `--persistent` 后可持续生效。托管暂停默认在 30 秒后自动继续，保持时间可配置为 1-300 秒。待处理断点可以检查、修改当前阶段允许的字段、继续或显式中止。

`sessions list --summary` 按主机和状态码汇总一个有条数上限的元数据页，并提供已捕获的正文字节总数及已完成请求的耗时统计。`doctor --output C:\Temp\fiddler-diagnostics.json` 会创建仅含元数据的诊断报告，不会启动守护进程。统计范围、排除字段和输出规则见 [CLI 指南](docs/zh-CN/cli.md)。

### 退出码

| 代码 | 含义 |
| ---: | --- |
| 0 | 成功或显示帮助 |
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
    "fiddler-classic-cli": {
      "command": "C:\\path\\to\\fiddler-classic-cli.exe",
      "args": ["mcp", "stdio"]
    }
  }
}
```

stdio 传输只将 JSON-RPC 协议消息写入 stdout，主程序诊断信息写入 stderr。

### Streamable HTTP

```powershell
./fiddler-classic-cli.exe mcp http
```

前台服务器采用无状态模式，仅绑定回环地址。两种 HTTP 模式均对包含 `Origin` 标头的请求返回 HTTP 403，不允许浏览器访问，也不启用 CORS。默认端点为 `http://127.0.0.1:8877/mcp`，请求必须携带以下授权头：

```http
Authorization: Bearer <token>
```

使用 `config token show` 查看默认令牌，使用 `config token rotate` 替换令牌。前台和托管 HTTP 监听器都接受默认凭据与命名客户端凭据，并在凭据轮换或撤销后的后续请求中重新加载。守护进程可以运行持续提供服务的托管监听器：

```powershell
fiddler-classic-cli mcp service configure --bind loopback --port 8877
fiddler-classic-cli mcp service enable
fiddler-classic-cli mcp clients authorize --name "Local agent"
fiddler-classic-cli mcp connections list
```

命名客户端令牌只显示一次，配置中只保存其 SHA-256 哈希。将托管服务绑定到 `0.0.0.0` 必须明确确认，因为 Bearer 凭据通过明文 HTTP 传输，任何监听到凭据的人都可以重复使用。本项目不会配置 TLS、防火墙规则或 CORS。配置保存在 `%LOCALAPPDATA%\FiddlerClassicCLI\config.json`，由仅允许当前用户访问的 ACL 保护。

管理标签页带有终端图标。面板变窄时操作按钮会自动换行，刷新时会保留编辑内容和选中项。控件支持键盘操作，每个回环或局域网 URL 旁都有剪贴板复制按钮，服务状态文字旁有彩色状态灯。[托管 HTTP 控制](docs/zh-CN/cli.md#托管-mcp-http)说明了如何应用设置和处理超时，并列出地址限制。

独立的只读 [Named pipes 区域](docs/zh-CN/installation.md#命名管道)显示桥接和守护进程的管道路径、监听器状态与状态检查结果、协议版本及守护进程详情，并提供路径复制按钮。**Refresh pipes** 仅读取状态，不会启动守护进程或更改监听器。

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

源码仓库包含英文 Agent Skill 包 `skills/fiddler-classic-cli`。该包遵循 `SKILL.md` 约定，`agents/openai.yaml` 包含发现该 Skill 所需的元数据。CLI 缺失时，Skill 会在获得授权后通过 `scripts/install.ps1` 下载最新的可执行文件。请从源码仓库获取 Skill 并单独配置，CLI 安装不会将 Skill 复制到 Agent 的全局配置。Skill 指引 CLI 任务查阅简短的参考文档，内容包括安装、诊断与应用程序启动、关闭和重启、流量检查、会话操作、AutoResponder 和断点命令。

## 安全

抓包流量是原始证据。桥接不会脱敏、标准化或静默转换标头与正文。显式输出可能包含密码、Cookie、Bearer 令牌、API 密钥和个人数据。请将终端输出、MCP 对话记录、正文文件和 SAZ 归档作为敏感证据保护。

- 桥接和 CLI 守护进程的命名管道仅允许当前 Windows 用户访问。
- MCP HTTP 默认禁用并仅绑定回环地址。绑定到 IPv4 `0.0.0.0` 前必须明确确认明文凭据风险。
- 每个 HTTP 请求都必须携带 256 位 Bearer 凭据。命名客户端令牌只保存 SHA-256 哈希。默认 CLI 令牌为兼容而保留，仍可在当前用户 ACL 保护下读取。
- 输入校验会检查标头名称和值，并拒绝 CRLF 注入。
- 协议帧、请求正文、结果数量和 MCP 正文分块均有上限。
- 托管断点触发器默认为一次性，并在有限等待时间后自动继续暂停的流量。
- 桥接会原样保留 AutoResponder 操作字符串。这些操作可以影响网络流量或访问当前用户有权读取的路径。
- `capture start|stop` 只负责将 Fiddler 挂接为运行它的 Windows 主机的系统代理或取消挂接。取消挂接后，显式路由的流量仍可到达其运行中的代理监听器。
- 本项目不会安装或信任根证书。

操作指南见 [SECURITY.zh-CN.md](SECURITY.zh-CN.md)。

## 开发

```powershell
dotnet build ./FiddlerClassicCLI.slnx
dotnet test ./tests/FiddlerClassicCLI.Tests/FiddlerClassicCLI.Tests.csproj
dotnet publish ./src/FiddlerClassicCLI.Host/FiddlerClassicCLI.Host.csproj -c Release -p:PublishProfile=win-x64
./scripts/package.ps1
```

如果 Fiddler 安装在其他目录，请传递 `-p:FiddlerInstallDir="C:\path\to\Fiddler"`。

## 许可证

Copyright 2026 SpecterShell。本项目采用 [Apache License 2.0](LICENSE) 许可证。
