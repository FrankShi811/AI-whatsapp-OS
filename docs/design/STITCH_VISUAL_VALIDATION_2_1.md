# AI Sales OS 2.1 — Stitch/WPF 视觉验证记录

日期：2026-07-23

## 验证方式

- 构建目标：`WAFlow.Desktop` Release / `win-x64`
- 数据：通过 `WAFLOW_DATABASE_PATH` 使用全新隔离 SQLite 文件
- 运行目标：`desktop/WAFlow.Desktop/bin/Release/net8.0-windows/win-x64/AISalesOS.exe`
- 未覆盖、安装、关闭或启动根目录的用户正式程序
- 检查窗口：约 1400 × 736 的 Windows 桌面窗口

## Dashboard

- 环境光页头能够清晰回答“今天应该做什么”。
- 状态、主行动和页面标题拥有明确左右分区。
- 四张指标卡保持一致高度，阴影和圆角没有影响快速扫描。
- AI 覆盖、等级分布和阶段漏斗仍保持原有数据密度。
- 页面级新手引导能够正常显示并通过 Escape 关闭。

## Lead Intelligence

- 批量分析按钮保持最高操作优先级。
- 搜索/筛选区与数据表格没有因页头升级产生压缩或横向溢出。
- AI Decision Brief 仍以抽屉形式存在，评分环、置信度、阶段和画像可同时读取。
- 普通 CRM 数据和 AI 决策区域拥有不同但一致的视觉语义。

## Customer Intelligence Report

- 页面首屏明确区分报告操作、客户选择、报告画布和 AI Sales Brief。
- Word/PDF/版本对比的禁用状态清晰。
- 空报告状态、证据边界和下一步建议不被环境光覆盖。
- 三栏结构在目标尺寸内完整显示。

## 与 Stitch 参考的差异

有意保留的差异：

- WPF 页头字号比落地页更小，适合高频桌面使用。
- 表格、筛选和抽屉密度高于营销网页。
- 环境渐变使用静态 Brush，不使用网页级大面积模糊动画。
- 仅高价值区域使用玻璃与环境光，避免视觉噪音和 GPU 持续开销。

结论：选定方案已成功转译为原生 WPF 设计系统，关键页面无重大布局差异或业务功能回归，可进入 Windows 自动更新发布流程。
