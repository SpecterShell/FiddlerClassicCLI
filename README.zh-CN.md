# Fiddler Classic CLI 与 MCP 服务器

[English (en-US)](README.md) | **简体中文 (zh-CN)**

这是一个非官方、仅支持 Windows 的 Fiddler Classic 5.x CLI 与 MCP 集成。它继续使用 Fiddler Classic 作为抓包引擎，并通过轻量级进程内扩展、当前用户专用的命名管道和统一的 .NET 主程序提供脚本化访问。

本项目与 Progress Telerik 没有关联，也不受其官方支持。项目遵循 Fiddler Classic 已公开的 [.NET 扩展接口](https://www.telerik.com/fiddler/fiddler-classic/documentation/extend-fiddler/interfaces)，并采用已发布 [Fiddler 插件](https://www.telerik.com/fiddler/add-ons)使用的部署约定。

每项公开能力都标记为原生、原生适配、自定义、混合或主程序功能。完整矩阵和一致性约定见[功能类型与原生兼容性](docs/zh-CN/feature-types.md)。

## 系统要求

- Windows x64
- Fiddler Classic 5.x
- 执行抓包和会话操作时，Fiddler 必须正在运行
- 仅从源码构建时需要 .NET SDK 10；发布版本已自包含运行时

桥接项目会引用本机安装的 `Fiddler.exe`，但不会将该文件复制到项目或发布产物中。

## 安装

### 发布包

每个发布版本包含 `fiddler-classic-win-x64.zip` 和 `SHA256SUMS`。请先根据清单校验 ZIP，再解压，并在解压目录中运行随附的当前用户安装脚本：

```powershell
./install.ps1
fiddler-classic --version
```

安装脚本会将自包含程序复制到 `%LOCALAPPDATA%\Programs\FiddlerClassicCLI\<版本>`，并更新当前用户的 `PATH`。它也可以为 Agent 工作流安装本地发布 ZIP，或从可信 GitHub 仓库下载并校验发布版本。本地包用法和私有仓库认证方式详见[安装指南](docs/zh-CN/installation.md)。

安装 Fiddler 扩展、重启 Fiddler Classic，然后验证连接：

```powershell
fiddler-classic bridge install
fiddler-classic doctor
fiddler-classic status
```

### 从源码构建

从源码构建、测试并发布：

```powershell
./scripts/build.ps1
```

自包含发布目录为 `artifacts/publish/win-x64`。可使用以下命令将其安装到当前用户：

```powershell
./artifacts/publish/win-x64/install.ps1
```

`bridge install` 命令只会将以下文件复制到 `%USERPROFILE%\Documents\Fiddler2\Scripts`：

- `FiddlerClassic.Bridge.dll`
- `FiddlerClassic.Protocol.dll`

使用 `bridge uninstall` 删除它们。卸载时会在交互式终端中要求确认；当 stdin 被重定向时必须传递 `--yes`。

## CLI

```text
fiddler-classic doctor
fiddler-classic status [--json]
fiddler-classic capture start|stop
fiddler-classic sessions list [过滤参数]
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
fiddler-classic config token show|rotate
```

依赖桥接的 CLI 命令会自动启动持久后台守护进程，并通过仅限当前用户访问的 Windows 命名管道与其通信。后续 CLI 调用会复用同一个进程。可使用 `daemon start`、`daemon status` 和 `daemon stop` 显式管理；详见 [CLI 指南](docs/zh-CN/cli.md)。

`sessions list`、`sessions watch` 和 HAR 导出共享过滤参数，可按 ID、方法、主机、URL、状态码、响应 MIME 类型、进程、请求头名称和值、耗时、HTTP 协议、正文总大小、错误状态，以及请求或响应正文内容筛选。正文搜索必须显式启用，按 UTF-8 字节精确匹配，默认检查每个会话前 64 KiB，最大为 1 MiB。列表默认返回 100 个会话，最多 1,000 个。

`sessions show` 返回元数据和保持原始顺序的请求头名称/值，不返回正文。`sessions body` 以 256 KiB 分块流式输出完整原始载荷。输出到 `-` 时，stdout 只用于正文数据，状态和错误写入 stderr。

`sessions watch` 会在会话完成时持续输出。`sessions replay --wait` 和 `request send --wait` 会从操作前的基准 ID 开始等待首个匹配的已完成会话。`sessions remove` 只删除显式指定的 ID。导出和比较结果会保留敏感证据：cURL 与原始 HTTP 用于复现单个请求，HAR 用于导出过滤后的集合，diff 比较元数据、原始请求头、耗时和正文哈希。WebSocket 帧元数据与载荷分离，载荷以分块方式读取。

`autoresponder` 控制 Fiddler 当前的 AutoResponder 引擎。规则使用运行时 ID，保留 Fiddler 的求值顺序，并接受原生匹配与操作字符串。FARX 导入会追加规则；`--replace --yes` 会替换整个列表。保存路径必须是绝对 `.farx` 路径，替换现有文件需要 `--overwrite --yes`。

`breakpoints arm request|response` 可按方法、主机、URL、请求头、进程，以及仅适用于响应的状态码/内容类型条件暂停后续流量。默认创建一次性触发器，传递 `--persistent` 可持续生效。托管断点默认在 30 秒后自动继续，可配置为 1-300 秒。待处理断点可以检查、按阶段修改、继续或显式中止。

### 退出码

| 代码 | 含义 |
| ---: | --- |
| 0 | 成功 |
| 1 | 未预期的失败 |
| 2 | 无效输入或协议版本不匹配 |
| 3 | 未安装 Fiddler |
| 4 | Fiddler、桥接或 CLI 守护进程不可用 |
| 5 | 操作被拒绝、目标不存在、发生冲突或缺少确认 |
| 6 | 桥接或 CLI 守护进程超时 |

## MCP

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

默认端点为 `http://127.0.0.1:8877/mcp`。它采用无状态模式，仅绑定回环地址，不启用 CORS，并要求：

```http
Authorization: Bearer <token>
```

使用 `config token show` 查看生成的令牌，使用 `config token rotate` 替换令牌。配置保存在 `%LOCALAPPDATA%\FiddlerClassicCLI\config.json`，并应用仅限当前用户访问的 ACL。

### 检查工具

MCP 接口采用 [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp) 的小型、可组合列表/详情模式，并针对 Fiddler 的进程级会话模型进行了调整。工具工作流和差异说明见[设计文档](docs/zh-CN/design.md)。

- `get_status`
- `start_capture`
- `stop_capture`
- `list_network_requests`
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

当还有更多匹配项时，`list_network_requests` 会返回稳定的 `nextMaxRequestId` 或 `nextMinRequestId` 续传边界。`wait_for_network_request` 可从排他的请求 ID 之后等待最多 60 秒。请求详情默认不包含请求头，只有 `includeHeaders=true` 时才返回。HTTP 正文和 WebSocket 载荷只能通过对应的限长载荷工具读取，每次 MCP 调用最多 64 KiB，并包含明确的文本或 Base64 编码元数据。清空、选择性删除和替换归档必须传递明确的确认参数。

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

规则修改工具标注为开放世界操作，因为原生 Fiddler 操作可以重定向、生成、延迟、丢弃流量，或从本地文件读取响应。删除、清空、文件替换和完整列表替换使用破坏性注解与明确确认参数。

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

断点列表和详情不包含正文。通过 `get_network_request_body` 读取暂停中的载荷；只有确实要替换时，才向 `update_network_breakpoint` 传递完整 Base64 正文。中止操作要求 `confirm=true`。

## Agent Skills

源码仓库和发布目录包含英文 Agent Skill 包 `skills/fiddler-classic-cli`。该包遵循 `SKILL.md` 约定，并包含用于发现的 `agents/openai.yaml` 元数据。Skill 可从相邻发布目录、本地发布包或可信 GitHub 发布版本引导安装缺失的 CLI，并涵盖流量检查、正文分块、守护进程与桥接诊断、代理状态安全和敏感证据处理。

## 安全

抓包流量是原始证据。桥接不会脱敏、标准化或静默转换请求头与正文。显式输出可能包含密码、Cookie、Bearer 令牌、API 密钥和个人数据。请妥善处理终端输出、MCP 对话记录、正文文件和 SAZ 归档。

- 桥接和 CLI 守护进程的命名管道仅允许当前 Windows 用户访问。
- HTTP 仅绑定 `127.0.0.1`，并使用保存在当前用户 ACL 下的 256 位 Bearer 令牌。
- 请求头名称和值会经过校验，并拒绝 CRLF 注入。
- 协议帧、请求正文、结果数量和 MCP 正文分块都有大小限制。
- 托管断点触发器默认为一次性，并在有限等待时间后自动继续暂停的流量。
- AutoResponder 操作字符串会被原样保留，可以影响网络流量或访问当前用户有权读取的路径。
- `capture start|stop` 只负责将 Fiddler 挂接为系统代理或取消挂接。
- 本项目不会安装或信任根证书。

操作指南见 [SECURITY.zh-CN.md](SECURITY.zh-CN.md)。

## 许可证

Copyright 2026 SpecterShell。本项目采用 [Apache License 2.0](LICENSE) 许可证。

## 开发

```powershell
dotnet build ./FiddlerClassicCLI.slnx
dotnet test ./tests/FiddlerClassic.Tests/FiddlerClassic.Tests.csproj
dotnet publish ./src/FiddlerClassic.Host/FiddlerClassic.Host.csproj -c Release -p:PublishProfile=win-x64
./scripts/package.ps1
```

如果 Fiddler 安装在其他目录，请传递 `-p:FiddlerInstallDir="C:\path\to\Fiddler"`。

[构建与发布工作流](.github/workflows/build-release.yml)会在拉取请求、推送到 `main`、版本标签和手动触发时运行。它会在 `windows-2025` 运行器上安装固定版本的 Fiddler Classic 编译引用，优先使用 WinGet，并在 WinGet 不可用或安装失败时改用 Chocolatey。随后，工作流会执行全部自动化测试，创建自包含发布包，验证校验值和必要文件，并上传两个发布文件。匹配 `v*` 的标签会将已测试文件发布为 GitHub Release；带有预发布后缀的标签（例如 `v0.3.0-preview.1`）会创建预发布版本。

工作流只将 Fiddler 用作扩展编译引用，不会在产物中包含 `Fiddler.exe`。

自动化测试使用模拟命名管道对端，并覆盖两种 MCP 传输。真实 Fiddler 测试需要显式启用：

| 环境变量 | 覆盖范围 |
| --- | --- |
| `FIDDLER_CLASSIC_INTEGRATION=1` | 状态、构造请求、抓包检查、大型/二进制正文分块、保存 SAZ、重放 |
| `FIDDLER_CLASSIC_AUTOMATION_INTEGRATION=1` | AutoResponder 备份/恢复、FARX 替换、请求/响应断点修改、自动继续 |
| `FIDDLER_CLASSIC_DESTRUCTIVE_INTEGRATION=1` | 清空和恢复 SAZ |
| `FIDDLER_CLASSIC_PROXY_INTEGRATION=1` | 挂接/取消系统代理，并恢复原始状态 |

只应在受控的 Fiddler 配置中运行门控测试。自动化测试会暂时替换并恢复 AutoResponder 规则列表，并通过实时断点发送回环流量。破坏性测试会备份并恢复会话列表，但仍会操作当前抓包证据。
