# Fiddler Classic CLI

[English (en-US)](../en-US/cli.md) | **简体中文 (zh-CN)**

`fiddler-classic` 可执行文件提供便于阅读的命令，用于检查和控制正在运行的 Fiddler Classic 5.x。为元数据命令添加 `--json` 可获得机器可读输出。

## 后台守护进程

依赖桥接的命令是持久后台守护进程的客户端。第一次执行操作时，CLI 会通过内部守护进程入口启动自身的隐藏副本，等待命名管道就绪，然后发送操作。后续调用会复用同一个进程。

Fiddler Classic 仅支持 Windows，因此守护进程使用 Windows 命名管道，而不是 Unix 域套接字。管道仅允许当前 Windows 用户访问。守护进程不是系统服务，也不会注册为开机启动项。

可以显式管理它：

```powershell
fiddler-classic daemon start
fiddler-classic daemon status
fiddler-classic daemon stop
```

启动守护进程不会启动 Fiddler、挂接系统代理或改变证书信任。`daemon status --json` 会返回进程 ID、启动时间、管道名称和主程序版本。

## 命令用法

功能行为分类见[功能类型与原生兼容性](feature-types.md)。其中，列表过滤、等待游标、比较和载荷分块是在 Fiddler 原生证据上实现的自定义操作，不会修改 Fiddler 的 Filters、Compare 或 Inspector UI 状态。

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

必需的标识符和路径使用位置参数，可选值使用选项。多个请求头应重复传递 `-H` 或 `--header`。

## 过滤与实时监视

会话列表、监视和 HAR 导出共享按 ID、方法、主机、URL、状态码、响应 MIME 类型、进程、请求头名称和值、耗时、HTTP 协议、正文总大小和错误状态筛选的参数。

`--body-contains` 会在 `--body-direction` 指定的请求或响应正文前缀中执行 UTF-8 字节精确搜索。`--body-search-bytes` 默认为 65,536，每个会话最多 1,048,576 字节。未提供 `--body-contains` 时不会搜索正文。

未提供 `--after-id` 时，`sessions watch` 从当前最新 ID 之后开始。命令持续输出已完成的匹配会话，直至达到 `--count`、1-60 秒无活动超时或进程被取消。`--jsonl` 每行输出一个会话对象。

## 修改与关联

`sessions remove --ids ...` 只删除选定会话；只要任一 ID 不存在，操作就会失败。其交互式确认行为与 `sessions clear` 相同。

重放和构造请求默认在接受后立即返回。添加 `--wait` 后，命令会从操作前的基准 ID 开始，等待首个方法和 URL 匹配的已完成请求。等待超时不会撤销已接受的请求。

## 导出、比较与 WebSocket

`sessions export <id> --format curl|raw-http` 用于复现单个敏感请求。原始 HTTP 会保留二进制正文；如果二进制正文无法安全表示为单条 Shell 命令，cURL 导出会拒绝执行。`sessions export --format har` 将过滤后的会话集合导出到绝对 `.har` 路径。覆盖现有文件需要 `--overwrite` 和明确确认。

`sessions diff` 比较元数据、保持原始顺序的请求/响应头、耗时以及请求/响应正文的 SHA-256，而不输出正文。`sessions websocket ... list` 返回帧方向、操作码、时间戳、长度、续帧和结束帧状态。`get` 命令从可选字节偏移开始流式输出完整帧载荷。

## AutoResponder

检查并配置当前引擎：

```powershell
fiddler-classic autoresponder status
fiddler-classic autoresponder configure --enable --permit-fallthrough --accept-connects --use-latency
```

规则使用 Fiddler 原生匹配和操作字符串。桥接为每个已加载规则分配运行时 ID，并保留从零开始的求值顺序：

```powershell
fiddler-classic autoresponder rules list
fiddler-classic autoresponder rules add "EXACT:https://example.test/api" "*drop"
fiddler-classic autoresponder rules update <rule-id> --action "*reset" --comment "temporary"
fiddler-classic autoresponder rules move <rule-id> 0
fiddler-classic autoresponder rules remove <rule-id> --yes
```

`--disable-on-match` 会在首次匹配后禁用规则。只有启用引擎的延迟开关时，每条规则的 `--latency-ms` 才会生效。规则对象保持加载期间，其 ID 保持稳定；加载 FARX 文件后应重新列出规则。

FARX 路径必须是绝对路径。导入会追加规则，替换则属于破坏性操作：

```powershell
fiddler-classic autoresponder rules save C:\captures\rules.farx
fiddler-classic autoresponder rules save C:\captures\rules.farx --overwrite --yes
fiddler-classic autoresponder rules load C:\captures\rules.farx
fiddler-classic autoresponder rules load C:\captures\rules.farx --replace --yes
```

原生操作会原样传递给 Fiddler。它们可以重定向、生成、延迟、丢弃或重置流量，也可以从当前用户有权访问的本地路径读取响应。

## 断点

触发器用于匹配后续流量的请求或响应阶段。默认只触发一次，并在 30 秒后自动继续托管的暂停。使用 `--persistent` 保留触发器，使用 `--hold 1..300` 修改超时秒数。

```powershell
fiddler-classic breakpoints arm request --method POST --host example.test --hold 45
fiddler-classic breakpoints arm response --status 500 --content-type json
fiddler-classic breakpoints arms
fiddler-classic breakpoints disarm <arm-id>
```

方法、主机、URL、进程和当前阶段请求头过滤条件通用于两种阶段。状态码与内容类型仅适用于响应触发器。`disarm` 只阻止后续匹配，不会继续已暂停的会话。

使用单调递增的序列号作为等待游标：

```powershell
fiddler-classic breakpoints list
fiddler-classic breakpoints wait --after-sequence 0 --timeout 30
fiddler-classic breakpoints show <breakpoint-id>
```

`show` 返回元数据和原始请求头，不返回正文字节。现有 `sessions body` 命令可以通过会话 ID 读取暂停中的会话。修改操作会检查当前阶段：

```powershell
fiddler-classic breakpoints update <breakpoint-id> --method PUT --url https://example.test/new --set-header "X-Test: yes" --body-file .\body.bin
fiddler-classic breakpoints update <breakpoint-id> --status 201 --reason Created --remove-header Content-Encoding --body "done"
fiddler-classic breakpoints resume <breakpoint-id>
fiddler-classic breakpoints abort <breakpoint-id> --yes
```

请求暂停可修改方法、URL、请求头和请求正文；响应暂停可修改状态码、原因短语、响应头和响应正文。正文为完整替换，最大 4 MiB。Fiddler UI 中手动创建的断点也会显示，但只有托管触发器会应用自动继续超时。

## 输出

元数据命令默认输出简洁的可读文本：

```powershell
fiddler-classic sessions list --host example.test --limit 20
```

脚本应使用 `--json`：

```powershell
fiddler-classic sessions list --host example.test --limit 20 --json
```

`sessions body` 和 `sessions websocket ... get` 会将完整原始字节写入必需的 `--output` 路径。使用 `--output -` 时，stdout 只包含载荷数据，诊断信息写入 stderr。

## 故障排查

命令无法连接时检查两个层级：

```powershell
fiddler-classic daemon status
fiddler-classic doctor
```

CLI 守护进程管道失效时，只重启守护进程：

```powershell
fiddler-classic daemon stop
fiddler-classic daemon start
```

只有 `doctor` 报告扩展桥接未连接时才重启 Fiddler Classic。即使 Fiddler 已关闭，守护进程也可以继续运行；操作命令会返回可执行的不可用或超时错误。
