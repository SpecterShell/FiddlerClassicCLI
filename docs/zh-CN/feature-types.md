# 功能类型与原生兼容性

[English (en-US)](../en-US/feature-types.md) | **简体中文 (zh-CN)**

每项公开功能都按照其行为来源分类。功能类型描述实现方式和兼容性约定，并不表示该功能是否同时由 CLI 和 MCP 提供。

## 类型

| 类型 | 含义 |
| --- | --- |
| 原生（Native） | 将操作委托给 Fiddler Classic API 或 UI 命令。最终行为和状态由 Fiddler 管理。 |
| 原生适配（Native adapter） | 使用当前 Fiddler 对象、事件、标记或引擎，同时增加适合传输的 ID、数据投影、校验或限长载荷访问。保留原生副作用和证据。 |
| 自定义（Custom） | 实现 Fiddler 中没有对应命令的代理功能。以本项目记录的 CLI/MCP 约定为准，并且不会修改名称相近的 Fiddler UI 设置。 |
| 混合（Hybrid） | 通过桥接读取 Fiddler 原生状态，再由外部主程序完成编排或序列化。 |
| 主程序（Host） | 完全在 Fiddler 外部运行，不使用 Fiddler 扩展 API。 |

## 功能矩阵

| 功能 | CLI 或 MCP 接口 | 类型 | 兼容行为 |
| --- | --- | --- | --- |
| 离线与实时状态 | `status`、`get_status` | 混合 | 安装和进程发现由主程序完成；代理、监听端口、HTTPS 解密、版本和会话数量来自当前 Fiddler 实例。 |
| 系统代理抓包 | `capture start\|stop`、`start_capture`、`stop_capture` | 原生 | 调用 Fiddler 的挂接或分离命令，并返回最终原生代理状态；不会修改证书信任。 |
| 会话快照与详情 | `sessions list\|show`、`list_network_requests`、`get_network_request` | 原生适配 | 读取 Fiddler 当前会话列表，保留原生 ID、状态、元数据和请求头顺序。 |
| 会话查询过滤与翻页 | 列表过滤参数和 MCP 续传边界 | 自定义 | 过滤快照，不修改 Fiddler 的 Filters 标签页、可见性规则或已保留会话。匹配和基于 ID 的翻页遵循本项目记录的语义。 |
| 会话等待与监视 | `sessions watch`、`wait_for_network_request` | 自定义 | 观察 Fiddler 完成事件，返回排他会话 ID 之后首个已完成的匹配项；不会创建 Fiddler UI 过滤器。 |
| 请求与响应正文访问 | `sessions body`、`get_network_request_body` | 原生适配 | 从原生 `Session` 正文数组复制精确字节。分块、偏移和文本/Base64 元数据仅属于传输行为；不会解码或标准化载荷。 |
| 清空与删除会话 | `sessions clear\|remove`、`clear_network_requests`、`remove_network_requests` | 原生 | 调用 Fiddler 会话列表删除命令。返回数量以 Fiddler 实际删除的会话为准。 |
| SAZ 归档 | `sessions save\|load`、`save_network_archive`、`load_network_archive` | 原生 | 使用 Fiddler 的 SAZ 读取器和写入器。路径校验与覆盖确认是桥接的安全控制。 |
| 请求重放 | `sessions replay`、`replay_network_request` | 原生 | 使用 Fiddler 的重新发出操作，包括原生无条件重放选项。可选结果关联属于主程序的自定义编排。 |
| 请求构造 | `request send`、`send_request` | 原生适配 | 校验并构建单个 HTTP 请求，再通过 Fiddler 的原生请求发送 API 提交。并不声称支持 Composer 的重定向、自动身份验证或 Scratchpad 批处理等选项。 |
| cURL、原始 HTTP 与 HAR 导出 | `sessions export` | 混合 | 通过桥接读取精确原生会话证据，再由主程序序列化目标格式；这些功能不是 Fiddler transcoder 插件。 |
| 会话比较 | `sessions diff`、`diff_network_requests` | 自定义 | 比较元数据、保持顺序的请求头、耗时和精确正文哈希；不会调用 Fiddler 的可视化 Compare 命令或外部比较程序。 |
| WebSocket 检查 | `sessions websocket`、`list_websocket_messages`、`get_websocket_message` | 原生适配 | 读取 Fiddler 原生 WebSocket 帧集合及精确载荷字节。翻页和限长载荷分块属于自定义传输行为。 |
| AutoResponder 设置与执行 | `autoresponder status\|configure` 及对应 MCP 工具 | 原生 | 读取和修改 Fiddler 当前 AutoResponder 引擎。匹配与操作字符串由 Fiddler 解释；桥接不会实现第二套规则引擎。 |
| AutoResponder 规则管理 | 规则列表、新增、更新、移动、删除和清空工具 | 原生适配 | 使用当前 Fiddler 规则对象。优先级变更使用 Fiddler 的原生提升/降低操作，因此求值顺序和分组行为仍由 Fiddler 管理。运行时 ID 只是桥接句柄。 |
| FARX 持久化 | AutoResponder 规则 `save` 和 `load` 工具 | 原生 | 使用 Fiddler 原生 FARX 保存、导入和替换操作。桥接增加路径校验、回滚、数量限制和确认。 |
| 原生断点处理 | 断点查看、更新、继续和中止工具 | 原生适配 | 使用 Fiddler 请求/响应手动篡改状态、原生会话请求头与正文、`ThreadResume` 和原生中止行为。仍可发现手动创建的 Fiddler 断点。 |
| 断点触发器与等待 | 断点触发器新增、移除、列表、等待和保持超时工具 | 自定义 | 增加限长过滤器、不透明 ID、序列游标、一次性行为和自动继续。匹配后通过设置 Fiddler 已记录的断点标记来激活原生断点。 |
| 诊断与安装 | `doctor`、`bridge install\|uninstall` | 主程序 | 发现文件、进程、版本、管道和配置，或复制桥接程序集；只有连接探测会访问当前桥接。 |
| 守护进程、MCP 传输与令牌管理 | `daemon`、`mcp`、`config token` | 主程序 | 在 Fiddler 外部实现本地 IPC、JSON-RPC 传输、环回 HTTP 身份验证和当前用户配置。 |

## 原生一致性规则

### 抓包证据

桥接以当前 `Session` 为事实来源。详情调用保留 Fiddler 请求头顺序和值。正文与 WebSocket 载荷 API 复制精确字节范围，不会静默解码、解压缩、脱敏或重新编码抓包证据。

自定义列表过滤器是查询时谓词。它们不会复现或修改 Fiddler Filters 标签页；该标签页的设置可以隐藏、标记、阻止或修改流量。

### AutoResponder

所有匹配和操作都由 Fiddler 求值。桥接保留引擎当前开关、规则顺序、启用状态、注释、匹配后禁用设置、延迟和导入响应标记。规则移动调用与 Fiddler 相同的提升/降低行为，不直接重排底层集合。

不透明规则 ID 只对当前已加载的规则对象有效。加载或替换规则集可能分配新的 ID，但不会改变 Fiddler 规则内容或顺序。

### 断点

桥接使用 Fiddler 的 `x-breakrequest` 和 `x-breakresponse` 标记激活请求与响应暂停，并观察原生手动篡改状态变化。修改直接应用于暂停中的原生 `Session`，继续或中止则委托给 Fiddler。

触发器、过滤匹配、序列游标和自动保持超时属于自定义安全与自动化功能。它们决定何时激活原生暂停，但不会替换 Fiddler 断点机制。

### 重放与请求构造

重放委托给 Fiddler 的重新发出命令。请求发送在完成校验与原始请求构建后委托给 `FiddlerObject.utilIssueRequest`。可选 `--wait` 行为通过基准 ID、方法和 URL 关联后续会话；该关联不是 Fiddler 提供的事务句柄。

## 验证

自动化测试验证协议帧、精确正文分块、过滤语义、翻页、输出格式、安全确认、CLI 解析和 MCP 传输。可选 Windows 集成测试会针对已安装的 Fiddler Classic 实例验证行为：

| 环境变量 | 原生兼容性覆盖 |
| --- | --- |
| `FIDDLER_CLASSIC_INTEGRATION=1` | 状态、请求发送、会话证据、SAZ、重放和结果等待 |
| `FIDDLER_CLASSIC_AUTOMATION_INTEGRATION=1` | AutoResponder 设置、原生规则优先级、FARX 恢复、请求/响应断点修改、继续和超时 |
| `FIDDLER_CLASSIC_DESTRUCTIVE_INTEGRATION=1` | 原生会话清空和 SAZ 恢复 |
| `FIDDLER_CLASSIC_PROXY_INTEGRATION=1` | 原生代理挂接/分离及原状态恢复 |

桥接面向 Fiddler Classic 5.x，其原生适配行为针对 `5.0.20262.6151` 进行测试。
