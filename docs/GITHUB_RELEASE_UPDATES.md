# AI Sales OS GitHub Release 自动更新与发布

## 架构

AI Sales OS 使用 Velopack 的 Windows 更新客户端，GitHub Releases 是唯一更新源，不需要云服务器或额外托管费用。Windows 首次安装入口为简体中文 Inno Setup 向导，向导内部调用 Velopack 安装核心，因此不会破坏后续自动更新身份。

macOS 构建目前暂停：缺少 Developer ID、签名和公证条件时，安装包无法达到可正常分发标准。GitHub Actions 的 Mac 任务默认跳过；只有用户明确通知恢复，并在仓库变量中设置 `ENABLE_MACOS_RELEASE=true` 后才会重新生成 Mac 资产。

程序启动后执行以下流程：

1. 读取构建时写入程序集的 GitHub 仓库 URL。
2. Windows 查询 `releases.win.json`。macOS 更新通道保留在源代码中，但当前不构建和发布新资产。
3. 有新版本时后台下载并校验更新包。
4. 左下角版本入口显示当前版本、最新版本、下载进度和更新日志。
5. 只有用户点击“安装更新并重启”后才真正替换应用。macOS 安装在 `/Applications` 时可能显示系统管理员授权提示。

客户端不保存 GitHub Token。供客户端读取 Release 的仓库必须是公开仓库；若源代码需要保密，可以使用独立的公开 Release 仓库只存放更新资产。

## 本地数据边界

应用安装目录与业务数据目录完全分离：

| 数据 | 固定位置 | 更新行为 |
|---|---|---|
| SQLite 客户与业务数据 | `%LOCALAPPDATA%\WAFlow\waflow.db` | 保留 |
| WhatsApp 多设备会话 | `%LOCALAPPDATA%\WAFlow\whatsapp-sessions` | 保留 |
| WhatsApp 媒体 | `%LOCALAPPDATA%\WAFlow\whatsapp-media` | 保留 |
| 本地 Bridge 运行时 | `%LOCALAPPDATA%\WAFlow\runtime` | 需要时由程序重新提取，不影响账号数据 |
| API Key | Windows Credential Manager 的 `WAFlow/...` 条目 | 保留 |

## 首次启用

旧的便携 EXE 或 Inno Setup 版本不受 Velopack 管理。1.17.2 及之后的便携版可以在版本中心检查 GitHub Release、自动下载正式 `AI Sales OS Setup.exe`，并由用户点击启动安装；安装资产匹配会忽略 GitHub 对空格、点号和连字符的文件名规范化。完成这一次正式安装后，后续版本即可在程序内自动下载并安装重启。1.17.1 及更早便携版仍需从 GitHub Release 手动运行一次 Setup，因为旧程序本身没有完整的便携版引导安装能力。

## 本地打包

```powershell
cd "D:\whatsapp 自动化"
$env:WAFLOW_DOTNET_PATH = "D:\whatsapp 自动化\work\dotnet8\dotnet.exe"
powershell -ExecutionPolicy Bypass -File .\scripts\build-velopack-release.ps1 `
  -Version 1.16.0 `
  -RepositoryUrl "https://github.com/FrankShi811/AI-whatsapp-OS"
```

输出：

- `AI Sales OS.exe`：固定名称的便携/验收程序，每次覆盖。
- `dist\installers\AI Sales OS Setup.exe`：固定名称的简体中文安装入口，每次覆盖；内部安装 Velopack 正式客户端。
- `dist\velopack\releases.win.json`、`.nupkg` 和 Setup：GitHub Release 更新资产。
- `dist\installers\AI Sales OS macOS Apple-Silicon Chinese Preview.zip`：Apple 芯片原生中文测试包。
- `dist\installers\AI Sales OS macOS Intel Chinese Preview.zip`：Intel Mac 原生中文测试包。
- `dist\installers\AI Sales OS macOS * Chinese Preview.pkg`：macOS 中文测试安装器；包内包含自包含 .NET 运行时与自动更新元数据。
- `dist\velopack-macos-*\releases.osx-*.json` 和 `.nupkg`：macOS 自动更新资产。

## GitHub Release 发布

日常版本更新只提交并推送 GitHub，由 Actions 生成 Release、安装包和更新清单。不要在用户电脑执行本地发布脚本，不要覆盖、关闭或重启正在使用的 `AI Sales OS.exe`；用户通过程序左下角版本中心手动确认安装。

1. 将仓库连接为 Git remote，并确保 GitHub Actions 可运行。
2. 更新两个 `.csproj` 的 `Version` 和 `docs/releases/vX.Y.Z.md`。
3. 提交并推送到 `main`。工作流会读取 `.csproj` 版本号并创建/更新对应 Release，无需在本机保存 GitHub Token。
4. 如需手动走标签发布，也可以创建并推送标签：

```powershell
git tag v1.16.0
git push origin v1.16.0
```

5. `.github/workflows/release.yml` 在 `windows-latest` 构建 Bridge、WPF、Velopack 和中文安装向导，并通过仓库内置 `GITHUB_TOKEN` 上传 Release。Mac 任务仅在仓库变量 `ENABLE_MACOS_RELEASE=true` 时运行。

也可以从 GitHub Actions 页面手动运行工作流并填写 `1.16.0`。

## 测试方法

### 基础回归

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\test-desktop.ps1
```

### 安装与启动

1. 在干净的 Windows 用户环境运行 `AI Sales OS Setup.exe`。
2. 确认开始菜单、桌面快捷方式和任务栏均显示 AI Sales OS 图标。
3. 确认左下角显示当前版本，并可打开版本中心。
4. 确认安装向导的标题、路径选择、按钮、错误和完成提示均为简体中文。

### macOS 人工测试

1. 根据 Mac 处理器选择 Apple-Silicon 或 Intel ZIP，解压后将 `AI Sales OS.app` 拖入“应用程序”。
2. 因当前测试包未签名/公证，首次启动右键选择“打开”；如仍拦截，在“系统设置 → 隐私与安全性”选择“仍要打开”。
3. 验证原生窗口、全部中文导航、Dashboard、客户列表、商机智能、历史会话读取、API Key 写入 Keychain，以及左下角版本中心自动检查更新。
4. 通过 `.pkg` 或 Velopack portable ZIP 安装后，验证版本中心能识别 `osx-arm64` / `osx-x64` 通道并下载对应架构更新。
5. 当前不要验收 WhatsApp 扫码、实时收发、建群和真实群发；这些能力等待原生 Mac Bridge 完成。

## 运行环境包含关系

- Windows WPF 主程序使用 `win-x64` 自包含发布，安装目标电脑无需另装 .NET 8。
- WhatsApp Bridge 以独立本地可执行文件随 Windows 应用嵌入，包含 Node.js 运行时，用户无需另装 Node.js 或 npm。
- macOS Apple 芯片和 Intel 包分别使用 `osx-arm64` / `osx-x64` 自包含发布，目标 Mac 无需另装 .NET、Avalonia 或 Python。
- Python、Pillow、Inno Setup、Node.js SDK 和 .NET SDK 只在 GitHub Actions 构建机使用，不是最终用户依赖。

### 本地更新闭环

1. 先打包并安装低版本，例如 1.16.0。
2. 使用同一 `dist\velopack` 目录再打包 1.16.1。
3. 启动旧版前设置 `AI_SALES_OS_UPDATE_SOURCE` 为该目录，用于本地更新源测试。
4. 等待左下角显示“已下载”，点击“安装更新并重启”。
5. 重启后确认版本为 1.16.1。
6. 更新前后分别计算 `waflow.db`、会话目录和配置的哈希/文件清单，确认未变化。

### GitHub Release 生产验收

1. 发布一个高于已安装版本的 Release。
2. 启动旧版，确认能显示远程版本和更新日志并自动下载。
3. 断网/限流时确认业务功能不受影响，版本中心只显示可重试错误。
4. 点击安装并重启，确认进程关闭、更新成功、版本号变化、本地数据完整。
