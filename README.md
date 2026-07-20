# AI Sales OS Windows 原生版

直接双击 `AI Sales OS.exe` 运行。当前版本为 1.2.2，主要数据继续保存在 `%LOCALAPPDATA%\WAFlow\waflow.db`，因此从 WAFlow 升级后仍会读取原有客户、账号、Campaign 和设置。

这是 WPF/.NET 8 自包含单文件 EXE，不是 Electron、Tauri 或 WebView 套壳，不启动 localhost HTTP 服务。最终用户无需安装 Node.js、npm、浏览器或 .NET Runtime；WhatsApp 桥接程序已作为 Windows EXE 嵌入主程序。

## 主要能力

- 原生 Dashboard、商机智能、客户列表、WhatsApp 草稿、WhatsApp Inbox 和 Campaign Automation。
- 多个个人 WhatsApp 账号扫码登录；账号的会话、消息与 Campaign 队列相互隔离。
- Inbox 提供会话列表、正常单聊和可编辑 CRM 客户侧栏，保存后联动整个系统。
- Campaign 支持受众筛选、动态字段、北京时间排期、发送间隔、每日上限、暂停/恢复、失败重试和发送审计。
- 非标准表头作为自定义维度保存；不同客户可以拥有不同维度，客户列表按实际数据动态显示。
- `.xlsx`、UTF-8/GB18030 CSV 单文件资源保护上限为 200MB，不设固定行数上限；大批量数据在后台解析并按批次写入 SQLite。
- AI 分析和话术生成使用用户配置的 DeepSeek API，不需要 OpenAI API Key。

## 品牌资源

- 主程序：`AI Sales OS.exe`
- 透明 Logo：`desktop\WAFlow.Desktop\Assets\AI-Sales-OS.png`
- Windows 多尺寸图标：`desktop\WAFlow.Desktop\Assets\AI-Sales-OS.ico`
- 独立 PNG 图标：`desktop\WAFlow.Desktop\Assets\Icons`

ICO 内含 16、20、24、32、40、48、64、128 和 256 像素图标，并已写入主 EXE、主窗口、设置窗口和导入窗口。

## 构建与测试

```powershell
cd "D:\whatsapp 自动化"
.\scripts\test-desktop.ps1
.\scripts\build-desktop.ps1
```

发布结果：`outputs\AI Sales OS.exe`，并同步复制到项目根目录。

## 风险边界

个人账号连接基于非官方 WhatsApp Web 多设备协议，不属于 Meta/WhatsApp Business Platform，可能发生重新验证、限制、登出或封号。AI Sales OS 不提供规避风控、指纹伪装、代理轮换或绕过平台限制的能力。请只向已经明确同意接收营销消息且未退订的联系人发送。
