# AI Sales OS Windows 原生版

直接双击 `AI Sales OS.exe` 运行。当前版本为 1.11.0，主要数据继续保存在 `%LOCALAPPDATA%\WAFlow\waflow.db`，因此升级后仍会读取原有客户、账号、群发任务和设置。

这是 WPF/.NET 8 自包含单文件 EXE，不是 Electron、Tauri 或 WebView 套壳，不启动 localhost HTTP 服务。最终用户无需安装 Node.js、npm、浏览器或 .NET Runtime；WhatsApp 桥接程序已作为 Windows EXE 嵌入主程序。

## 主要能力

- 原生 Dashboard、商机智能、客户列表、WhatsApp Inbox 和 WhatsApp 自动化群发。
- 多个个人 WhatsApp 账号扫码登录；账号的会话、消息与 Campaign 队列相互隔离。
- Inbox 接收手机提供的历史同步包、会话和 WhatsApp 联系人，支持按姓名或号码实时搜索；电话或 WhatsApp 原表字段会与会话号码安全关联，客户侧栏与客户列表双向联动。
- 对话区按“自己发送的绿色气泡靠右、客户消息白色气泡靠左”显示；Enter 发送、Ctrl+Enter 换行，并支持不超过 100MB 的常见图片、视频、音频、Office、PDF 和压缩文件。
- 发出消息显示发送中、服务器已接收、已送达、已读或失败状态；单双灰勾和双蓝勾与 WhatsApp 回执联动，并保存发送、送达和已读时间。
- Inbox 每 60 秒显示公网出口 IP 和大致位置；存在运行或排期群发时，安全阀门每 10 秒并在每次发送前复核任务基线 IP。IP 不一致会停止所有账号的自动触达，保存停止位置并弹窗汇总成功、失败、跳过和待发送数量。
- 会话按最新消息自动排序；右键可置顶或取消置顶，并通过已连接的个人 WhatsApp 会话同步到手机端。
- WhatsApp 自动化群发支持持久话术模板、与客户列表/商机智能/导入原表严格对齐的动态字段、客户单选或多选、即时或北京时间定时任务、按秒或分钟间隔、每日上限、暂停/恢复和发送审计。
- 群发页提供独立“发送历史与质量”统计表，显示任务总数、成功、失败、跳过、取消、待发送、完成进度、成功率、安全停止位置和原因。
- 首次启动显示五步蒙层引导，只要求完成 API 对接：填写 DeepSeek 或兼容 AI 接口的 Base URL、API Key，并从 `/models` 自动拉取的列表选择工作模型。程序不再提供或要求企业销售资料。右上角“使用引导”可随时重新打开。
- 导入时只需选择文件和工作表，不再逐列映射；原表每一列都按原表头保存，常用字段自动联动到 CRM。
- 不同客户可以拥有不同维度；客户列表按原始列顺序横向显示，长表头不会竖排，双击客户可逐项编辑全部系统字段和原表维度。
- `.xlsx`、UTF-8/GB18030 CSV 单文件资源保护上限为 200MB，不设固定行数上限；大批量数据在后台解析并按批次写入 SQLite。
- AI 分析和话术生成使用用户选择的 DeepSeek 或 OpenAI Chat Completions 兼容模型，不需要 OpenAI API Key。
- 新导入及尚未完成 AI 分析的客户统一为 D 级、0 分；导入和人工编辑不再运行本地规则评分。WhatsApp 客户新回复会捕捉报价、数量、交期、需求、积极/延后/异议等信号并进入 AI 队列，只有结构校验成功的 AI 结果才能更新商机智能和 Dashboard 等级分布。
- 回复关键词只用于帮助模型定位证据，不会单独改变等级；现有八因素结构暂时保留，后续可按用户提供的评分标准和商机模板替换。

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

WhatsApp 的完整历史包通常只在“首次关联设备”时下发。如果某个账号已经被旧版 AI Sales OS 扫码关联，而旧版没有保存历史事件，升级后请在 Inbox 点击“退出账号”，再点击“连接 / 显示二维码”重新扫码一次。以后收到的联系人、会话和历史消息会写入本地 SQLite，重启软件不会丢失；“同步联系人与历史”用于拉取当前会话变更和重放本次运行已接收的数据。

如果加密登录凭据损坏或 Windows 凭据密钥与本机会话不匹配，1.7.0 会把不可读取的会话目录改名备份，并自动进入重新扫码流程，不会删除客户列表、CRM 资料或已经同步到 SQLite 的消息。

公网 IP 检测会向 ipify 查询当前出口 IP，并仅在首次检测或 IP 变化时向 ipwho.is 查询国家、地区和城市等近似位置；这些服务不会收到 AI Sales OS 中的客户、消息或 DeepSeek 数据。IP 位置仅供网络变化提醒，不能判断 WhatsApp 是否会封禁账号。
