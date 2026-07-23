# AI Sales OS 2.0 — UI Audit 与 Figma Design System

## 交付范围

本文件记录 2.0.0 的 Phase 1（UI Audit）与 Phase 2（Figma Design System）正式交付。
Figma 文件：[AI Sales OS — 2026 Product Experience Redesign](https://www.figma.com/design/btHMngWNxgiJ0coDkK256g)

本轮不改写 CRM、WhatsApp、邮件、AI Provider、自动化、SQLite 或自动更新业务逻辑。设计系统通过 WPF 全局资源、复用组件和主题映射进入产品，所有升级继续保留 `%LOCALAPPDATA%\WAFlow\waflow.db`。

## Phase 1 — Current UI Audit

### 总体结论

当前产品已经具备完整的 AI 销售操作系统信息架构，不需要重做导航或拆散业务流程。主要问题集中在：

1. 语义颜色仍混有旧版本硬编码，AI、等级、状态与消息颜色没有完全由主题资源统一管理。
2. 字号、控件高度、圆角和留白存在多套尺度，跨页面切换时视觉节奏不完全一致。
3. AI 分析已有评分、置信度、雷达、证据和下一步动作，但缺少正式的组件契约，后续迭代容易产生视觉漂移。
4. 深色主题存在，但旧颜色并非与浅色主题一一对应的语义系统。
5. 动效需要遵循“帮助理解而非装饰”的原则，并尊重 Windows 的减少动画设置。

### 页面审计

| 页面 | 已有优势 | 2.0 设计方向 |
| --- | --- | --- |
| Dashboard | 指标、等级分布、阶段漏斗、优先商机和触达质量齐全 | 作为 Sales Command Center，强化“今天先做什么”和 AI 覆盖状态 |
| Lead Intelligence | 评分、置信度、六维雷达、证据、风险与 Next Action 完整 | 统一 AI Score Ring、Confidence Meter、Reasoning Trace 和等级颜色 |
| Customer List | 动态字段、筛选、批量选择和高密度表格适合真实客户数据 | 保留高密度工作台，使用语义状态和优先级视觉，避免传统表格感 |
| WhatsApp Inbox | 会话、聊天、CRM 客户抽屉、AI 助理和消息状态已联动 | 统一消息气泡、AI 建议和客户脑组件，保持自然的日常沟通体验 |
| Email Inbox | 与 CRM 共用客户资料，结构与 WhatsApp Inbox 对齐 | 复用同一收件箱框架和 Customer Intelligence 视觉语言 |
| Automation | 模板、动态字段、客户选择、计划、节奏、安全阀和历史完整 | 使用 Workflow Node 与执行状态颜色表达 AI Workflow Engine |
| Customer Analysis | 多阶段报告、版本、导出与管理层摘要完整 | 采用咨询报告层级、事实/判断/建议分层和风险视觉 |
| Settings / API | 模型发现、连接测试、密钥和健康状态完整 | 作为 AI Control Center，突出连接状态、所选模型和故障边界 |

## Phase 2 — Figma Design System

### Figma 结构

- Foundations：`0:1`
- Components：`5:2`
- Product Screens：`5:3`
- Foundation board：`6:16`
- 变量：120
- 文字样式：10
- 效果样式：4

Figma Starter 仅允许每个变量集合一个 mode，因此浅色与深色语义分别存放在 `Semantic Light` 和 `Semantic Dark` 集合中；WPF 运行时由 `ThemeManager` 映射到同一组 DynamicResource。

### 核心组件节点

| 组件 | Figma Node |
| --- | --- |
| Button | `7:47` |
| AI Score Ring | `8:31` |
| AI Confidence Meter | `8:58` |
| AI Reasoning Trace | `9:104` |
| Customer Priority Card | `10:78` |
| Message Bubble | `11:36` |
| Workflow Node | `12:66` |

### 视觉语义

| 语义 | Light | Dark | 用途 |
| --- | --- | --- | --- |
| Canvas | `#EFF4F2` | `#08130F` | 应用背景 |
| Surface | `#FFFFFF` | `#10221C` | 卡片与主要工作区 |
| Raised Surface | `#FFFFFF` | `#1D3029` | 抽屉、浮层和高优先级卡片 |
| Text Primary | `#08130F` | `#F7FAF9` | 标题与关键数据 |
| Text Secondary | `#4A5D56` | `#C5D3CE` | 正文与说明 |
| Execution / Primary | `#0C9A70` | `#16B889` | 确认、执行、成功路径 |
| AI Accent | `#7868FF` | `#B9AEFF` | AI 判断、推理和生成 |
| AI Processing | `#31C8E5` | `#62D9EF` | AI 处理中状态 |
| Warning | `#E0A12B` | `#F0B94F` | 风险与待确认 |
| Danger | `#E35D5D` | `#F57D7D` | 失败、删除和高风险 |
| Info / Grade B | `#4E8CF7` | `#75A9FF` | 信息状态与 B 级 |

ABCD 等级固定为：A `#16B889`、B `#4E8CF7`、C `#E0A12B`、D `#83958E`。
WhatsApp 自己发送使用 `ChatOutbound`，客户消息使用 `ChatInbound`；两者在浅色和深色主题下分别适配。

### 排版

- Page title：28 / 38，Bold
- Section title：18 / 28，SemiBold
- Body：13 / 21
- Label：12 / 18
- Micro：11 / 16
- Metric：30，Bold，使用显示数字字体
- 中文优先：Segoe UI Variable Text / Microsoft YaHei UI
- 指标数字：Segoe UI Variable Display / Segoe UI

### 空间、圆角和深度

- 页面外边距：30
- 卡片内边距：20–22
- 主卡片圆角：16–18
- 控件圆角：10–12
- 标准控件高度：40
- Card Shadow：低对比、22 blur
- Floating Shadow：34 blur，仅用于浮层
- AI Glow：26 blur，仅用于 AI 高价值内容，不用于普通表格

### WPF 组件映射

| Figma 契约 | WPF 资源 / 控件 |
| --- | --- |
| Holographic Card | `HolographicCard` |
| AI Confidence Meter | `ConfidenceMeter` |
| AI Reasoning Trace | `ReasoningStepCard` |
| Customer Priority Card | `PriorityCard` |
| Message Bubble / inbound | `InboundMessageBubble` |
| Message Bubble / outbound | `OutboundMessageBubble` |
| Workflow Node | `WorkflowNodeCard` |
| AI Score Ring | `Controls.ScoreRing` |

`ScoreRing` 在数值变化时使用 360ms 的 ease-out 动画，只在评分变化时运行；Windows 关闭客户端动画时直接显示最终结果。

## 可访问性与性能约束

1. 不使用循环装饰动画；AI 动效必须表示分析、进度或状态变化。
2. DataGrid 保持行、列虚拟化，不能因卡片视觉降低大量客户时的滚动性能。
3. 所有主操作具备键盘焦点边框；危险操作使用文字与颜色双重表达。
4. 深浅主题使用同一语义键，不允许页面直接判断主题。
5. 新组件必须优先复用全局资源，禁止重新硬编码品牌色。
6. 2.0.0 的回归测试必须验证设计 token、主题映射、版本一致性和业务冒烟测试。

## 后续视觉路线

Phase 3 将在 Figma MCP 配额可用后继续完成 Product Screens 的完整页面稿与交互原型；Phase 4 才进行更大范围的页面级视觉替换。2.0.0 已完成 Phase 1、Phase 2 以及不破坏业务的 WPF 设计系统落地，为后续页面级迭代提供稳定基线。
