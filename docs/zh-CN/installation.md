# 安装

[English (en-US)](../en-US/installation.md) | **简体中文 (zh-CN)**

Fiddler Classic CLI 以自包含 Windows x64 目录发布。安装脚本只会写入当前用户的本地应用数据目录并更新当前用户的 `PATH`；Fiddler 桥接扩展需要单独安装。

## 发布包

一个发布版本包含以下两个文件：

- `fiddler-classic-win-x64.zip`
- `SHA256SUMS`

请先确认 ZIP 的 SHA-256 摘要与 `SHA256SUMS` 中同名文件的精确条目一致，再解压该文件。在解压目录中运行随附的安装脚本：

```powershell
$cliPath = (./install.ps1 | Select-Object -Last 1)
& $cliPath --version
```

安装脚本会将完整程序复制到 `%LOCALAPPDATA%\Programs\FiddlerClassicCLI\<版本>`，并将该版本目录放在当前用户 `PATH` 中由 Fiddler Classic CLI 管理的目录之前。脚本也会更新调用它的 PowerShell 进程中的 `PATH`。对于已经缓存环境变量的长时间运行 Agent 进程，请继续使用脚本返回的 `$cliPath`。

如需保持 `PATH` 不变或指定其他当前用户目录：

```powershell
$cliPath = (./install.ps1 -InstallRoot "C:/Tools/FiddlerClassicCLI" -NoPathUpdate | Select-Object -Last 1)
```

## Agent 引导安装

Agent Skill 在 `skills/fiddler-classic-cli/scripts/install.ps1` 中包含相同的安装脚本。当 Skill 位于已解压的发布目录中时，不传递来源参数即可安装相邻的发布程序。

已安装的 Skill 可以从可信 GitHub 仓库下载发布版本。请以 `owner/name` 格式明确传入仓库标识：

```powershell
$cliPath = (& "$skillRoot/scripts/install.ps1" -Repository $trustedRepository | Select-Object -Last 1)
& $cliPath --version
```

安装脚本会通过 GitHub API 解析发布版本，下载 `fiddler-classic-win-x64.zip` 和 `SHA256SUMS`，要求清单中存在精确文件名条目，并在解压前验证 SHA-256。需要指定版本时，请将精确发布标签传给 `-Version`。对于私有仓库，请在进程环境中设置 `GITHUB_TOKEN`；安装脚本不会输出该令牌。

如需安装已经下载的 ZIP，请将 `SHA256SUMS` 保存在同一目录并运行：

```powershell
$cliPath = (& "$skillRoot/scripts/install.ps1" -PackagePath $trustedPackagePath | Select-Object -Last 1)
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

`bridge install` 会将 `FiddlerClassic.Bridge.dll` 和 `FiddlerClassic.Protocol.dll` 复制到 `%USERPROFILE%\Documents\Fiddler2\Scripts`。该命令不会安装 Fiddler、改变系统代理或配置信任证书。

## 构建与打包

构建需要 .NET 10 SDK 和本机 Fiddler Classic 5.x：

```powershell
./scripts/build.ps1
```

使用以下命令创建发布 ZIP 和校验清单：

```powershell
./scripts/package.ps1
```

构建带标签的版本时，可传入保持语义版本文本不变的标签值：

```powershell
./scripts/package.ps1 -Version "0.3.0-preview.1"
```

发布文件会写入 `artifacts/release`。

## GitHub Actions

仓库的[构建与发布工作流](../../.github/workflows/build-release.yml)使用 GitHub 托管的 `windows-2025` 运行器进行编译，并使用 Ubuntu 运行器发布 Release。

构建任务会执行以下步骤：

1. 安装 `global.json` 选定的 SDK。
2. 通过 WinGet 安装 `Telerik.Fiddler.Classic` `5.0.20262.6151`。如果 WinGet 不可用，或未生成版本完全一致的 `Fiddler.exe`，任务会改用固定版本的 Chocolatey 包。
3. 在编译前验证 `%LOCALAPPDATA%\Programs\Fiddler` 中的 `Fiddler.exe`。
4. 运行 `scripts/package.ps1`，执行全部自动化测试、发布自包含主程序、创建 ZIP 和校验清单，并验证归档内容约定。
5. 将 `fiddler-classic-win-x64.zip` 和 `SHA256SUMS` 上传为保留 14 天的工作流产物。

构建任务会在拉取请求、推送到 `main`、匹配 `v*` 的标签和手动触发时运行。其令牌只有仓库只读权限。

对于版本标签，发布任务会下载同一次构建产生的产物，再次验证，并使用具有 `contents: write` 权限的工作流 `GITHUB_TOKEN` 创建 GitHub Release。稳定标签使用 `v0.3.0` 格式；`v0.3.0-preview.1` 等后缀会将 Release 标记为预发布版本。工作流会验证标签已经存在，也不会替换现有 Release 中的文件。

Fiddler 只会作为编译引用安装到临时 Windows 运行器。`Fiddler.exe` 不会被复制到发布目录、工作流产物或 GitHub Release。
