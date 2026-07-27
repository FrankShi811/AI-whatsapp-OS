using System.IO;
using System.Windows;
using System.Windows.Controls;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Windows;

public partial class KnowledgeUploadWindow : Window
{
    private readonly AppServices _services;
    private readonly KnowledgeDocument? _existing;
    private List<CustomerOption> _customers = [];

    public KnowledgeUploadOptions? Options { get; private set; }

    public KnowledgeUploadWindow(
        AppServices services,
        string fileName,
        KnowledgeDocument? existing = null)
    {
        InitializeComponent();
        _services = services;
        _existing = existing;
        FileNameText.Text = fileName;
        TitleBox.Text = existing?.Title ?? Path.GetFileNameWithoutExtension(fileName);
        CategoryBox.ItemsSource = Enum.GetValues<KnowledgeCategory>()
            .Select(value => new CategoryOption(KnowledgeLabels.Category(value), value)).ToList();
        CategoryBox.SelectedItem = (CategoryBox.ItemsSource as IEnumerable<CategoryOption>)
            ?.FirstOrDefault(item => item.Value == existing?.Category) ?? CategoryBox.Items[0];
        UsageModeBox.ItemsSource = new[]
        {
            new UsageOption("表达风格参考（默认）", KnowledgeUsageMode.StyleReference),
            new UsageOption("原文模板（仅限人工明确标记）", KnowledgeUsageMode.ExactTemplate),
            new UsageOption("政策参考", KnowledgeUsageMode.PolicyReference),
            new UsageOption("分析参考", KnowledgeUsageMode.AnalysisReference)
        };
        UsageModeBox.SelectedItem = (UsageModeBox.ItemsSource as IEnumerable<UsageOption>)
            ?.FirstOrDefault(item => item.Value == existing?.UsageMode) ?? UsageModeBox.Items[0];
        ScopeKindBox.ItemsSource = Enum.GetValues<KnowledgeScopeKind>()
            .Select(value => new ScopeOption(ScopeLabel(value), value)).ToList();
        ScopeKindBox.SelectedItem = (ScopeKindBox.ItemsSource as IEnumerable<ScopeOption>)
            ?.FirstOrDefault(item => item.Value == existing?.Scope.Kind) ?? ScopeKindBox.Items[0];
        AccountIdBox.Text = existing?.Scope.AccountId ?? "";
        ConversationIdBox.Text = existing?.Scope.ConversationId ?? "";
        TemporaryTaskIdBox.Text = existing?.Scope.TemporaryTaskId ?? "";
        Loaded += Window_Loaded;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _customers = (await _services.Repository.GetLeadsAsync())
            .Select(lead => new CustomerOption(
                $"{lead.DisplayName} · {(string.IsNullOrWhiteSpace(lead.Company) ? "未填写公司" : lead.Company)} · {lead.Id}",
                lead.Id))
            .ToList();
        CustomerBox.ItemsSource = _customers;
        if (!string.IsNullOrWhiteSpace(_existing?.Scope.CustomerId))
            CustomerBox.SelectedItem = _customers.FirstOrDefault(item => item.Id == _existing.Scope.CustomerId);
        ScopeKind_SelectionChanged(this, null!);
    }

    private void ScopeKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var kind = (ScopeKindBox.SelectedItem as ScopeOption)?.Value ?? KnowledgeScopeKind.Global;
        AccountBindingPanel.Visibility = kind is KnowledgeScopeKind.Account or KnowledgeScopeKind.Conversation
            ? Visibility.Visible : Visibility.Collapsed;
        CustomerBindingPanel.Visibility = kind == KnowledgeScopeKind.Customer
            ? Visibility.Visible : Visibility.Collapsed;
        ConversationBindingPanel.Visibility = kind == KnowledgeScopeKind.Conversation
            ? Visibility.Visible : Visibility.Collapsed;
        TemporaryBindingPanel.Visibility = kind == KnowledgeScopeKind.Temporary
            ? Visibility.Visible : Visibility.Collapsed;
        ScopeHelpText.Text = kind switch
        {
            KnowledgeScopeKind.Global => "对全部客户与账号可见。仅适合通用且经过批准的政策、SOP 和产品知识。",
            KnowledgeScopeKind.Account => "只允许指定 WhatsApp 账号检索，其他账号不可见。",
            KnowledgeScopeKind.Customer => "只允许指定客户使用；可按姓名搜索并选择客户。",
            KnowledgeScopeKind.Conversation => "只允许指定账号下的指定会话使用，必须同时填写账号 ID 和会话 ID。",
            KnowledgeScopeKind.Temporary => "只允许显式携带同一任务 / 运行 ID 的一次性流程使用，不进入长期客户记忆。",
            _ => ""
        };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            MessageBox.Show("请填写知识标题。", "知识上传", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var scopeKind = (ScopeKindBox.SelectedItem as ScopeOption)?.Value ?? KnowledgeScopeKind.Global;
        var customerId = (CustomerBox.SelectedItem as CustomerOption)?.Id;
        if (string.IsNullOrWhiteSpace(customerId) && scopeKind == KnowledgeScopeKind.Customer)
        {
            var typed = CustomerBox.Text.Trim();
            customerId = _customers.FirstOrDefault(item =>
                item.Label.Equals(typed, StringComparison.CurrentCultureIgnoreCase))?.Id ?? typed;
        }
        var scope = new KnowledgeScope
        {
            Kind = scopeKind,
            AccountId = AccountIdBox.Text.Trim(),
            CustomerId = customerId ?? "",
            ConversationId = ConversationIdBox.Text.Trim(),
            TemporaryTaskId = TemporaryTaskIdBox.Text.Trim()
        };
        var missing = scopeKind switch
        {
            KnowledgeScopeKind.Account when string.IsNullOrWhiteSpace(scope.AccountId) => "请填写账号 ID。",
            KnowledgeScopeKind.Customer when string.IsNullOrWhiteSpace(scope.CustomerId) => "请选择客户。",
            KnowledgeScopeKind.Conversation when string.IsNullOrWhiteSpace(scope.AccountId) ||
                                                   string.IsNullOrWhiteSpace(scope.ConversationId) => "会话作用域必须同时填写账号 ID 和会话 ID。",
            KnowledgeScopeKind.Temporary when string.IsNullOrWhiteSpace(scope.TemporaryTaskId) => "请填写任务 / 运行 ID。",
            _ => ""
        };
        if (!string.IsNullOrWhiteSpace(missing))
        {
            MessageBox.Show(missing, "知识上传", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Options = new KnowledgeUploadOptions
        {
            ExistingDocumentId = _existing?.Id ?? "",
            Title = title,
            Category = (CategoryBox.SelectedItem as CategoryOption)?.Value,
            SourceKind = _existing?.SourceKind ?? KnowledgeSourceKind.ApprovedDocument,
            UsageMode = (UsageModeBox.SelectedItem as UsageOption)?.Value ?? KnowledgeUsageMode.StyleReference,
            ExactTemplate = (UsageModeBox.SelectedItem as UsageOption)?.Value == KnowledgeUsageMode.ExactTemplate,
            Scope = scope,
            EffectiveFrom = _existing?.EffectiveFrom,
            EffectiveUntil = _existing?.EffectiveUntil
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string ScopeLabel(KnowledgeScopeKind value) => value switch
    {
        KnowledgeScopeKind.Global => "全局知识",
        KnowledgeScopeKind.Account => "账号专属",
        KnowledgeScopeKind.Customer => "客户专属",
        KnowledgeScopeKind.Conversation => "会话专属",
        KnowledgeScopeKind.Temporary => "临时任务",
        _ => value.ToString()
    };

    private sealed record CategoryOption(string Label, KnowledgeCategory Value);
    private sealed record UsageOption(string Label, KnowledgeUsageMode Value);
    private sealed record ScopeOption(string Label, KnowledgeScopeKind Value);
    private sealed record CustomerOption(string Label, string Id);
}
