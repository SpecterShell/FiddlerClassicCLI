# 安装

[English (en-US)](../en-US/installation.md) | **简体中文 (zh-CN)**

Fiddler Classic CLI 以单个自包含 Windows x64 可执行文件 `fiddler-classic-cli.exe` 发布，其中嵌入了 .NET 运行时、托管依赖、桥接 DLL 和 `LICENSE`。运行时，.NET 默认将原生运行库文件提取到 `%TEMP%\.net`。默认安装目录为 `%LOCALAPPDATA%\Programs\FiddlerClassicCLI`，更新后可执行文件路径保持不变。

## 最新发布版本

在 PowerShell 中运行引导命令。该命令会执行[仓库的 `scripts/install.ps1`](https://github.com/SpecterShell/FiddlerClassicCLI/blob/main/scripts/install.ps1)，请先检查脚本内容，仅使用可信来源：

```powershell
irm https://raw.githubusercontent.com/SpecterShell/FiddlerClassicCLI/main/scripts/install.ps1 | iex
```

引导脚本默认从 `SpecterShell/FiddlerClassicCLI` 的最新 GitHub 发布版本下载名称完全一致的 `fiddler-classic-cli.exe`。脚本要求 GitHub 提供有效的 `sha256` 文件摘要，并在执行前验证下载内容。可执行文件或摘要缺失、校验值不匹配时，安装会停止。脚本安装 CLI 和 Fiddler 桥接，将固定安装目录加入当前用户的 `PATH`。安装不会启动 Fiddler、CLI 守护进程或 MCP 服务器，不会安装 Agent Skills，也不会更改证书信任和 HTTPS 解密设置。

终端或 Agent 进程尚未刷新环境变量时，可直接使用安装路径：

```powershell
$cliPath = Join-Path $env:LOCALAPPDATA "Programs/FiddlerClassicCLI/fiddler-classic-cli.exe"
& $cliPath --version
```

安装后，运行中的组件仍使用已加载的代码。需要使更新生效时，请显式重启 Fiddler、守护进程及 MCP 服务器或客户端。关闭或重启 Fiddler 前，请保存需要的捕获记录。

CLI 主程序和桥接扩展应使用同一次构建的版本。仅修改身份验证选项并点击 **Save settings** 时，旧版守护进程可能报错 `Specify a bind mode, a port, or both.`。请更新这两个组件、停止旧守护进程，再重启 Fiddler。面板会在刷新和执行更改前检查守护进程能力。状态检查失败时，设置控件会暂时禁用，未保存的编辑会保留，等待连接恢复。

## 检查脚本后再运行

如需先检查脚本，可将其下载到唯一的临时路径：

```powershell
$installerPath = Join-Path ([IO.Path]::GetTempPath()) ("fiddler-classic-cli-" + [guid]::NewGuid() + ".ps1")
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/SpecterShell/FiddlerClassicCLI/main/scripts/install.ps1" -OutFile $installerPath -ErrorAction Stop
Get-Content -LiteralPath $installerPath
```

检查脚本后，执行并删除临时副本：

```powershell
& $installerPath
Remove-Item -LiteralPath $installerPath
```

## 引导脚本选项

`scripts/install.ps1` 负责安装 CLI。未指定 `-PackagePath` 时，脚本会下载所选 GitHub 发布版本的可执行文件，与当前工作目录无关。

| 参数 | 行为 |
| --- | --- |
| `-PackagePath PATH` | 安装显式指定的本地可执行文件、目录或带有相邻校验清单的 ZIP。 |
| `-Repository owner/name` | 指定 GitHub 仓库，默认为 `SpecterShell/FiddlerClassicCLI`。 |
| `-Version TAG` | 指定准确的发布标签。省略时获取最新发布版本。 |
| `-InstallDirectory PATH` | 指定专用的本地绝对安装目录，默认为 `%LOCALAPPDATA%\Programs\FiddlerClassicCLI`。 |
| `-NoPathUpdate` | 保持 `PATH` 不变。 |
| `-SkipBridge` | 仅安装 CLI，保留已有桥接和主程序启动记录。 |
| `-Json` | 返回结构化安装结果。 |

`-PackagePath` 不能与 `-Repository` 或 `-Version` 同时使用。在源码仓库中，可用以下命令安装本地发布目录：

```powershell
./scripts/install.ps1 -PackagePath ./artifacts/publish/win-x64
```

显式指定的本地 EXE 作为可信来源处理，无需相邻的校验清单：

```powershell
./scripts/install.ps1 -PackagePath "C:/Downloads/fiddler-classic-cli.exe"
```

本地 ZIP 要求同一目录中有 `SHA256SUMS`，且其中的 ZIP 文件名和校验值完全匹配。本地目录作为可信输入处理。当前发布版本仅提供 EXE，引导脚本也只下载该文件。GitHub 下载始终要求根据 GitHub 提供的文件 SHA-256 摘要进行验证。访问私有 GitHub 仓库时，请通过进程环境提供 `GITHUB_TOKEN`，不要将其写入日志。

如需指定其他安装目录并保持 `PATH` 不变：

```powershell
./scripts/install.ps1 -InstallDirectory "C:/Tools/FiddlerClassicCLI" -NoPathUpdate
```

## 本地开发

在源码仓库中发布当前代码，然后安装该构建：

```powershell
./scripts/build.ps1
./scripts/install-local.ps1
```

`scripts/install-local.ps1` 使用其所在仓库中的 `artifacts/publish/win-x64/fiddler-classic-cli.exe`，不受当前工作目录影响。它将该本地 EXE 作为 `-PackagePath` 传给 `scripts/install.ps1`，不会构建或下载发布版本。如果发布输出不存在，脚本会停止并提示先运行 `scripts/build.ps1`。

默认安装会更新 `%LOCALAPPDATA%\Programs\FiddlerClassicCLI`、当前用户的 PATH 和 Fiddler 桥接。即使本地构建报告的版本号相同，也会替换已安装的 CLI。替换已加载的桥接前，请先保存需要的捕获记录并正常关闭 Fiddler。更新可执行文件前，也请停止正在使用它的守护进程或 MCP 服务器。安装后需手动启动这些组件。

该脚本将 `-InstallDirectory`、`-NoPathUpdate`、`-SkipBridge` 和 `-Json` 传给 `scripts/install.ps1`。如需保留常用的 CLI 安装、PATH 和桥接，可安装到独立目录：

```powershell
./scripts/install-local.ps1 -InstallDirectory "$env:LOCALAPPDATA/Programs/FiddlerClassicCLI-Dev" -NoPathUpdate -SkipBridge
```

直接使用该目录中的可执行文件即可。省略 `-SkipBridge` 会部署开发构建的桥接，并将 Fiddler 的主程序启动记录指向开发构建的可执行文件。`-Json` 返回与 `scripts/install.ps1` 相同的结构化安装结果，失败时返回非零退出码。

## 使用独立可执行文件

发布版本仅提供 `fiddler-classic-cli.exe`，不附带 ZIP 或独立校验文件。使用引导脚本可自动下载并验证。手动下载 EXE 时，请先将其 SHA-256 与 GitHub 返回的文件摘要比较，确认一致后再运行。文档和 Agent Skill 文件保留在源码仓库中。

将已验证的可执行文件放在固定目录中，然后部署其嵌入的桥接：

```powershell
./fiddler-classic-cli.exe bridge install
```

`bridge install` 部署扩展并记录当前可执行文件的路径，CLI 保留在原处，`PATH` 保持不变。请保留该路径上的可执行文件，供 Fiddler 启动守护进程。如需将本地可执行文件复制到固定安装目录并更新 `PATH`，请按上文示例使用 `scripts/install.ps1`，通过 `-PackagePath` 指定来源。

PowerShell 安装脚本和 `bridge install` 均不会启动 Fiddler、守护进程或 MCP 服务器，也不会配置 Agent Skills。如需使用 Skill，请从源码仓库获取 `skills/fiddler-classic-cli` 并另行设置。

## 安装错误

CLI 文件事务失败时，安装程序会回滚该事务。如果 CLI 文件提交后桥接安装失败，已验证的 CLI 会保留，脚本返回非零退出码。桥接文件可能已发生更改。请先保存需要的捕获记录并正常关闭 Fiddler，再使用已安装的可执行文件重试 `fiddler-classic-cli bridge install`。

## Fiddler 桥接

默认安装会将嵌入的 `FiddlerClassicCLI.Bridge.dll` 和 `FiddlerClassicCLI.Protocol.dll` 提取到 `%USERPROFILE%\Documents\Fiddler2\Scripts`，并在 `%LOCALAPPDATA%\FiddlerClassicCLI\bridge-host.json` 中写入已安装主程序的路径和版本。该启动记录仅限当前用户访问，供扩展在 Fiddler 随后加载时查找主程序。

如果使用了 `scripts/install.ps1 -SkipBridge`，或需要修复桥接，可运行：

```powershell
& $cliPath bridge install
```

首次安装桥接后启动 Fiddler，可能会为 `FiddlerClassicCLI.Bridge.dll` 和 `FiddlerClassicCLI.Protocol.dll` 分别弹出标题为 "Caution: Unverified Extension Detected" 的扩展授权窗口。每个窗口都要求确认是否允许加载 Fiddler Scripts 目录中的对应 DLL。

请检查每个窗口显示的完整路径和文件名，仅在信任已安装文件时选择 `Allow`。如果还希望 Fiddler 记住此次授权，可选择 `Always allow`。文件或来源不明时，请选择 `Do not allow`，核实安装来源后再继续。两个提示都需要在 Fiddler 中手动处理。安装脚本和 CLI 会将这些信任决定留给用户，等待授权或拒绝加载期间，桥接可能无法连接。

显式启动或重启 Fiddler Classic，并处理扩展提示后，验证连接：

```powershell
& $cliPath doctor
& $cliPath status --json
```

**Fiddler Classic CLI** 标签页及其 Tools 菜单快捷入口管理 MCP HTTP 访问，与 Fiddler 抓包代理分别配置。面板包含 **MCP**、**Named pipes** 和 **Settings** 子标签页。桥接安装不会安装 Fiddler，也不会更改系统代理、证书或 HTTPS 解密设置。`bridge uninstall` 在确认后删除扩展文件和启动记录。

在 **MCP** 中，**Enable MCP HTTP** 复选框位于 Bind 和 Port 上方。服务启用时仍可编辑这些控件，点击 **Apply** 后修改才会生效。绑定有变化时，Apply 会依次停止、配置并重启已启用的监听器。存在活动连接，或将启用远程访问、`none` 模式时，须先明确确认。任一步骤失败都会停止后续操作并显示错误。Apply/Refresh 下方的 **MCP addresses** 分组框集中显示回环和局域网 URL，**Authorized clients** 和 **Active connections** 各有独立分组框。Bind 提供 `loopback`、`all` 和 `selected`，选定模式可通过列表勾选最多 16 个活动本地 IPv4 地址。已保存的地址保持固定，选定地址不可用时会报告绑定错误，不会回退到其他接口。

每个显示的回环或局域网 URL 旁都紧邻一个带剪贴板图标的复制按钮。URL 和管道地址可用鼠标或键盘选中。面板变窄时，较长 URL 会自动换行。空间不足时，复制按钮只显示图标，并保留悬停提示和键盘快捷键。复制成功后，所点击的按钮会显示约 1.5 秒的 **Copied!**，紧凑模式下以该文字替换图标。按钮宽度随当前文字调整，随后恢复原宽度。按钮的悬停提示说明具体操作，包括对连接或凭据的影响。绑定下拉框按 Windows 实际渲染的字体调整宽度，完整显示选项。接口选择列表设置 `UseCompatibleTextRendering=false`，与周围原生控件保持一致。

在 **Settings** 中，可将启动策略设为 `enabled`、`disabled` 或 `last-state`。默认值 `last-state` 保留已保存的预期状态，新安装保持禁用。策略在守护进程启动时和 Fiddler 加载扩展时生效，也适用于已在运行的守护进程。保存策略不会改变当前监听状态。**Versions** 分组框显示组件版本，**Documentation** 提供项目和文档链接。

身份验证下拉框提供 **Require for all**（`required`，每个请求均须凭据）、**Non-loopback only**（默认值 `non-loopback`，仅回环连接豁免）和 **No authentication**（`none`，不检查 Bearer 凭据）。修改前必须禁用服务。新建或未设置的配置使用 `non-loopback`。配置文件中的显式旧字段 `HttpRequireAuthentication` 在加载时将 `true` 转为 `required`，将 `false` 转为 `none`。豁免检查实际套接字的两个 IP，判断前会将 IPv4 映射地址转换为 IPv4。即使请求来自本机，访问局域网地址仍须提供凭据，未知地址或转发标头不能获得豁免。仅绑定回环地址时，保存或启用 `non-loopback` 无需访问风险警告。保存或启用 `none` 时，必须确认所有可达客户端都将获得完整 MCP 权限，包括读取抓包流量和使用修改工具。使用 `required` 或 `non-loopback` 的远程访问也需要确认，因为令牌通过明文 HTTP 传输。在默认 `non-loopback` 模式下，本机进程（包括其他 Windows 账户下的进程）可以匿名访问回环 MCP，命名管道仍仅允许当前用户访问。通过回环连接的本地中继或反向代理会被视为回环对端。如需回环调用方或中继也验证身份，请选择 `required`。这些设置仅适用于托管监听器。前台 `mcp http` 始终要求身份验证并仅绑定回环地址。启动确认规则、CLI 选项与安全限制见[托管 HTTP](cli.md#托管-mcp-http)。

运行 `& $cliPath app detect --json` 可查找 Fiddler，自定义安装可加上 `--path "C:/Tools/Fiddler/Fiddler.exe"`。Fiddler 已关闭时，`& $cliPath app open` 使用 `-noattach` 启动它。如需从 CLI 重启，请先保存需要的捕获记录，再运行 `& $cliPath app restart --yes`。原生对话框可能需要手动处理。进程选择和超时说明见[应用程序命令](cli.md#fiddler-应用程序)。

### 命名管道

只读 **Named pipes** 子标签页直接排列各个控件，不使用外围分组框。桥接和守护进程的完整管道路径以 `\\.\pipe\<name>` 格式显示，复制按钮紧邻各个路径右侧。这些按钮与 URL 复制按钮使用相同的悬停提示、紧凑布局和 **Copied!** 反馈。子标签页还列出支持的桥接协议（v3）、守护进程协议（v1）和访问限制 **Current Windows user only**。Windows ACL 将访问权限限制为当前用户。

桥接状态反映本地监听器的实际生命周期：`Stopped`、`Starting`、`Listening`、`Retrying` 或 `Unavailable`。无法监听时，请查看显示的最近一次监听器错误。守护进程状态检查成功后显示 `Responding` 和 PID。检查超时或无法获取响应时，守护进程状态仍不确定，PID 会清除为 `Unknown`。仅凭检查失败无法判定守护进程已停止。可用 `doctor` 检查主程序安装，再重试状态检查。

状态每两秒刷新一次，请求不会重叠。**Refresh pipes** 仅读取本地桥接快照和守护进程状态，不会启动守护进程或配置监听器。扩展仍会在初始化时启动或发现守护进程，此过程独立于该按钮和标签页选择。该区域不显示凭据、令牌或流量内容，也不记录管道连接数量或历史。

## 构建与打包

构建需要 .NET 10 SDK，并要求本机已安装 Fiddler Classic 5.x 或 6.x：

```powershell
./scripts/build.ps1
```

使用以下命令创建发布可执行文件：

```powershell
./scripts/package.ps1
```

构建带标签的版本时，请传入去掉开头 `v` 的语义版本值：

```powershell
./scripts/package.ps1 -Version "0.3.0-preview.1"
```

普通构建会在发布前清理 `artifacts/publish/win-x64`，最终仅在其中保留 `fiddler-classic-cli.exe`。打包脚本验证该输出，清理生成的 `artifacts/release` 目录，再仅复制可执行文件。`-SkipBuild` 验证并复制现有发布输出，不重新构建。编译中间文件和测试文件存放在这两个输出目录之外。

发布验证要求目录中只有一个名称正确的 Windows x64 PE 可执行文件。验证会拒绝额外文件或目录、重解析点以及无效的可执行文件头，并在提供预期摘要时检查 SHA-256。`./scripts/test-release-verifier.ps1` 检查这些拒绝规则，`./scripts/test-single-file.ps1` 检查独立运行和嵌入资源，`./scripts/test-release-install.ps1` 在临时位置检查安装行为。这些检查必须保留用户已有的 CLI、桥接和环境设置。

## GitHub Actions

仓库的[构建与发布工作流](../../.github/workflows/build-release.yml)使用 GitHub 托管的 `windows-2025` 运行器进行编译，并使用 Ubuntu 运行器发布 Release。

构建任务会执行以下步骤：

1. 安装 `global.json` 选定的 SDK。
2. 通过 WinGet 安装 `Telerik.Fiddler.Classic` `6.0.20261.7291`。如果 WinGet 不可用，或安装的 `Fiddler.exe` 版本不完全一致，任务会改用固定版本的 Chocolatey 包。
3. 在编译前验证 `%LOCALAPPDATA%\Programs\Fiddler` 中的 `Fiddler.exe`。
4. 运行 `scripts/package.ps1`，测试解决方案、发布自包含主程序，并验证发布输出目录和 Release 目录各自仅包含 `fiddler-classic-cli.exe`。
5. 测试发布拒绝规则和独立运行，并在临时位置检查安装、重装和升级。将可执行文件及其嵌入桥接的 SHA-256 记录为任务输出。
6. 仅上传 `fiddler-classic-cli.exe`，作为保留 14 天的工作流产物。摘要通过工作流元数据传递给后续任务。

构建任务会在拉取请求、推送到 `main`、匹配 `v*` 的标签和手动触发时运行。其令牌只有仓库只读权限。

版本标签触发的发布任务会下载同一次构建产生的可执行文件，并使用具有 `contents: write` 权限的工作流 `GITHUB_TOKEN` 运行 `scripts/publish-release.ps1`。脚本会先验证单文件输出及其预期 SHA-256，再访问 GitHub，且仅上传 `fiddler-classic-cli.exe`。Release 已存在时，脚本保留其标题、说明、草稿状态和预发布设置。尚不存在时，脚本会确认标签已经存在，再创建 Release。稳定标签使用 `v0.3.0` 格式。带有后缀的标签（如 `v0.3.0-preview.1`）会将新建的 Release 标记为预发布版本。

重新运行时，已有的 `fiddler-classic-cli.exe` 必须处于已上传状态，且 SHA-256 摘要与本地文件一致。文件不一致或缺少可验证的摘要时，脚本会停止。脚本不会替换已有文件，不可变的 Release 必须已经包含匹配的可执行文件。身份验证或连接失败也会中止发布。`scripts/test-release-publication.ps1` 使用模拟的 GitHub CLI 和本地可执行文件测试样本，在工作流的两个平台上检查这些处理分支，不会向 GitHub 发送请求。

每次工作流都会针对 `5.0.20253.3311` 和 `6.0.20261.7291` 执行仅检查元数据的兼容性矩阵。各运行器下载同一个发布 EXE，先验证其 SHA-256，再将嵌入的桥接提取到测试目录。随后校验桥接哈希，并在不执行 Fiddler 代码的情况下解析直接 API 引用。两个矩阵任务均通过后才能发布 Release。此检查不验证原生绑定策略、按名称反射的行为或 UI 正确性。

手动工作流包含 `run_fiddler_compatibility` 选项。启用后，矩阵还会启动原生 Fiddler，执行生命周期和证据检查。矩阵针对每个引用仅编译检查程序和测试探针，不会重新构建生产桥接或协议 DLL。

原生探针检查标签页和 Tools 菜单注册，并检查标签页始终未选中时的守护进程启动、重复加载、受控内存会话检查、精确二进制正文范围和卸载清理。脚本拒绝本地用户环境，且要求显式启用一次性运行器测试。它不发送流量，也不挂接系统代理。本地构建成功不能证明已执行这些原生矩阵测试。

普通构建将 Fiddler 用作编译引用，只有显式启用的兼容性任务将其作为原生测试宿主。`Fiddler.exe`、测试探针、生成的凭据和原生用户配置均不会作为发布产物上传。
