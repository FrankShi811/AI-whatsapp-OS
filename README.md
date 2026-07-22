# AI Sales OS Windows 原生版

直接双击 `AI Sales OS.exe` 运行。当前版本为 1.17.1，主要数据继续保存在 `%LOCALAPPDATA%\WAFlow\waflow.db`，因此升级后仍会读取原有客户、账号、邮件历史、自动化任务、分析报告和设置。

这是 WPF/.NET 8 自包含单文件 EXE，不是 Electron、Tauri 或 WebView 套壳，不启动 localhost HTTP 服务。最终用户无需安装 Node.js、npm、浏览器或 .NET Runtime；WhatsApp 桥接程序已作为 Windows EXE 嵌入主程序。

## 主要能力

- 原生 Dashboard、商机智能、客户列表、WhatsApp Inbox、邮件 Inbox、多渠道自动化触达和客户智能分析中心。
- 邮件 Inbox 支持 Gmail、Outlook / Microsoft 365、Yahoo、iCloud 和自定义 IMAP / SMTP；邮件地址自动匹配 CRM 客户，收发历史保存在本地，未匹配联系人可从客户侧栏创建客户。
- 客户智能分析会按“数据整理 → 事实提取 → 商业分析 → 销售策略 → 报告生成”分阶段调用所选 AI 模型，整合 CRM、完整 WhatsApp 历史、Lead Intelligence、自动化触达和客户轨迹，生成中文为主且事实、AI 判断、销售建议明确分离的专业报告。
- 每位客户的报告按版本永久保留，可重新分析、查看历史和对比版本；支持带封面、评分卡、图表、页码、证据台账和管理层摘要的原生 `.docx` 与 `.pdf` 导出。
- 多个个人 WhatsApp 账号扫码登录；账号的会话、消息与 Campaign 队列相互隔离。
- 已连接账号可在 WhatsApp Inbox 点击“＋ 建群”，填写群名并从已同步联系人中单选或多选成员，也可手动添加国际号码；确认后会通过当前个人账号真实创建 WhatsApp 群组并同步到手机端。建群不会进入自动化群发队列。
- Inbox 接收手机提供的历史同步包、会话和 WhatsApp 联系人，支持按姓名或号码实时搜索；电话或 WhatsApp 原表字段会与会话号码安全关联，客户侧栏与客户列表双向联动。
- 对话区按“自己发送的绿色气泡靠右、客户消息白色气泡靠左”显示；Enter 发送、Ctrl+Enter 换行，并支持不超过 100MB 的常见图片、视频、音频、Office、PDF 和压缩文件。
- 发出消息显示发送中、服务器已接收、已送达、已读或失败状态；单双灰勾和双蓝勾与 WhatsApp 回执联动，并保存发送、送达和已读时间。
- Inbox 每 60 秒显示公网出口 IP 和大致位置；存在运行或排期群发时，安全阀门每 10 秒并在每次发送前复核任务基线 IP。IP 不一致会停止所有账号的自动触达，保存停止位置并弹窗汇总成功、失败、跳过和待发送数量。
- 会话按最新消息自动排序；右键可置顶或取消置顶，并通过已连接的个人 WhatsApp 会话同步到手机端。
- 多渠道自动化触达支持 WhatsApp 与邮件任务、持久话术模板、邮件主题模板、与客户列表/商机智能/导入原表严格对齐的动态字段、客户单选或多选、即时或北京时间定时任务、按秒或分钟间隔、每日上限、暂停/恢复和发送审计。
- 自动化页提供独立“发送历史与质量”统计表，按 WhatsApp / 邮件渠道显示任务总数、成功、失败、跳过、取消、待发送、完成进度、成功率、安全停止位置和原因。
- 首次启动显示整套新手入门；Dashboard、商机智能、客户列表、WhatsApp Inbox、自动化群发、客户智能分析和 API 对接在第一次进入时还会分别显示本模块教程。每套教程同时解释模块用途、主要功能和操作步骤，关闭后右上角“本页使用手册”仍可随时重新打开。首次配置只要求 API 对接，不要求企业资料。
- 导入时只需选择文件和工作表，不再逐列映射；原表每一列都按原表头保存，常用字段自动联动到 CRM。
- 不同客户可以拥有不同维度；客户列表按原始列顺序横向显示，长表头不会竖排，双击客户可逐项编辑全部系统字段和原表维度。
- `.xlsx`、UTF-8/GB18030 CSV 单文件资源保护上限为 200MB，不设固定行数上限；大批量数据在后台解析并按批次写入 SQLite。
- AI 分析和话术生成使用用户选择的 DeepSeek 或 OpenAI Chat Completions 兼容模型，不需要 OpenAI API Key。
- 新导入及尚未完成 AI 分析的客户统一为 D 级、0 分；导入和人工编辑不运行本地规则评分。WhatsApp 客户新回复会把原始消息与历史上下文送入 AI 队列，只有结构校验成功的 V2 结果才能更新商机智能和 Dashboard 等级分布。
- Lead Intelligence V2 的基础画像由付费营销意愿 25、供应链稳定性 20、电商基础 15、私域/流量 15、已有销售能力 15、素材准备度 10 六维组成，并叠加 -20 到 +20 的 WhatsApp 行为修正分；本地只校验算术、范围、原因和证据完整性，不使用关键词给客户加减分。
- 每次启动会先校验 SQLite 并保存最近 10 个完整备份；发现可安全重建的页面或索引损坏时，先归档损坏原件，再恢复全部可读取表并显示恢复数量，避免初始化直接失败。
- 安装版启动时自动检查公开 GitHub Release；发现新版本后在后台下载，左下角版本中心显示当前/最新版本、进度和更新日志。只有用户点击“安装更新并重启”后才应用更新，不需要自建服务器。

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
.\scripts\build-velopack-release.ps1 -Version 1.16.0 -RepositoryUrl "https://github.com/FrankShi811/AI-whatsapp-OS"
```

发布结果始终为项目根目录的固定文件 `AI Sales OS.exe`。后续构建只覆盖该文件，不再生成带版本号或 `outputs` 目录中的重复 EXE；程序使用稳定的 `AI.Sales.OS.Desktop` AppUserModelID 保持 Windows 任务栏身份不变。

Windows 安装包固定输出为 `dist\installers\AI Sales OS Setup.exe`，后续构建同样覆盖旧文件。它是 Velopack 管理的正式安装入口；旧便携版或旧 Inno 安装版只需手动安装这一次，后续即可在程序内完成更新。GitHub Release 发布、打包、数据边界和升级测试见 `docs\GITHUB_RELEASE_UPDATES.md`。

## macOS 原生中文测试版

仓库已新增 `desktop\WAFlow.Mac` Avalonia 原生客户端，输出 Apple 芯片和 Intel 两个 Mach-O `.app`、中文 `.pkg` 安装包及 portable ZIP，不是 Electron、WebView 或改扩展名的 Windows 文件。两个架构均自包含 .NET 运行时，并分别使用 `osx-arm64` / `osx-x64` GitHub Release 更新通道。

测试版已包含中文 Dashboard、商机智能、客户列表、WhatsApp 历史、自动化任务、客户智能分析、API 对接、macOS Keychain 保存，以及 GitHub Release 自动检查、后台下载和确认后安装重启。它尚未使用 Apple Developer ID 签名/公证，且原生 Mac WhatsApp Bridge 尚未完成，因此扫码、实时收发、建群和真实群发默认不可用。本轮按用户要求先交付包结构，由用户在真实 Mac 上人工安装测试。详细步骤和边界见 `docs\MACOS_NATIVE_PORT.md`。

## 风险边界

个人账号连接基于非官方 WhatsApp Web 多设备协议，不属于 Meta/WhatsApp Business Platform，可能发生重新验证、限制、登出或封号。AI Sales OS 不提供规避风控、指纹伪装、代理轮换或绕过平台限制的能力。请只向已经明确同意接收营销消息且未退订的联系人发送。

WhatsApp 的完整历史包通常只在“首次关联设备”时下发。如果某个账号已经被旧版 AI Sales OS 扫码关联，而旧版没有保存历史事件，升级后请在 Inbox 点击“退出账号”，再点击“连接 / 显示二维码”重新扫码一次。以后收到的联系人、会话和历史消息会写入本地 SQLite，重启软件不会丢失；“同步联系人与历史”用于拉取当前会话变更和重放本次运行已接收的数据。

如果加密登录凭据损坏或 Windows 凭据密钥与本机会话不匹配，1.7.0 会把不可读取的会话目录改名备份，并自动进入重新扫码流程，不会删除客户列表、CRM 资料或已经同步到 SQLite 的消息。

公网 IP 检测会向 ipify 查询当前出口 IP，并仅在首次检测或 IP 变化时向 ipwho.is 查询国家、地区和城市等近似位置；这些服务不会收到 AI Sales OS 中的客户、消息或 DeepSeek 数据。IP 位置仅供网络变化提醒，不能判断 WhatsApp 是否会封禁账号。
