# 功能类型与原生兼容性

[English (en-US)](../en-US/feature-types.md) | **简体中文 (zh-CN)**

每项公开功能均按其行为来源分类。功能类型描述实现方式和兼容性约定，不表示该功能是否同时由 CLI 和 MCP 提供。

## 类型

| 类型 | 含义 |
| --- | --- |
| 原生（Native） | 将操作委托给 Fiddler Classic API 或 UI 命令。最终行为和状态由 Fiddler 管理。 |
| 原生适配（Native adapter） | 使用当前 Fiddler 对象、事件、标记或引擎，增加适合传输的 ID、数据投影、校验或限长载荷访问，并保留原生副作用和证据。 |
| 自定义（Custom） | 为 Agent 实现 Fiddler 中没有对应命令的操作。行为以本项目记录的 CLI/MCP 约定为准，不会修改名称相近的 Fiddler UI 设置。 |
| 混合（Hybrid） | 通过桥接读取 Fiddler 原生状态，再由外部主程序完成编排或序列化。 |
| 主程序（Host） | 完全在 Fiddler 外部运行，不使用 Fiddler 扩展 API。 |

## 功能矩阵

| 功能 | CLI 或 MCP 接口 | 类型 | 兼容行为 |
| --- | --- | --- | --- |
| CLI 帮助与解析错误 | 单独运行 `fiddler-classic-cli`、命令组、`--help` | 主程序 | 帮助写入 stdout，退出码为 0。解析错误将帮助和错误写入 stderr，退出码为 2，不执行操作。使用 `--json` 时，stderr 仅包含结构化错误。 |
| 离线与实时状态 | `status`、`get_status` | 混合 | 主程序查找已安装的程序和进程。代理、监听端口、HTTPS 解密、版本和会话数量来自当前 Fiddler 实例。 |
| Windows 主机系统代理抓包 | `capture start\|stop`、`start_capture`、`stop_capture` | 原生 | 将 Fiddler 挂接为运行它的 Windows 主机的系统代理或取消挂接，并返回原生代理状态。取消挂接后，显式路由的流量仍可到达运行中的监听器。证书信任和 HTTPS 解密设置保持不变。 |
| 会话快照与详情 | `sessions list\|show`、`list_network_requests`、`get_network_request` | 原生适配 | 读取 Fiddler 当前会话列表，保留原生 ID、状态和元数据，严格保留标头顺序。 |
| 会话查询过滤与翻页 | 列表过滤参数和 MCP 续传边界 | 自定义 | 过滤快照，不修改 Fiddler 的 Filters 标签页、可见性规则或已保留会话。匹配和基于 ID 的翻页遵循本项目记录的语义。 |
| 会话等待与监视 | `sessions watch`、`wait_for_network_request` | 自定义 | 观察 Fiddler 完成事件，返回首个已完成的匹配项，其会话 ID 必须大于指定边界。不会创建 Fiddler UI 过滤器。 |
| 请求与响应正文访问 | `sessions body`、`get_network_request_body` | 原生适配 | 从原生 `Session` 正文数组复制精确字节。分块、偏移和文本/Base64 元数据仅属于传输行为。不会解码或标准化载荷。 |
| 清空与删除会话 | `sessions clear\|remove`、`clear_network_requests`、`remove_network_requests` | 原生 | 调用 Fiddler 会话列表删除命令。返回数量以 Fiddler 实际删除的会话为准。 |
| SAZ 归档 | `sessions save\|load`、`save_network_archive`、`load_network_archive` | 原生 | 使用 Fiddler 的 SAZ 读取器和写入器。路径校验与覆盖确认是桥接的安全控制。 |
| 请求重放 | `sessions replay`、`replay_network_request` | 原生 | 使用 Fiddler 的重新发出操作，包括原生无条件重放选项。可选结果关联属于主程序的自定义编排。 |
| 请求构造 | `request send`、`send_request` | 原生适配 | 校验并构建单个 HTTP 请求，再通过 Fiddler 的原生请求发送 API 提交。兼容性约定不保证与 Composer 的重定向、自动身份验证或 Scratchpad 批处理等选项一致。 |
| cURL、原始 HTTP 与 HAR 导出 | `sessions export` | 混合 | 通过桥接读取精确的原生会话证据，再由主程序按所选格式序列化。这些功能不属于 Fiddler transcoder 插件。 |
| 会话比较 | `sessions diff`、`diff_network_requests` | 自定义 | 比较元数据、保留顺序的标头、耗时和精确正文哈希。不会调用 Fiddler 的可视化 Compare 命令或外部比较程序。 |
| WebSocket 检查 | `sessions websocket`、`list_websocket_messages`、`get_websocket_message` | 原生适配 | 读取 Fiddler 原生 WebSocket 帧集合及精确载荷字节。翻页和限长载荷分块属于自定义传输行为。 |
| AutoResponder 设置与执行 | `autoresponder status\|configure` 及对应 MCP 工具 | 原生 | 读取和修改 Fiddler 当前 AutoResponder 引擎。匹配与操作字符串由 Fiddler 解释。桥接不会实现第二套规则引擎。 |
| AutoResponder 规则管理 | 规则列表、新增、更新、移动、删除和清空工具 | 原生适配 | 使用当前 Fiddler 规则对象。优先级变更调用 Fiddler 的原生提升/降低操作，求值顺序和分组行为仍由 Fiddler 管理。运行时 ID 是仅供桥接使用的句柄。 |
| FARX 持久化 | AutoResponder 规则 `save` 和 `load` 工具 | 原生 | 使用 Fiddler 原生 FARX 保存、导入和替换操作。桥接增加路径校验、回滚、数量限制和确认。 |
| 原生断点处理 | 断点查看、更新、继续和中止工具 | 原生适配 | 使用 Fiddler 请求/响应手动篡改状态、原生会话标头与正文、`ThreadResume` 和原生中止行为。仍可发现手动创建的 Fiddler 断点。 |
| 断点触发器与等待 | 断点触发器新增、移除、列表、等待和保持超时工具 | 自定义 | 增加限长过滤器、不透明 ID、序列游标、一次性行为和自动继续。匹配后通过设置 Fiddler 已记录的断点标记来激活原生断点。 |
| CLI 安装 | `scripts/install.ps1`、`scripts/install-local.ps1` | 主程序 | 将已发布程序安装到固定的当前用户路径，更新 PATH，并默认部署嵌入的桥接。`scripts/install.ps1` 下载发布 EXE 并验证 GitHub 提供的文件 SHA-256 摘要，也可使用显式指定的本地来源。开发安装脚本选用所在仓库中已有的发布输出，无需下载。两者均不会启动 Fiddler、守护进程或 MCP 服务器，也不安装 Agent Skills。 |
| 诊断与桥接维护 | `doctor`、`bridge install\|uninstall` | 主程序 | 查找文件、进程、版本、管道和配置，复制桥接程序集，并维护当前用户的主程序启动记录。只有连接探测会访问运行中的桥接。 |
| Fiddler 应用程序管理 | `app detect\|open\|close\|restart`（仅 CLI） | 主程序 | 通过 Windows API 查找可执行文件，以及当前会话中属于当前用户的进程。显式启动使用 `-noattach`。经确认的关闭和重启请求正常关闭窗口，不会强制终止或自动保存捕获记录。 |
| 守护进程与 MCP 传输 | `daemon`、`mcp stdio\|http` | 主程序 | 在 Fiddler 外部实现本地 IPC 和 JSON-RPC 传输。C# SDK 2.2.0 支持 MCP `2026-07-28` 和旧版初始化流程，详见[协议兼容性](cli.md#mcp-协议)。守护进程应用已保存的 HTTP 启动策略。前台服务器仅绑定回环地址，并始终要求身份验证。前台和托管 HTTP 服务器在所有身份验证模式下都拒绝携带 Origin 的请求，匿名托管请求还须通过 Host 校验。 |
| 托管 HTTP 访问 | `mcp service`、`mcp clients`、`mcp connections`、`config token` | 主程序 | 配置回环、全接口或最多 16 个选定本地 IPv4 地址的绑定，以及启动策略、身份验证、命名凭据和活动连接。身份验证模式为 `required`、`non-loopback`（默认值）和 `none`。默认仅豁免实际套接字远端和本地 IP 都是回环地址的连接。即使请求来自本机，访问局域网地址仍须提供凭据。保存或启用 `none` 需要明确确认风险。所有匿名请求均拥有完整 MCP 权限，且不会归属到任何凭据。选定绑定不会回退到其他地址，Fiddler 抓包代理保持不变。 |
| Fiddler 管理界面 | **Fiddler Classic CLI** 标签页和 Tools 菜单入口 | 混合 | 扩展使用原生 MCP、Named pipes 和 Settings 子标签页显示监听器控件、客户端、连接、管道诊断、启动与身份验证设置、版本，以及项目和文档链接。身份验证使用带描述性选项的下拉框。接口选择列表使用原生文字渲染，与周围控件保持一致。绑定修改在点击 Apply 后生效，已启用的监听器会在确认后重启。管道刷新仅读取状态。扩展加载时应用启动策略，不依赖标签页选择。守护进程持有持久 HTTP 状态、凭据与连接。 |
| 有界会话汇总 | `sessions list --summary`、`summarize_network_requests` | 混合 | 主程序汇总一个有数量上限的原生元数据页。按主机和状态码统计的数量、已捕获正文字节总数和已完成请求耗时仅覆盖返回记录，不读取标头或正文。 |
| 仅含元数据的诊断文件 | `doctor --output` | 混合 | 从本地、桥接和守护进程状态中选取白名单字段生成报告，不启动守护进程。报告排除流量、凭据、身份、本地路径和原始错误。 |

## 原生一致性规则

### 代理挂接

抓包挂接会更改运行 Fiddler 的 Windows 主机的系统代理。无论是否挂接，显式路由的流量都可通过回环地址或已配置远程访问的接口到达运行中的 Fiddler 监听器。IPv4 `0.0.0.0` 表示绑定所有 IPv4 接口，客户端应使用具体的主机地址和代理端口，并满足网络路由和防火墙规则的要求。Fiddler 代理设置与 MCP HTTP 绑定相互独立。

### 抓包证据

桥接以当前 `Session` 为抓包证据的权威来源。详情调用保留 Fiddler 标头顺序和值。正文与 WebSocket 载荷 API 复制精确字节范围，不会静默解码、解压缩、脱敏或重新编码抓包证据。

自定义列表过滤器仅在查询时应用。它们不会复现或修改 Fiddler Filters 标签页。该标签页的设置可以隐藏、标记、阻止或修改流量。

### AutoResponder

所有匹配和操作都由 Fiddler 求值。桥接保留引擎当前开关、规则顺序、启用状态、注释、匹配后禁用设置、延迟和导入响应标记。移动规则时，桥接调用 Fiddler 的提升/降低操作，不直接重排底层集合。

不透明规则 ID 只对当前已加载的规则对象有效。加载或替换规则集可能分配新的 ID。ID 分配不会改变 Fiddler 规则内容或顺序。

### 断点

桥接使用 Fiddler 的 `x-breakrequest` 和 `x-breakresponse` 标记激活请求与响应暂停，并观察原生手动篡改状态变化。修改直接应用于暂停中的原生 `Session`，继续或中止则委托给 Fiddler。

触发器、过滤匹配、序列游标和自动保持超时属于自定义安全与自动化功能。它们决定何时通过 Fiddler 的原生断点机制激活暂停。

### 重放与请求构造

桥接将重放委托给 Fiddler 的重新发出命令。发送请求时，桥接先校验并构建原始请求，再调用 `FiddlerObject.utilIssueRequest`。可选 `--wait` 行为通过基准 ID、方法和 URL 关联后续会话。Fiddler 没有为该关联提供事务句柄。

## 验证

自动化测试验证协议帧、精确正文分块、过滤语义、翻页、输出格式、安全确认、CLI 解析和 MCP 传输。显式启用的 Windows 集成测试针对已安装的 Fiddler Classic 实例验证行为：

| 环境变量 | 原生兼容性覆盖 |
| --- | --- |
| `FIDDLER_CLASSIC_INTEGRATION=1` | 状态、请求发送、会话证据、SAZ、重放和结果等待 |
| `FIDDLER_CLASSIC_AUTOMATION_INTEGRATION=1` | AutoResponder 设置、原生规则优先级、FARX 恢复、请求/响应断点修改、继续和超时 |
| `FIDDLER_CLASSIC_DESTRUCTIVE_INTEGRATION=1` | 原生会话清空和 SAZ 恢复 |
| `FIDDLER_CLASSIC_PROXY_INTEGRATION=1` | 原生代理挂接/分离及原状态恢复 |

桥接支持 Fiddler Classic 5.x 和 6.x。普通构建使用 `6.0.20261.7291` 引用。CI 针对 `5.0.20253.3311` 和 `6.0.20261.7291` 执行直接 API 元数据解析检查，均通过后才能发布。两个任务从同一个已验证的发布 EXE 中提取桥接，并检查其 SHA-256。显式启用的原生运行还会测试标签页与菜单注册、未选中标签页时启动、合成会话证据和卸载清理。详见 [CI 验证](installation.md#github-actions)。元数据检查或单元测试通过，不能证明已执行原生矩阵测试。
