# AI Sales OS macOS 真原生迁移与交付说明

## 当前结论（1.16.2）

Windows 主客户端仍是原生 WPF。仓库现已新增独立的 `WAFlow.Mac` Avalonia 原生客户端，复用 `WAFlow.Core` 的 SQLite、客户、评分、会话历史、Campaign 和设置契约，并使用 macOS Keychain 保存 API Key。它不是 Electron、WebView 或 HTTP 网页壳。

在本机使用 .NET 8 SDK 对 `osx-arm64` 执行发布已验证失败：

```text
error NETSDK1082: Microsoft.WindowsDesktop.App.WPF 没有运行时包可用于指定的 RuntimeIdentifier“osx-arm64”。
```

所以 WPF 本体仍不能通过修改 RID 或扩展名变成 Mac 程序；当前 Mac 包来自真实 Avalonia/Cocoa 原生窗口和 `osx-arm64`/`osx-x64` Mach-O apphost。

## 当前可交付测试包

- `dist/installers/AI Sales OS macOS Apple-Silicon Chinese Preview.zip`
- `dist/installers/AI Sales OS macOS Intel Chinese Preview.zip`

两个 ZIP 都包含标准 `AI Sales OS.app/Contents/MacOS`、`Resources/AI-Sales-OS.icns` 和 `Info.plist`，并保留 Unix 可执行权限。界面和安装说明为中文。构建命令：

```powershell
.\scripts\build-macos-preview.ps1 -Architecture Both
```

本轮已在 Windows 交叉编译并静态验证 Mach-O、架构、bundle、图标、中文说明和可执行权限；按用户要求，首次真实安装与启动由用户在 Mac 上人工验收。

## 推荐迁移方案

1. 继续扩大 `WAFlow.Mac` 页面能力，使其覆盖 Windows 版全部编辑和操作流程。
2. 保留 Windows WPF 与 macOS Avalonia 两个原生 UI，共享 `WAFlow.Core` 领域服务。
3. 密钥存储已按平台隔离：Windows 使用 Credential Manager，macOS 使用 Keychain。
4. 抽象任务栏/通知能力：Windows 保留 AppUserModelID，macOS 使用 bundle identifier、Dock icon 和 UserNotifications。
5. 下一阶段分别构建 WhatsApp Bridge 的 `win-x64` PE 和 `osx-arm64`/`osx-x64` Mach-O，并按平台嵌入应用资源；当前 Mac 测试包默认不执行扫码、实时收发、建群或真实群发。
6. 为 macOS 生成 Universal 2 `.app`，再制作 `.dmg` 或 `.pkg`；默认推荐安装到 `/Applications`。macOS 没有“非系统盘优先”这一 Windows 盘符概念，安装器应遵循平台惯例，同时允许用户拖放到其他磁盘的 Applications 目录。
7. 在真实 macOS arm64 与 x64/Universal 环境完成：首次安装、升级覆盖、数据库迁移、Keychain、WhatsApp 扫码、AI API、导入、消息同步、建群入口、卸载/删除应用等回归。
8. 公开分发前使用 Apple Developer ID Application/Installer 证书签名，并提交 Apple notarization；没有开发者身份时只能交付未公证的本地测试包，不能声称已完成正式分发验收。

## 完成 macOS 安装包所需外部条件

- 一台可执行构建和安装测试的真实 Mac，或有权限的 macOS CI Runner。
- 明确采用 Avalonia 原生跨平台迁移，或另写 SwiftUI 客户端；前者能最大限度复用当前 C# 核心。
- 如需正式对外分发：Apple Developer Team、Developer ID 证书及 notarization 权限。

macOS 客户端已经接入 GitHub Release 自动检查、后台下载和用户确认后的安装重启，并按 `osx-arm64` / `osx-x64` 隔离更新通道。GitHub Actions 会在 Windows Release 成功后，使用 `macos-latest` 构建两个自包含 `.pkg` 安装包、portable ZIP 和对应更新清单。

在 Apple Developer ID 和真实 Mac 验收完成前，当前安装包明确标记为“中文测试版”，不声称已经签名、公证或完成 WhatsApp 全链路回归。WhatsApp 扫码、实时收发、建群和真实群发仍需原生 macOS Bridge，因此这些功能不属于当前 Mac 人工验收范围。
