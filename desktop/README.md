# AI Sales OS Windows 原生桌面版

该目录是 WPF/.NET 8 原生实现，不加载 React，不包含 Electron、Tauri 或 WebView，也不会启动 Fastify、Vite 或 localhost HTTP 服务。

## 运行时结构

- `WAFlow.Desktop`：保留的内部项目名；产物程序集为 `AISalesOS.exe`，提供全部 WPF 原生界面。
- `WAFlow.Core`：保留的内部核心模块名，包含 SQLite、Excel/CSV、评分、DeepSeek、WhatsApp 多账号和持久群发任务调度。
- `WAFlow.WhatsApp.Bridge.exe`：内嵌 Node SEA Windows EXE，通过标准输入输出 JSON-RPC 与主程序通信，不开放本地 HTTP 端口。
- `WAFlow.SmokeTests`：离线核心测试；DeepSeek 使用模拟响应，不访问真实账号或客户。

内部命名与 `%LOCALAPPDATA%\WAFlow` 数据目录有意保留，以兼容既有数据库、Windows 凭据和 WhatsApp 加密会话。产品界面、文件名、版本属性和应用图标均已统一为 AI Sales OS。

## 构建与测试

```powershell
cd "D:\whatsapp 自动化"
.\scripts\test-desktop.ps1
.\scripts\build-desktop.ps1
```

如构建机的 Node.js 或 .NET SDK 不在 PATH，可分别设置 `WAFLOW_NODE_PATH` 和 `WAFLOW_DOTNET_PATH`。发布结果为 `outputs\AI Sales OS.exe`，是 `win-x64` 自包含单文件 EXE。

## 安全与发送边界

- AI 仅调用用户配置的 DeepSeek HTTPS API；失败时保留原始客户数据并标记可重试。
- DeepSeek API Key 和 WhatsApp 会话加密密钥保存在 Windows 凭据管理器。
- 群发任务必须人工批准，发送前再次检查账号连接、E.164 号码、营销同意、退订状态、消息内容和每日上限；发送间隔不作为规避平台风控的承诺。
- 默认只允许个人会话；群组、状态和频道不会进入自动发送队列。
- 非官方个人账号协议存在限制或封号风险，程序不实现规避检测的随机化、指纹或代理功能。
