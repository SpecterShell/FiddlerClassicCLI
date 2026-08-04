# CLI 与 MCP 设计

[English (en-US)](../en-US/design.md) | **简体中文 (zh-CN)**

此集成借鉴了 [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp) 面向代理的原则和列表/详情工具模式，同时保留 Fiddler Classic 的术语和进程级抓包模型。

## 功能分类

功能按照行为来源分为原生、原生适配、自定义、混合或主程序功能。[功能类型与原生兼容性矩阵](feature-types.md)定义了每个 CLI 和 MCP 领域的一致性约定。自定义查询、等待、比较和传输操作使用独立规范，不声称复现名称相近的 Fiddler UI 标签页。

## 检查工作流

使用能够回答当前问题的最小工具：

1. 调用 `list_network_requests` 过滤摘要并取得请求 ID。
2. 调用 `get_network_request` 获取一个事务。请求头保持为可选内容，只有 `includeHeaders=true` 时才返回。
3. 仅在需要载荷字节时调用 `get_network_request_body`。每次最多读取 64 KiB，并以 `offset + bytesReturned` 继续，直至 `eof=true`。
4. 当代理需要下一个已完成的匹配请求时，调用 `wait_for_network_request`，无需轮询完整列表。

CLI 通过 `sessions list`、`sessions show` 和 `sessions body` 提供相同的渐进式检查流程。默认输出为可读文本，`--json` 为脚本提供稳定的结构化数据。CLI 的完整正文写入显式指定的文件或 stdout，不会嵌入元数据结果。

依赖桥接的 CLI 命令使用自动启动的后台守护进程。每次调用通过仅限当前用户访问的 Windows 命名管道发送一个带帧请求，守护进程再将现有的带版本桥接协议转发给 Fiddler。守护进程不持有抓包状态；Fiddler 始终是事实来源。

已完成会话通知会写入进程内的限长 ID 缓冲区。等待操作在 Fiddler UI 线程之外休眠，只在获取匹配快照时切换回 UI 线程。构造请求和重放会返回基准会话 ID、预期方法与 URL；可选等待功能使用这些关联信息查找首个匹配的已完成会话。

## AutoResponder 模型

桥接在 UI 线程中操作 Fiddler 当前的 `AutoResponder` 实例。它为已加载的规则对象分配不透明 ID，公开原始匹配/操作文本，并通过 Fiddler 的原生提升/降低操作调整优先级。导入使用 Fiddler 的 FARX 导入器，替换使用其规则加载器。FARX 字节始终保存在磁盘上，协议中只传递经过校验的绝对路径。

桥接不会解释原生操作。这样可以保留 Fiddler 的行为，避免出现第二套不同的规则引擎；同时也意味着修改工具属于开放世界操作：操作可能影响网络流量，或使用当前用户权限读取本地响应文件。

## 断点模型

触发器是用于后续请求头或响应头的限长内存过滤器。匹配时，扩展会设置 Fiddler 的 `x-breakrequest` 或 `x-breakresponse` 会话标记，并跟踪进入手动篡改状态的过程。一次性触发器会在匹配后立即移除。

待处理断点 ID 指向当前暂停。即使同一个 Fiddler 会话先在请求阶段暂停、再在响应阶段暂停，单调递增的序列号也可作为可靠的等待游标。托管暂停具有 1-300 秒保持时间，到期时调用 `ThreadResume`。Fiddler 中手动创建的暂停也可被发现和控制，但桥接不会为其设置超时。

只有会话仍处于预期的手动篡改状态时才能修改。请求暂停可修改方法、URL、请求头和正文；响应暂停可修改状态码、原因短语、响应头和正文。正文替换限制为 4 MiB。继续或中止会先移除待处理句柄，再改变 Fiddler 状态，避免并发调用重复操作。

## 翻页

代理检查流量时，Fiddler 的抓包集合可能继续变化。因此，`list_network_requests` 使用包含边界的请求 ID，而不是页码。当仍有更多过滤结果时，响应会包含一个续传值：

- 按最新优先返回时提供 `nextMaxRequestId`；下次调用使用相同过滤条件，并将其作为 `maxRequestId`。
- 按最早优先返回时提供 `nextMinRequestId`；下次调用使用相同过滤条件，并将其作为 `minRequestId`。

这样可以避免新到达的会话改变数字页位置而产生重复结果。

## 输出边界

- 列表只包含元数据，不包含请求头或正文。
- 详情仅在明确请求时包含保持原始顺序的请求头，不包含正文。
- MCP 正文结果是带明确编码元数据的限长分块。
- WebSocket 列表只包含帧元数据；载荷使用独立的 64 KiB MCP 分块接口。
- CLI 正文输出保留完整原始载荷，不执行标准化。
- 大型归档使用绝对 `.saz` 路径表示，不以内联数据传输。
- FARX 规则集使用绝对 `.farx` 路径表示，不以内联数据传输。
- 断点详情包含元数据和可选请求头；现有的限长正文工具用于读取暂停中的载荷。
- cURL、原始 HTTP、HAR 和 diff 都是显式的敏感证据操作。diff 返回正文哈希，不返回正文数据。

这些边界在不改变抓包证据的前提下，减少常规调用消耗的上下文。

## 安全模型

工具注解区分只读检查、破坏性删除或传输中修改，以及开放网络操作。清空、选择性删除、归档/FARX 替换、规则删除和会话中止会在适用处要求明确确认。已捕获的请求头和正文不会被静默脱敏，因此调用方必须将显式输出的证据视为敏感数据。

主程序连接用户正在运行的 Fiddler Classic。它不会自动启动 Fiddler、在没有命令的情况下挂接系统代理，也不会安装或信任证书。

## 有意保留的差异

Chrome DevTools MCP 的 `list_network_requests` 和 `get_network_request` 面向页面范围。本项目使用相同的请求中心 MCP 词汇，但查询 Fiddler Classic 的进程级会话列表。独立的 `get_network_request_body` 工具可避免普通元数据调用意外返回大量或敏感载荷。面向人的 CLI 继续使用 `sessions`，因为该术语与 Fiddler Classic UI 一致。
