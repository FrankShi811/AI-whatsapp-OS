# AI Sales OS Visual Experience V2

## 当前 UI 审计

- 原有 WPF 页面边界和业务事件清晰，适合在不改写业务逻辑的前提下升级体验。
- 主要问题是页面视觉权重接近、筛选与表格缺少层级、Dashboard 偏统计而非行动、AI 结果以长文本为主、浅色硬编码较多。
- Inbox 已具备会话、附件、回执、客户侧栏等完整能力，但会话、AI 判断和 CRM 编辑之间缺少视觉上的决策顺序。
- 自动化页面功能完整，但模板、任务、受众和安全规则同时出现，首次扫描成本高。

## 体验策略

核心工作流统一为：`今日优先事项 → AI 判断 → 人工执行 → 结果反馈`。

- Dashboard 首屏先回答“今天应该做什么”。
- 商机智能必须同时展示分数、置信度、证据、风险和下一步，而不是只展示等级。
- 客户列表作为统一 Customer Graph，上传表格的动态字段保持一等公民。
- Inbox 把会话、客户上下文和 AI Sales Brief 放在同一屏幕。
- Campaign 使用 01–04 的渐进式任务编排语言，并保留人工批准和 IP 安全阀。

## Signal Glass 视觉系统

- 语义颜色：Primary 表示安全执行，AI Accent 表示模型判断，Warning 表示需人工注意，Danger 表示阻断或失败。
- 主题：System / Light / Dark 三种持久化模式；所有页面共享动态资源。
- 层级：Canvas、Surface、Elevated Card、AI Card 四层，玻璃感只用于 AI 决策层和浮动命令面板。
- 排版：Segoe UI Variable Text / Display，8px 基础栅格，紧凑表格与更高对比标题并存。
- 组件：Metric Card、AI Card、Grade Donut、Score Ring、Lead Radar、状态胶囊、命令面板、统一按钮/输入/表格/标签页。
- 动效：页面进入采用短距离淡入位移；按钮提供即时按压与悬停反馈；批量 AI、同步和发送继续使用真实进度状态。

## 页面实现

- Dashboard：今日动作、AI 覆盖、ABCD 分布、阶段漏斗、优先商机、群发质量。
- Lead Intelligence：双栏决策工作区、Score Ring、六维雷达、置信度、行为证据、Next Best Action、风险复核。
- Customer List：主搜索、精细筛选、动态维度、批量选择与删除上下文条。
- WhatsApp Inbox：三栏会话工作区、CRM Live Sync、AI Sales Brief、自然消息与媒体操作。
- Campaign Automation：任务指标、01–04 编排步骤、动态字段、精准受众、历史与质量。
- Settings：AI Provider、模型目录、主题模式、本地数据边界。

## 交互与可访问性

- `Ctrl+K` 打开快速操作，`Ctrl+1…5` 切换主要模块，`Esc` 关闭命令层。
- 颜色不作为唯一状态载体；关键状态同时保留文字、数字或图标。
- 保留标准 Windows 标题栏、最小化、最大化与关闭行为。
- 动画控制在 130–220ms，服务于位置和状态理解，不持续占用注意力。
