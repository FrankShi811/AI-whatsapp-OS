# WAFlow Campaign Automation 与 WhatsApp Inbox 执行计划

## 1. 产品目标

在现有 WPF 原生 EXE 中新增两个完整模块：

1. `WhatsApp Inbox`：在 WAFlow 内完成客户会话、消息状态、客户画像和 CRM 字段联动。
2. `Campaign Automation`：按筛选条件选择已同意接收消息的客户，使用 WhatsApp 已批准模板，按北京时间定时并按间隔逐个发送。

EXE 继续使用 WPF/.NET 8 原生窗口，不改成 Electron、WebView 网站壳或 HTTP 前端。

## 2. 官方接入边界

- 生产通道只接 WhatsApp Business Platform Cloud API。
- 账户接入采用 Meta Embedded Signup 或人工填写 WABA ID、Phone Number ID 和访问令牌。
- 如 Meta 对该企业开放 WhatsApp Business App Coexistence，则通过官方 onboarding 流程接入；不实现 WhatsApp Web DOM 注入、非官方 QR 会话接管或逆向协议。
- Cloud API 的主动会话只允许使用已批准的 Message Template；客户最后一次入站消息后 24 小时内才允许自由文本回复。
- Campaign 只发送给有可审计 opt-in 的个人客户；无效号码、已退订、无同意记录、模板变量缺失、频控受限的客户自动排除。
- “群发”定义为对筛选后的个人收件人逐条发送，不向 WhatsApp 群聊或群组 ID 自动发消息。

## 3. 运行架构

```text
WAFlow.Desktop.exe (WPF)
  ├─ WhatsApp Inbox 三栏界面
  ├─ 客户侧栏与 CRM 编辑
  ├─ Campaign 创建/审批/监控
  └─ 本地 SQLite 缓存与审计

WAFlow.Worker (Windows 后台服务，第二步交付)
  ├─ 北京时间排期
  ├─ 逐个收件人发送
  ├─ 重试/暂停/恢复/幂等
  └─ 退订与频控检查

WAFlow Relay (公网 HTTPS，无用户界面)
  ├─ Meta Webhook 验证与签名校验
  ├─ 入站消息/送达/已读/失败事件
  └─ 加密转发给已授权的原生客户端

Meta WhatsApp Cloud API
  ├─ 发送消息
  ├─ 模板管理查询
  └─ Webhook 事件
```

公网 Relay 是 WhatsApp Webhook 的基础设施，不是 WAFlow 的网页版本。用户日常操作仍全部在原生 EXE 中完成。

## 4. 数据模型

### WhatsApp 连接

- `whatsapp_accounts`：WABA、Phone Number ID、显示号码、状态、Graph 版本、连接时间。
- 访问令牌不进 SQLite；使用 Windows Credential Manager。
- `webhook_cursors`：Relay 拉取或推送游标。

### 会话与消息

- `conversations`：客户、号码、最后消息、24 小时窗口截止、未读数、负责人。
- `messages`：Meta message ID、方向、类型、正文、模板、时间、状态、原始事件摘要。
- `message_status_events`：sent/delivered/read/failed 状态历史。
- `webhook_events`：以 Meta event/message ID 幂等去重。

### 客户同意与退订

- `contact_consents`：客户、同意类别、来源、证明、同意时间、撤回时间。
- `suppression_entries`：退订、投诉、人工禁止触达及原因。

### Campaign

- `campaigns`：名称、目的、模板、受众条件、北京时间、间隔、抖动、每日窗口、审批状态、运行状态。
- `campaign_recipients`：客户、号码、变量快照、资格结果、计划时间、发送状态、Meta message ID、错误和重试次数。
- `campaign_runs`：每次启动、暂停、恢复、完成和崩溃恢复记录。

## 5. 原生界面

### WhatsApp Inbox

- 左栏：会话列表、未读、搜索、负责人、阶段和标签筛选。
- 中栏：消息时间线、送达/已读状态、模板或自由文本编辑器。
- 右栏：客户画像、评分、阶段、标签、负责人、Next Action、备注和所有自定义维度。
- 右栏修改直接写入同一 `Lead`，记录审计事件；新入站消息更新活跃时间和客户时间线。

### Campaign Automation

- Step 1：名称、业务目的和已批准模板。
- Step 2：等级/阶段/标签/负责人/导入批次/自定义维度受众筛选。
- Step 3：模板变量映射、客户级预览和排除原因。
- Step 4：北京时间开始时间、每条间隔、随机抖动、每日发送窗口和最大条数。
- Step 5：测试发送、人工审批、启动。
- 运行页：排队、发送、送达、已读、回复、失败、退订；支持暂停、恢复和取消。

## 6. 调度规则

- 时间持久化为 UTC，界面默认 `Asia/Shanghai`，排期时明确显示北京时间。
- 每个收件人生成唯一幂等键，重启后不会重复发送。
- 用户设置基础间隔，调度器可增加随机抖动；收到 Meta 限流、质量或账户错误时自动暂停而不是继续冲击。
- 临时错误指数退避，永久错误直接失败；所有错误保留 Meta code、标题和可执行建议。
- EXE 关闭后仍需继续的任务由 Windows Service 执行；未安装服务时界面明确提示“关闭 WAFlow 将暂停任务”。

## 7. 顺序任务包

### T8.0 架构与迁移

完成本文档、接口契约、SQLite 增量迁移和兼容策略。

### T8.1 Cloud API Provider

实现账户配置、密钥存储、连接测试、模板查询、单条发送和结构化错误。

### T8.2 消息同步

实现会话/消息仓储、Webhook 幂等消费、状态更新和客户号码关联。

### T8.3 WhatsApp Inbox

实现原生三栏界面、消息编辑器、客户侧栏编辑和全系统数据联动。

### T8.4 Campaign Builder

实现受众快照、模板变量、排除预览、北京时间排期、间隔和审批。

### T8.5 Durable Scheduler

实现逐条调度、重试、暂停/恢复、崩溃恢复、退订和频控保护。

### T8.6 Relay

实现并部署公网 HTTPS Webhook Relay，完成签名验证、加密同步和健康检查。

### T8.7 验收发布

使用 Meta 测试号码验证收发、模板 Campaign、状态回执和回复联动，发布新版原生 EXE。

## 8. 验收标准

- 能连接一个 WhatsApp Business Platform 号码并显示真实连接状态。
- 入站消息进入对应客户会话；未知号码创建待认领客户。
- 用户可在 Inbox 中回复，24 小时窗口外强制选择已批准模板。
- 右侧栏编辑姓名、公司、阶段、标签、负责人、备注和自定义维度后，客户列表与商机智能立即一致。
- Campaign 能预览全部收件人和排除原因，人工审批后按北京时间和间隔发送。
- 已退订、无 opt-in、无效号码、变量缺失和重复号码不会发送。
- 进程或电脑重启后任务可恢复且不会重复发送。
- sent/delivered/read/failed/replied 状态写入分析事件并可按 Campaign 汇总。

## 9. 外部前置条件

- Meta Business Portfolio、WhatsApp Business Account、Business Phone Number。
- Meta App 与 `whatsapp_business_messaging` / `whatsapp_business_management` 权限。
- 长期 System User Access Token 或正式 Embedded Signup 配置。
- 一个可由 Meta 访问的公网 HTTPS Webhook 域名与部署环境。
- 用于真实验收的 Meta 测试号码或已批准生产号码、至少一个已批准模板和已同意接收消息的测试联系人。
