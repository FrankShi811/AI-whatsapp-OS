using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;
using WAFlow.Core.Services;

namespace WAFlow.Mac;

public sealed partial class MainWindow
{
    private async Task<Control> BuildSettingsAsync()
    {
        var settings = await _services.Repository.GetAppSettingsAsync(_lifetime.Token);
        var profiles = (settings.ConfiguredAiProviders ?? []).ToDictionary(
            item => item.ProviderId,
            item => item,
            StringComparer.OrdinalIgnoreCase);
        foreach (var definition in AiProviderCatalog.Supported)
            if (!profiles.ContainsKey(definition.Id))
                profiles[definition.Id] = new AiProviderProfile
                {
                    ProviderId = definition.Id,
                    DisplayName = definition.DisplayName,
                    BaseUrl = definition.DefaultBaseUrl,
                    Model = definition.ExampleModels.FirstOrDefault() ?? ""
                };

        var page = PageStack();
        page.Children.Add(PageLead(
            "设置",
            "统一管理 AI Provider、板块模型、界面缩放、主题与本地数据体验。",
            "SYSTEM CONFIGURATION"));

        var providerDefinitions = AiProviderCatalog.Supported.ToList();
        var activeIndex = Math.Max(0, providerDefinitions.FindIndex(item =>
            item.Id.Equals(settings.ActiveProviderId, StringComparison.OrdinalIgnoreCase)));
        var providerBox = new ComboBox
        {
            ItemsSource = providerDefinitions.Select(item => item.DisplayName).ToList(),
            SelectedIndex = activeIndex,
            MinWidth = 260
        };
        var baseUrl = new TextBox();
        var model = new TextBox();
        var apiKey = new TextBox
        {
            PasswordChar = '●',
            Watermark = "留空表示继续使用钥匙串中的现有密钥"
        };
        var modelStatus = BodyText("", Muted, 11);
        var reasoning = new ComboBox
        {
            ItemsSource = new[] { AiReasoningEfforts.Auto }.Concat(AiReasoningEfforts.Ordered).ToList(),
            SelectedItem = AiReasoningEfforts.Normalize(settings.DefaultReasoningEffort)
        };
        var useGlobal = new CheckBox
        {
            Content = "全部模块使用同一 Provider / 模型",
            IsChecked = settings.UseGlobalAiConfiguration
        };

        void LoadProvider()
        {
            var definition = providerDefinitions[Math.Clamp(providerBox.SelectedIndex, 0, providerDefinitions.Count - 1)];
            var profile = profiles[definition.Id];
            baseUrl.Text = string.IsNullOrWhiteSpace(profile.BaseUrl) ? definition.DefaultBaseUrl : profile.BaseUrl;
            model.Text = string.IsNullOrWhiteSpace(profile.Model)
                ? profile.AvailableModels.FirstOrDefault() ?? definition.ExampleModels.FirstOrDefault() ?? ""
                : profile.Model;
            var hasKey = false;
            try { hasKey = !string.IsNullOrWhiteSpace(new MacKeychainSecretStore($"WAFlow/AiProvider/{definition.Id}").Read()); }
            catch { }
            modelStatus.Text = $"{definition.Description} · {(hasKey ? "钥匙串已有 API Key" : "尚未保存 API Key")} · " +
                               $"{profile.AvailableModels.Count:N0} 个已发现模型";
            apiKey.Text = "";
        }
        providerBox.SelectionChanged += (_, _) => LoadProvider();
        LoadProvider();

        var globalForm = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnSpacing = 12,
            RowSpacing = 10
        };
        var globalFields = new Control[]
        {
            Field("AI Provider", providerBox),
            Field("默认推理强度", reasoning),
            Field("Base URL", baseUrl, "必须为 HTTPS；自定义兼容接口也在这里配置。"),
            Field("模型", model),
            Field("API Key", apiKey, "保存后写入 macOS 钥匙串，不进入数据库和日志。")
        };
        for (var index = 0; index < globalFields.Length; index++)
        {
            Grid.SetRow(globalFields[index], index / 2);
            Grid.SetColumn(globalFields[index], index % 2);
            globalForm.Children.Add(globalFields[index]);
        }
        var aiPanel = new StackPanel { Spacing = 12 };
        aiPanel.Children.Add(globalForm);
        aiPanel.Children.Add(modelStatus);
        aiPanel.Children.Add(useGlobal);

        var moduleLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AiModuleKeys.LeadIntelligence] = "商机智能",
            [AiModuleKeys.Customers] = "客户列表 / Customer Brain",
            [AiModuleKeys.WhatsAppInbox] = "WhatsApp Inbox",
            [AiModuleKeys.EmailInbox] = "邮件 Inbox",
            [AiModuleKeys.Campaigns] = "自动化群发",
            [AiModuleKeys.KnowledgeBase] = "知识库",
            [AiModuleKeys.CustomerAnalytics] = "客户智能分析"
        };
        var routeInputs = new Dictionary<string, (ComboBox Provider, TextBox Model, ComboBox Reasoning)>();
        var routePanel = new StackPanel { Spacing = 8 };
        foreach (var moduleKey in AiModuleKeys.Configurable)
        {
            settings.AiModulePreferences.TryGetValue(moduleKey, out var preference);
            var provider = new ComboBox
            {
                ItemsSource = providerDefinitions.Select(item => item.DisplayName).ToList(),
                SelectedIndex = Math.Max(0, providerDefinitions.FindIndex(item =>
                    item.Id.Equals(preference?.ProviderId ?? settings.ActiveProviderId, StringComparison.OrdinalIgnoreCase))),
                MinWidth = 190
            };
            var routeModel = new TextBox
            {
                Text = preference?.Model ?? settings.DeepSeekModel,
                Watermark = "模型 ID"
            };
            var routeReasoning = new ComboBox
            {
                ItemsSource = new[] { AiReasoningEfforts.Auto }.Concat(AiReasoningEfforts.Ordered).ToList(),
                SelectedItem = AiReasoningEfforts.Normalize(preference?.ReasoningEffort ?? settings.DefaultReasoningEffort),
                MinWidth = 120
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("1.1*,1.1*,1.2*,.75*"), ColumnSpacing = 9 };
            row.Children.Add(TextCell(moduleLabels[moduleKey], true));
            Grid.SetColumn(provider, 1);
            row.Children.Add(provider);
            Grid.SetColumn(routeModel, 2);
            row.Children.Add(routeModel);
            Grid.SetColumn(routeReasoning, 3);
            row.Children.Add(routeReasoning);
            routePanel.Children.Add(row);
            routeInputs[moduleKey] = (provider, routeModel, routeReasoning);
        }
        aiPanel.Children.Add(SectionCard("按模块路由", "可选", routePanel));
        var aiActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, HorizontalAlignment = HorizontalAlignment.Right };
        aiActions.Children.Add(ActionButton("发现可用模型", async () =>
        {
            try
            {
                var definition = providerDefinitions[Math.Clamp(providerBox.SelectedIndex, 0, providerDefinitions.Count - 1)];
                var secretStore = new MacKeychainSecretStore($"WAFlow/AiProvider/{definition.Id}");
                var key = string.IsNullOrWhiteSpace(apiKey.Text) ? secretStore.Read() : apiKey.Text.Trim();
                if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("请先填写 API Key。");
                var catalog = await _services.DeepSeek.DiscoverModelsAsync(baseUrl.Text ?? "", key, _lifetime.Token);
                var profile = profiles[definition.Id];
                profile.AvailableModels = catalog.Models.ToList();
                profile.ModelCapabilities = catalog.ModelCapabilities.ToList();
                profile.ModelsFetchedAt = catalog.FetchedAt;
                modelStatus.Text = $"已发现 {catalog.Models.Count:N0} 个模型：{string.Join("、", catalog.Models.Take(12))}";
                if (string.IsNullOrWhiteSpace(model.Text)) model.Text = catalog.Models.FirstOrDefault() ?? "";
            }
            catch (Exception error) { await ShowMessageAsync("模型发现失败", error.Message); }
        }));
        aiActions.Children.Add(ActionButton("保存 AI 设置", async () =>
        {
            try
            {
                var definition = providerDefinitions[Math.Clamp(providerBox.SelectedIndex, 0, providerDefinitions.Count - 1)];
                if (!Uri.TryCreate(baseUrl.Text, UriKind.Absolute, out var uri) ||
                    !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Base URL 必须是有效的 HTTPS 地址。");
                if (string.IsNullOrWhiteSpace(model.Text)) throw new InvalidOperationException("请填写模型 ID。");
                var profile = profiles[definition.Id];
                profile.DisplayName = definition.DisplayName;
                profile.BaseUrl = baseUrl.Text.Trim().TrimEnd('/');
                profile.Model = model.Text.Trim();
                if (!profile.AvailableModels.Contains(profile.Model, StringComparer.OrdinalIgnoreCase))
                    profile.AvailableModels.Insert(0, profile.Model);
                var providerStore = new MacKeychainSecretStore($"WAFlow/AiProvider/{definition.Id}");
                if (!string.IsNullOrWhiteSpace(apiKey.Text)) providerStore.Save(apiKey.Text.Trim());
                profile.IsConfigured = !string.IsNullOrWhiteSpace(providerStore.Read());
                settings.ActiveProviderId = definition.Id;
                settings.DeepSeekBaseUrl = profile.BaseUrl;
                settings.DeepSeekModel = profile.Model;
                settings.DefaultReasoningEffort = AiReasoningEfforts.Normalize(reasoning.SelectedItem?.ToString());
                settings.UseGlobalAiConfiguration = useGlobal.IsChecked == true;
                settings.ConfiguredAiProviders = profiles.Values
                    .Where(item => item.IsConfigured || item.ProviderId.Equals(definition.Id, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => providerDefinitions.FindIndex(def => def.Id.Equals(item.ProviderId, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                settings.AiModulePreferences = routeInputs.ToDictionary(
                    item => item.Key,
                    item =>
                    {
                        var selectedProvider = providerDefinitions[Math.Clamp(item.Value.Provider.SelectedIndex, 0, providerDefinitions.Count - 1)];
                        return new AiModuleModelPreference
                        {
                            ProviderId = selectedProvider.Id,
                            Model = item.Value.Model.Text?.Trim() ?? "",
                            ReasoningEffort = AiReasoningEfforts.Normalize(item.Value.Reasoning.SelectedItem?.ToString())
                        };
                    },
                    StringComparer.OrdinalIgnoreCase);
                await _services.Repository.SaveAppSettingsAsync(settings, _lifetime.Token);
                var activeKey = providerStore.Read();
                if (!string.IsNullOrWhiteSpace(activeKey)) _services.Secrets.Save(activeKey);
                await _services.LeadAutomation.NotifyProviderConfiguredAsync(_lifetime.Token);
                AiStateText.Text = "● AI 已配置";
                await ShowMessageAsync("AI 设置已保存", $"{definition.DisplayName} · {profile.Model} 已成为当前全局配置。");
                await RenderCurrentPageAsync();
            }
            catch (Exception error) { await ShowMessageAsync("无法保存 AI 设置", error.Message); }
        }, primary: true));
        aiPanel.Children.Add(aiActions);
        page.Children.Add(SectionCard("AI Provider 与模型路由", "macOS Keychain", aiPanel));

        var theme = new ComboBox
        {
            ItemsSource = new[] { "System", "Light", "Dark" },
            SelectedItem = string.IsNullOrWhiteSpace(settings.ThemeMode) ? "System" : settings.ThemeMode,
            MinWidth = 180
        };
        var scale = new ComboBox
        {
            ItemsSource = new[] { 80, 90, 100, 110, 125 },
            SelectedItem = new[] { 80, 90, 100, 110, 125 }
                .OrderBy(value => Math.Abs(value - settings.UiScalePercentage))
                .First(),
            MinWidth = 180
        };
        var appearance = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        appearance.Children.Add(Field("主题模式", theme, "跟随系统、浅色或深色。保存后立即应用。"));
        var scaleField = Field("界面缩放", scale, "80% / 90% 适合低分辨率；110% / 125% 可提升可读性。");
        Grid.SetColumn(scaleField, 1);
        appearance.Children.Add(scaleField);
        var saveAppearance = ActionButton("保存外观设置", async () =>
        {
            settings.ThemeMode = theme.SelectedItem?.ToString() ?? "System";
            settings.UiScalePercentage = scale.SelectedItem is int value ? value : 100;
            await _services.Repository.SaveAppSettingsAsync(settings, _lifetime.Token);
            MacThemeManager.Apply(settings.ThemeMode);
            ApplyUiScale(settings.UiScalePercentage);
            await ShowMessageAsync("外观设置已保存", "主题与界面缩放已立即应用。");
        });
        var appearancePanel = new StackPanel { Spacing = 12 };
        appearancePanel.Children.Add(appearance);
        appearancePanel.Children.Add(saveAppearance);
        page.Children.Add(SectionCard("外观与可读性", "立即生效", appearancePanel));

        var dataPanel = new StackPanel { Spacing = 10 };
        dataPanel.Children.Add(BodyText($"数据库：{_services.Repository.DatabasePath}", Ink, 12));
        dataPanel.Children.Add(BodyText($"WhatsApp 会话：{PlatformDataPaths.WhatsAppSessionsDirectory}", Ink, 12));
        dataPanel.Children.Add(BodyText("没有共享客户数据库、跨用户同步或自动上传；备份文件也由您自行保管。", Muted, 11));
        var dataActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        dataActions.Children.Add(ActionButton("在 Finder 中显示", () =>
        {
            if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("/usr/bin/open", [Path.GetDirectoryName(_services.Repository.DatabasePath)!])
                {
                    UseShellExecute = false
                });
            return Task.CompletedTask;
        }));
        dataActions.Children.Add(ActionButton("备份本地数据库", async () =>
        {
            var destination = await PickSaveFileAsync(
                "备份 AI Sales OS 本地数据库",
                $"AI-Sales-OS-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db",
                ".db");
            if (string.IsNullOrWhiteSpace(destination)) return;
            File.Copy(_services.Repository.DatabasePath, destination, true);
            await ShowMessageAsync("数据库已备份", destination);
        }));
        dataPanel.Children.Add(dataActions);
        page.Children.Add(SectionCard("本地数据与隐私", "LOCAL ONLY", dataPanel));

        var updatePanel = new StackPanel { Spacing = 9 };
        updatePanel.Children.Add(BodyText($"当前版本：{_updates.State.CurrentVersion}"));
        updatePanel.Children.Add(BodyText(_updates.State.Message, Ink, 12));
        updatePanel.Children.Add(ActionButton("检查 macOS 更新", async () =>
        {
            await _updates.CheckAndDownloadAsync(force: true);
            await ShowMessageAsync("更新检查完成", _updates.State.Message);
        }));
        page.Children.Add(SectionCard("版本与更新", "独立 macOS 通道", updatePanel));
        return page;
    }
}
