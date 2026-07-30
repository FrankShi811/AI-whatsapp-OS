# AI Sales OS v5.11.1 原生 macOS 交付说明

## 产品与数据边界

Windows 主客户端继续使用 WPF；macOS 客户端使用 Avalonia/Cocoa 原生窗口并复用
`WAFlow.Core`。它不是 PWA、Electron、WebView 或 localhost 网页壳，也不会修改
Windows EXE、安装器、Velopack 通道或 PWA。

两个桌面客户端都采用本机数据。macOS 默认数据目录为：

```text
~/Library/Application Support/WAFlow
```

客户、消息、知识库、自动化、报告和应用设置保存在该目录的 SQLite 数据库及受控子目录；
API Key、邮箱密码和 WhatsApp 会话加密密钥保存在 macOS Keychain。项目没有共享客户数据库、
跨用户可见性或自动上传。

v5.11.1 起，设置页可以把完整工作区迁移到其他本机固定磁盘或空文件夹。迁移会先复制、
校验文件哈希和 SQLite 完整性，再重启切换；新位置启动失败会回滚，旧位置发生变化时会
安全保留。工作区位置只记录在当前 Mac，不会联通任何用户数据。

## v5.11.1 macOS 功能基线

macOS 版以 GitHub `main` 的 Windows v5.11.1 为功能基线，包含 8 个主模块：

1. Dashboard：客户、待跟进、渠道未读、自动化状态和优先商机。
2. 商机智能：单客户及断点续跑批量 AI 分析、评分、证据、风险和下一步动作。
3. 客户列表：Buyer ID 统一身份、Excel/CSV 全量导入、自定义维度、搜索、10/30 分页、
   新建、编辑和删除。
4. WhatsApp Inbox：多账号、二维码登录、同步、实时消息、文字/媒体发送、置顶、
   建群和 AI 回复建议。
5. 邮件 Inbox：Gmail、Outlook、Yahoo、iCloud 和自定义 IMAP/SMTP 账号，
   macOS Keychain、同步、收发和 AI 邮件草稿。
6. 自动化群发：WhatsApp/邮件受众、模板字段、预览、人工审批、排期、暂停、恢复、
   取消、安全阀和执行历史。
7. 知识库：PDF/Office/表格/文本/图片解析、版本、风险扫描、人工启用、停用和删除。
8. 客户智能分析：版本化客户报告、证据台账和 Word/PDF 导出。

设置模块支持 13 个内置/自定义 OpenAI 兼容 Provider、模型发现、全局或按模块路由、
推理强度、主题/缩放偏好、本地数据库备份和独立 macOS 更新通道。

## 原生 WhatsApp Bridge

`bridge/scripts/build-sea.mjs` 会在 macOS runner 上把 Node SEA、Baileys 和全部 Bridge
资源打包成无 `.exe` 后缀的 arm64/x64 Mach-O：

```text
AI Sales OS.app/Contents/MacOS/WAFlow.WhatsApp.Bridge
```

应用通过 stdin/stdout JSON 协议启动该本机进程。WhatsApp 会话凭据采用 AES-256-GCM
加密，密钥来自 Keychain；会话和媒体仍在本机 `WAFlow` 目录。没有浏览器后端或共享中转服务。

## 构建与 DMG

Apple Silicon：

```powershell
./scripts/build-macos-preview.ps1 -Version 5.11.1 -Architecture AppleSilicon
```

同时构建两种架构：

```powershell
./scripts/build-macos-preview.ps1 -Version 5.11.1 -Architecture Both
```

macOS 主机构建时会：

1. 安装锁定版本的 Bridge 依赖并生成目标架构 Mach-O。
2. 发布自包含 .NET 8 Avalonia 应用。
3. 创建标准 `.app`、ICNS、`Info.plist` 和中文安装说明。
4. 对内部验收包执行 ad-hoc `codesign`。
5. 使用 `hdiutil` 创建真实 UDIF 压缩 DMG。

Apple Silicon 产物：

```text
dist/installers/AI Sales OS macOS Apple-Silicon Chinese v5.11.1.dmg
dist/installers/AI Sales OS macOS Apple-Silicon Chinese Preview.zip
```

独立工作流 `.github/workflows/macos-dmg.yml` 不依赖也不发布 Windows Release。它在
Apple Silicon macOS runner 上挂载 DMG，校验 app/Bridge 架构、权限、bundle 身份和签名，
随后从已挂载 DMG 直接执行运行时冒烟，并使用 `/usr/bin/open -W -n` 通过
Finder/LaunchServices 启动完整 UI 冒烟。LaunchServices 启动必须生成 8 个模块及
深浅主题共至少 10 张真实截图才通过。

```text
AI Sales OS.app/Contents/MacOS/AISalesOS.Mac --smoke-test
```

## 签名、公证与验收边界

当前仓库未配置 Apple Developer 凭据时，自动构建使用 ad-hoc 签名，适合内部安装验收；
DMG 提供“首次安装并打开 AI Sales OS.command”，它会复制到当前用户的 Applications、
移除这一个已校验应用的 quarantine 属性、复验代码签名并打开，同时把旧应用保留为带时间戳
的备份。它不会删除或迁移本地客户数据。
没有 Apple Developer ID Application 证书和 notarization 凭据时，不能声称产物已经通过
Apple 公证或适合公开无提示分发。

工作流已支持在仓库 Actions secrets 中配置
`MACOS_CERTIFICATE_P12_BASE64`、`MACOS_CERTIFICATE_PASSWORD`、
`APPLE_NOTARY_KEY_P8_BASE64`、`APPLE_NOTARY_KEY_ID` 和
`APPLE_NOTARY_ISSUER_ID`。全部配置后会启用 hardened runtime、Developer ID 签名、
`notarytool` 公证、`stapler` 装订和 `spctl` 验收。

正式发布还需：

- Developer ID Application 签名。
- `notarytool` 公证与 `stapler` 装订。
- `spctl --assess` Gatekeeper 验收。
- 真实账号的 WhatsApp 扫码/收发/建群、邮箱 IMAP/SMTP、升级保留数据和长时间后台同步回归。

这些外部凭据和真实账号验收不影响内部 DMG 的生成，但决定能否把它升级为公开正式包。
