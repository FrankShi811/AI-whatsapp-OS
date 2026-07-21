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
        new("1.16.1", "2026-07-21", "跨平台自动更新正式验证",
        [
            "完成 GitHub Release 首次发布与后续版本更新链路验证。",
            "Windows、Apple Silicon 和 Intel 分别使用独立 Velopack 更新通道。",
            "内置开源中文 PDF 字体，避免不同电脑缺少系统字体导致报告导出失败。"
        ], true),
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
