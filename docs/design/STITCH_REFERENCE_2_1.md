# AI Sales OS 2.1 — Stitch 视觉参考评估

## 输入

- 来源：Google Stitch 项目 `3242300623870117673`
- 下载包：`stitch_remix_of_auralis_saas_landing_page.zip`
- 内容：12 套 Auralis SaaS 落地页变体，每套包含 HTML 和屏幕截图

该包不是 AI Sales OS 的完整桌面产品稿，也不能直接作为 WPF 页面使用。它的价值是提供可验证的视觉方向与细节参考。

## 选定方案

采用 `auralis_ai_voice_infrastructure_refined_alignment_transparency` 作为主参考。

选择理由：

1. 信息层级最清晰，标题、说明、关键指标和行动按钮之间的优先级稳定。
2. 半透明表面与环境光较克制，适合高频使用的企业桌面应用。
3. 大留白和非对称布局能够突出 AI 决策区域，不会变成游戏化或霓虹界面。
4. 组件圆角、边界、阴影和状态胶囊易于映射到原生 WPF。

## 转译规则

### 保留

- 温和中性的应用画布
- 半透明高层表面
- 低强度紫、青、绿环境光
- 强对比标题与宽松的页面头部
- 关键 AI 区域使用不同于普通 CRM 卡片的视觉语言
- 8 px 基础间距、柔和圆角和低对比边界

### 不直接复制

- 不嵌入 HTML、Tailwind 或浏览器容器
- 不使用网页落地页的超大字体比例
- 不使用持续循环的装饰动画
- 不在每张卡片上叠加阴影或渐变
- 不牺牲数据表格密度、键盘效率和滚动性能

## WPF 映射

- `GlassCard`：常规高层信息容器
- `AmbientHeroCard`：页面级任务定位和主行动区域
- `IntelligenceGlassCard`：AI 推理、画像与建议
- `ElevatedMetricCard`：Dashboard 核心指标
- `AuroraAmbient`：静态低成本环境渐变
- `AuroraBorder`：AI 区域的渐变边界

所有效果均使用原生 WPF Brush、Border 和有限 DropShadowEffect 实现，不改动 CRM、WhatsApp、AI Provider、SQLite 或自动化业务逻辑。

## macOS 发布边界

macOS 安装包默认停止构建。GitHub Actions 只有在仓库变量 `ENABLE_MACOS_RELEASE=true` 时才运行 Mac 预览构建；恢复前必须具备有效 Developer ID、签名与公证条件，并获得用户明确通知。
