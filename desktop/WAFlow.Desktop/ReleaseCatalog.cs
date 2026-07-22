using System.Reflection;

namespace WAFlow.Desktop;

public sealed record ReleaseNote(string Version, string Date, string Title, IReadOnlyList<string> Changes, bool IsCurrent = false);

public static class ReleaseCatalog
{
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "1.15.0";

    public static IReadOnlyList<ReleaseNote> History { get; } =
    [
        new("1.18.1", "2026-07-22", "更新通道、WhatsApp 动态与未读状态修复",
        [
            "更新检查改为读取 GitHub Release 静态 Velopack 清单，不再调用容易触发匿名限流的 GitHub Releases API。",
            "WhatsApp 最新动态与普通私聊消息分型显示；动态在客户会话顶部保留 24 小时，并明确标记为非普通聊天消息。",
            "WhatsApp 动态不再写入客户最近回复、商机评分证据、AI 会话助理或客户智能报告。",
            "会话已读时间写入本地数据库；切换板块或重新同步后，手机端旧未读计数不会恢复已经清除的气泡。"
        ], true),
        new("1.18.0", "2026-07-22", "WhatsApp AI 会话助理",
        [
            "WhatsApp Inbox 输入区新增 AI 会话助理，可使用已配置模型读取客户白色气泡并生成可编辑回复。",
            "AI 同步提取中文需求摘要、客户意向、采购信号、风险和下一步动作；字段更新必须引用真实客户原话。",
            "发送前提供回复与 CRM 字段预览，可选择只填入输入框或发送并同步；未匹配联系人可在确认后建立客户档案。",
            "AI 提取结果写入统一客户动态字段和审计轨迹，供商机智能重跑及客户智能报告继续使用。",
            "WhatsApp Inbox 独立新手引导已补充 AI 助理操作步骤与数据边界。"
        ]),
        new("1.17.2", "2026-07-22", "AI 分析与正式安装通道稳定版",
        [
            "修复 GitHub 将安装资产空格规范化为点号后，便携版无法识别正式 Setup / PKG 的问题。",
            "延续结构化 AI 自动纠错、当前资料报告降级、失败原因细分与八个板块独立新手引导。",
            "Windows、Apple Silicon 和 Intel Mac 的中文安装包重新生成并通过 GitHub Release 分发。"
        ]),
        new("1.17.1", "2026-07-22", "AI 结构化分析与便携版更新修复",
        [
            "AI 结构化输出增加兼容解析、自动纠错重试和严格证据归一化，资料不足不再被误判为程序失败。",
            "客户智能报告可基于当前全部可用资料生成；AI 阶段异常时使用安全降级结果，并明确标记信息缺口，后续可生成新版本。",
            "商机智能失败状态区分 AI 格式、API 限流、网络与配置原因，便于判断为何需要重试。",
            "便携版可检查 GitHub Release 并下载正式安装包；完成一次正式安装后进入标准自动更新通道。",
            "逐项校验 Dashboard、商机智能、客户列表、WhatsApp Inbox、邮件 Inbox、自动化触达、客户智能分析和 API 设置的独立新手引导。"
        ]),
        new("1.17.0", "2026-07-22", "邮件 Inbox 与多渠道销售自动化",
        [
            "新增邮件 Inbox，支持 Gmail、Outlook / Microsoft 365、Yahoo、iCloud 和自定义 IMAP / SMTP 账户连接、收取、发送与本地历史保存。",
            "邮箱地址与客户列表、商机智能及客户情报报告使用同一客户资料；未匹配邮件联系人可在侧栏创建客户。",
            "自动化任务支持 WhatsApp 与邮件两种渠道、动态客户字段、邮件主题模板、定时或即时发送，并在历史中分渠道统计成功与失败。",
            "WhatsApp 联系人同步结果改为诊断信息，不再把本地联系人缓存缺失误判为号码不存在，也不再因此阻止真实发送。"
        ]),
        new("1.16.2", "2026-07-22", "群发真实回执与失败统计修复",
        [
            "群发成功改为以 WhatsApp 服务器回执为准，不再把本地调用受理误记为成功。",
            "异步失败回执会同步修正任务收件人、成功率、失败数和历史任务统计。",
            "发送前校验号码是否注册 WhatsApp；旧版错误成功记录在启动时自动对账修复。"
        ]),
        new("1.16.1", "2026-07-21", "跨平台自动更新正式验证",
        [
            "完成 GitHub Release 首次发布与后续版本更新链路验证。",
            "Windows、Apple Silicon 和 Intel 分别使用独立 Velopack 更新通道。",
            "内置开源中文 PDF 字体，避免不同电脑缺少系统字体导致报告导出失败。"
        ]),
        new("1.16.0", "2026-07-21", "GitHub Release 自动更新",
        [
            "启动自动检查 GitHub Release，发现新版本后在后台自动下载。",
            "左下角版本中心显示当前版本、最新版本、更新日志和下载状态。",
            "用户确认后自动安装并重启，应用升级不覆盖本地客户数据、WhatsApp 账号或配置。"
        ]),
        new("1.15.0", "2026-07-21", "版本中心、WhatsApp 建群与安装交付",
        [
            "左下角改为当前版本入口，可随时查看版本更新历史。",
            "WhatsApp Inbox 增加建群入口、成员选择、号码校验和真实群组创建。",
            "新增 Windows 安装包流程，自动优先推荐可用的非系统盘。"
        ]),
        new("1.14.0", "2026-07-21", "分板块新手引导",
        [
            "Dashboard、商机智能、客户列表、WhatsApp Inbox、自动化群发、客户智能分析和 API 设置拥有独立教程。",
            "各板块首次进入自动展示，右上角长期保留本页使用手册。"
        ]),
        new("1.13.0", "2026-07-21", "AI 原生销售工作台视觉升级",
        [
            "统一 Dashboard、客户列表、商机智能、Inbox 和自动化群发的信息层级与视觉系统。",
            "增加明暗主题、AI 评分可视化、快捷操作和高密度企业级组件。"
        ]),
        new("1.12.0", "2026-07-21", "Lead Intelligence V2",
        [
            "升级 AI 驱动的六维客户评分、行为信号和结构化证据。",
            "新导入客户保持 D 级 / 0 分，只有 AI 分析成功后才更新。",
            "支持批量分析、失败重试和 Dashboard 等级联动。"
        ]),
        new("1.11.0", "2026-07-20", "WhatsApp Inbox 与自动化稳定性",
        [
            "修复历史同步、媒体预览、发送回执、消息回复与撤回。",
            "加入公网 IP 风险监测、群发安全阀门和执行历史。",
            "改进模型发现、API 对接和本地数据库恢复。"
        ])
    ];
}
