# 客户外部调查模块

## 运行边界

该模块原生运行于 AI Sales OS 的 WPF/.NET 8、`WAFlow.Core` 与本地 SQLite 架构中。它不是独立 Electron/Python 服务，不上传整库，不依赖开发者中转服务器，也不会把搜索结果直接覆盖客户主档。

- 搜索：用户自己的 Tavily / Brave API Key，或用户主动启动的本地 SearXNG。
- 网页读取：只允许公开 HTTP/HTTPS；每次重定向重新校验 DNS，拒绝本机、内网、链路本地、保留地址和云元数据地址。
- AI：沿用现有板块模型路由与本机 Key；严格结构化 JSON，首次失败只允许一次修复。默认不开启可能计费的 AI 分析；只有用户同时开启付费请求、开启板块 AI 分析、设置正月预算与每任务预算预留后才会联网调用。
- 事实门禁：只有 `Verified` 或 `HumanConfirmed` 且未过期事实进入 Customer Brain 与客户智能报告。
- 成本：默认月预算 `$0`、`AllowPaidRequests=false`、`AllowAiAnalysisRequests=false`。搜索适配器只使用基础端点；额度耗尽后切换下一 Provider 或停止，不会自动进入付费模式。联网前先持久化最坏重试次数的本地额度/预算预留，完成后按实际 HTTP 尝试数结算；进程中断时宁可保守占用，也不静默重放可能已计费的请求。

## 持久化与恢复

启动时在同一 SQLite 事务内创建并验证以下表：

1. `customer_enrichment_jobs`
2. `customer_enrichment_queries`
3. `customer_enrichment_sources`
4. `customer_enrichment_facts`
5. `customer_enrichment_fact_sources`
6. `customer_enrichment_reviews`
7. `customer_enrichment_provider_usage`
8. `customer_enrichment_settings`

迁移完成前会验证必要列、索引、复合主体外键、事实—来源同任务/同客户约束及 `foreign_key_check`。任一验证失败即回滚整个模块迁移；应用现有 `DatabaseStartupGuard` 仍会在初始化前建立健康备份并处理数据库恢复。

后台任务在 SQLite 中持久化。程序异常退出后，尚未预留任何外部请求的 `Running` 任务可安全恢复入队；已有联网前预留的任务会标记为 `Failed/RECOVERY_REVIEW_REQUIRED`，避免静默重复调用，用户核对用量后可手动强制刷新。已取消任务不会恢复。默认保留模块记录 730 天，清理只作用于已结束的调查任务与历史用量；当前自然月用量账本永不因短保留周期提前删除，也不触碰客户、消息、账号、知识、报告或其他业务数据。

## 调查流程

1. 从统一客户档案规范化企业邮箱与 E.164 电话，并生成身份 Hash。
2. 按固定顺序生成最多 6 条最小化商业查询；敏感属性查询在发送前拒绝。
3. 按 Tavily → Brave → SearXNG 顺序检索；错误、额度耗尽、熔断和切换写入本地用量与审计。
4. 每条查询最多保留 8 个结果，每位客户最多读取 12 个网页；URL 与正文 Hash 去重。
5. 以完整邮箱/电话、企业域名、姓名+公司、国家和商业语境进行确定性主体评分。只匹配姓名或电话号码末 8 位永远不能自动核验；邮箱/电话冲突进入冲突状态。
6. AI 只能从已保存来源中提取公开商业事实；每条事实必须引用来源 ID 和可在来源原文找到的证据。
7. 销售人员可确认、编辑确认、拒绝或标记过期；事实更新、事实—来源链接与审核记录在同一事务提交。编辑确认不会把支持旧值的公开引文冒充为新值证据：新值改为独立人工审核来源，原来源仅保留在任务与审核历史中。

## 可选本地 SearXNG

参见 [`infrastructure/searxng/README.md`](../infrastructure/searxng/README.md)。AI Sales OS 不会自动安装或启动该组件。
