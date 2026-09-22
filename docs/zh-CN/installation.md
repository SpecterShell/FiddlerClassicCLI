# 安装

[English (en-US)](../en-US/installation.md) | **简体中文 (zh-CN)**

Fiddler Classic CLI 以自包含 Windows x64 目录发布。默认情况下，安装脚本写入当前用户的本地应用数据目录，并更新其 `PATH`。Fiddler 桥接扩展需要单独安装。

## 发布包

每个发布版本包含两个文件：

- `fiddler-classic-win-x64.zip`
- `SHA256SUMS`

解压 ZIP 前，请核对其 SHA-256 摘要，所用 `SHA256SUMS` 条目的文件名必须完全一致。在解压目录中运行随附的安装脚本：

```powershell
$cliPath = (./install.ps1 | Select-Object -Last 1)
& $cliPath --version
```

安装脚本会将完整程序复制到 `%LOCALAPPDATA%\Programs\FiddlerClassicCLI\<版本>`，并用该版本目录替换当前用户 `PATH` 中由 Fiddler Classic CLI 管理的目录项。脚本也会更新调用它的 PowerShell 进程中的 `PATH`。长时间运行的 Agent 进程可能已缓存环境变量，请在这些进程中使用脚本返回的 `$cliPath`。

替换现有安装前，请停止其前台 MCP 服务器，并运行 `daemon stop`。安装后，使用脚本返回的可执行文件路径重启 MCP 客户端；如需守护进程，再从该构建运行 `daemon start`。运行中的进程会保留已加载的 SDK，仅复制文件不会更新 MCP 支持。安装路径变更后，还须按下方桥接安装步骤更新主程序启动记录。

如需保持 `PATH` 不变或指定其他当前用户目录，请运行：

```powershell
$cliPath = (./install.ps1 -InstallRoot "C:/Tools/FiddlerClassicCLI" -NoPathUpdate | Select-Object -Last 1)
```

## Agent 引导安装

安装脚本位于源码仓库和已解压发布包的根目录。加载随附的 Skill 后，可从包含 `SKILL.md` 的目录定位该共享根目录：

```powershell
$distributionRoot = Split-Path -Parent (Split-Path -Parent $skillRoot)
$installerPath = Join-Path $distributionRoot "install.ps1"
```

在已解压的发布目录中，运行 `$installerPath` 且不传递来源参数，即可安装脚本所在目录中的发布程序。单独安装的 Skill 不包含安装脚本。如果 `$installerPath` 不存在，请使用已解压的发布包或可信源码仓库。

根目录中的安装脚本可以从可信 GitHub 仓库下载发布版本。请以 `owner/name` 格式明确传入仓库标识：

```powershell
$cliPath = (& $installerPath -Repository $trustedRepository | Select-Object -Last 1)
& $cliPath --version
```

安装脚本通过 GitHub API 查找发布版本，并下载 `fiddler-classic-win-x64.zip` 和 `SHA256SUMS`。清单必须包含文件名完全一致的条目，脚本会在解压前验证 SHA-256。需要安装特定版本时，请将完整、准确的发布标签传给 `-Version`。安装私有仓库中的版本时，请在进程环境中设置 `GITHUB_TOKEN`；安装脚本不会输出该令牌。

如需安装已经下载的 ZIP，请将 `SHA256SUMS` 保存在同一目录并运行：

```powershell
$cliPath = (& $installerPath -PackagePath $trustedPackagePath | Select-Object -Last 1)
```

## Fiddler 桥接

安装 CLI 后，再安装扩展文件：

```powershell
& $cliPath bridge install
```

重启 Fiddler Classic 以加载扩展，然后验证所有组件：

```powershell
& $cliPath doctor
& $cliPath status --json
```

`bridge install` 会将 `FiddlerClassic.Bridge.dll` 和 `FiddlerClassic.Protocol.dll` 复制到 `%USERPROFILE%\Documents\Fiddler2\Scripts`。该命令还会以原子方式写入 `%LOCALAPPDATA%\FiddlerClassicCLI\bridge-host.json`，准确记录已安装主程序的可执行文件和版本，供扩展启动或查找守护进程。此文件仅允许当前用户访问；`bridge uninstall` 会删除该文件。

重启 Fiddler 后，主标签栏中会显示 **Fiddler Classic CLI** 标签页，Tools 菜单中的快捷入口可选中该标签页。该标签页管理 MCP HTTP 访问，不会改变 Fiddler 的抓包代理。安装桥接扩展不会安装 Fiddler，也不会改变系统代理或配置证书信任。

运行 `& $cliPath app detect --json` 可查找 Fiddler；自定义安装可加上 `--path "C:/Tools/Fiddler/Fiddler.exe"`。Fiddler 已关闭时，`& $cliPath app open` 会启动它，但不挂接系统代理。如需从 CLI 重启，请先保存需要的捕获记录，再运行 `& $cliPath app restart --yes`。原生对话框仍可能需要手动处理。进程选择和超时行为见[应用程序命令](cli.md#fiddler-应用程序)。

## 构建与打包

构建需要 .NET 10 SDK，并要求本机已安装 Fiddler Classic 5.x 或 6.x：

```powershell
./scripts/build.ps1
```

使用以下命令创建发布 ZIP 和校验清单：

```powershell
./scripts/package.ps1
```

构建带标签的版本时，请传入去掉开头 `v` 的语义版本值：

```powershell
./scripts/package.ps1 -Version "0.3.0-preview.1"
```

打包脚本将发布文件写入 `artifacts/release`。归档验证会拒绝 `Fiddler.exe`，不论其目录层级或文件名大小写。验证也会拒绝不安全的 Windows 路径、重复路径、符号链接以及文件与目录冲突。归档必须包含根目录安装脚本和全部五个 Skill 参考文件，且 Skill 内不得包含脚本。普通构建会在发布前清理固定的发布目录；`-SkipBuild` 直接打包现有目录。

运行 `./scripts/test-release-verifier.ps1` 可测试归档拒绝规则。`./scripts/test-release-install.ps1 -FixtureOnly` 使用仅报告版本、不执行其他操作的测试程序，安全验证首次安装、同版本重装和并存升级。测试会校验已安装文件的哈希，确认旧版本不变，并检查 `-NoPathUpdate` 是否保持两个 PATH 值不变。CI 使用实际发布 ZIP 执行同一组安装测试。

## GitHub Actions

仓库的[构建与发布工作流](../../.github/workflows/build-release.yml)使用 GitHub 托管的 `windows-2025` 运行器进行编译，并使用 Ubuntu 运行器发布 Release。

构建任务会执行以下步骤：

1. 安装 `global.json` 选定的 SDK。
2. 通过 WinGet 安装 `Telerik.Fiddler.Classic` `6.0.20261.7291`。如果 WinGet 不可用，或安装的 `Fiddler.exe` 版本不完全一致，任务会改用固定版本的 Chocolatey 包。
3. 在编译前验证 `%LOCALAPPDATA%\Programs\Fiddler` 中的 `Fiddler.exe`。
4. 运行 `scripts/package.ps1`，执行全部自动化测试、发布自包含主程序、创建 ZIP 和校验清单，并验证归档内容约定。
5. 测试归档拒绝规则，并执行临时安装、重装和升级检查，然后记录发布桥接 DLL 的 SHA-256。
6. 将 `fiddler-classic-win-x64.zip` 和 `SHA256SUMS` 上传为保留 14 天的工作流产物。

构建任务会在拉取请求、推送到 `main`、匹配 `v*` 的标签和手动触发时运行。其令牌只有仓库只读权限。

版本标签触发的发布任务会下载同一次构建产生的产物，再次验证，并使用具有 `contents: write` 权限的工作流 `GITHUB_TOKEN` 创建 GitHub Release。稳定标签使用 `v0.3.0` 格式；带有后缀的标签（如 `v0.3.0-preview.1`）会将 Release 标记为预发布版本。工作流会验证标签已经存在，且不会替换现有 Release 中的文件。

每次工作流都会针对 `5.0.20253.3311` 和 `6.0.20261.7291` 执行仅检查元数据的兼容性矩阵。各运行器下载同一个发布 ZIP，校验桥接哈希，并在不执行 Fiddler 代码的情况下解析直接 API 引用。两个矩阵任务均通过后才能发布 Release。此检查不验证原生绑定策略、按名称反射的行为或 UI 正确性。

手动工作流包含 `run_fiddler_compatibility` 选项。启用后，矩阵还会启动原生 Fiddler，执行生命周期和证据检查。矩阵针对每个引用仅编译检查程序和测试探针，不会重新构建生产桥接或协议 DLL。

原生探针检查标签页和 Tools 菜单注册，并检查标签页始终未选中时的守护进程启动、重复加载、受控内存会话检查、精确二进制正文范围和卸载清理。脚本拒绝本地用户环境，且要求显式启用一次性运行器测试。它不发送流量，也不挂接系统代理。本地构建成功不能证明已执行这些原生矩阵测试。

普通构建将 Fiddler 用作编译引用，只有显式启用的兼容性任务将其作为原生测试宿主。`Fiddler.exe`、测试探针、生成的凭据和原生用户配置均不会作为发布产物上传。
