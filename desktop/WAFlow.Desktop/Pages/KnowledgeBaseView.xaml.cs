using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Desktop.Windows;

namespace WAFlow.Desktop.Pages;

public partial class KnowledgeBaseView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private List<KnowledgeDocument> _documents = [];
    private KnowledgeDocument? _selected;
    private KnowledgeCandidate? _selectedCandidate;
    private bool _refreshing;

    public event EventHandler? DataChanged;

    public KnowledgeBaseView(AppServices services)
    {
        InitializeComponent();
        _services = services;
        StatusFilter.ItemsSource = new[] { new StatusOption("全部状态", null) }
            .Concat(Enum.GetValues<KnowledgeDocumentStatus>().Select(value => new StatusOption(KnowledgeLabels.Status(value), value)))
            .ToList();
        StatusFilter.SelectedIndex = 0;
        ScopeFilter.ItemsSource = new[] { new ScopeOption("全部范围", null) }
            .Concat(Enum.GetValues<KnowledgeScopeKind>().Select(value => new ScopeOption(ScopeLabel(value), value)))
            .ToList();
        ScopeFilter.SelectedIndex = 0;
        MetadataCategoryBox.ItemsSource = Enum.GetValues<KnowledgeCategory>()
            .Select(value => new CategoryOption(KnowledgeLabels.Category(value), value)).ToList();
        MetadataUsageBox.ItemsSource = new[]
        {
            new UsageOption("表达风格参考", KnowledgeUsageMode.StyleReference),
            new UsageOption("原文模板", KnowledgeUsageMode.ExactTemplate),
            new UsageOption("政策参考", KnowledgeUsageMode.PolicyReference),
            new UsageOption("分析参考", KnowledgeUsageMode.AnalysisReference)
        };
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var selectedId = _selected?.Id;
            _documents = await _services.KnowledgeBase.GetDocumentsAsync();
            CandidateGrid.ItemsSource = await _services.KnowledgeLearning.RefreshCandidatesAsync();
            ApplyFilters();
            if (!string.IsNullOrWhiteSpace(selectedId))
            {
                var match = _documents.FirstOrDefault(item => item.Id == selectedId);
                if (match is not null) DocumentList.SelectedItem = match;
            }
            LibraryStatusText.Text = _documents.Count == 0
                ? "尚未上传知识。文件只会保存在本机。"
                : $"已启用 {_documents.Count(item => item.Status == KnowledgeDocumentStatus.Active)} · 待审核 {_documents.Count(item => item.Status == KnowledgeDocumentStatus.ReadyForReview)} · 冲突 {_documents.Count(item => item.Status == KnowledgeDocumentStatus.Conflicted)}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyFilters()
    {
        var query = SearchBox.Text.Trim();
        var status = (StatusFilter.SelectedItem as StatusOption)?.Value;
        var scope = (ScopeFilter.SelectedItem as ScopeOption)?.Value;
        var visible = _documents.Where(document =>
            (status is null || document.Status == status) &&
            (scope is null || document.Scope.Kind == scope) &&
            (query.Length == 0 ||
             document.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             document.OriginalFileName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             document.Summary.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             document.Tags.Any(tag => tag.Contains(query, StringComparison.CurrentCultureIgnoreCase))))
            .ToList();
        DocumentList.ItemsSource = visible;
        DocumentCountText.Text = $"{visible.Count:N0} 项";
    }

    private async void DocumentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = DocumentList.SelectedItem as KnowledgeDocument;
        UpdateButtonState();
        if (_selected is null)
        {
            ClearDetail();
            return;
        }
        await LoadDetailAsync(_selected);
    }

    private async Task LoadDetailAsync(KnowledgeDocument document)
    {
        DetailTitleText.Text = document.Title;
        DetailMetaText.Text = $"{document.CategoryLabel} · {document.ScopeLabel} · {document.VersionLabel} · {document.StatusLabel}";
        MetadataTitleBox.Text = document.Title;
        MetadataCategoryBox.SelectedItem = (MetadataCategoryBox.ItemsSource as IEnumerable<CategoryOption>)
            ?.FirstOrDefault(item => item.Value == document.Category);
        MetadataUsageBox.SelectedItem = (MetadataUsageBox.ItemsSource as IEnumerable<UsageOption>)
            ?.FirstOrDefault(item => item.Value == document.UsageMode);
        MetadataTagsBox.Text = string.Join("，", document.Tags);
        EffectiveFromPicker.SelectedDate = document.EffectiveFrom?.LocalDateTime;
        EffectiveUntilPicker.SelectedDate = document.EffectiveUntil?.LocalDateTime;
        SummaryText.Text = string.IsNullOrWhiteSpace(document.Summary) ? "暂无摘要。" : document.Summary;
        ProcessingText.Text =
            $"状态：{document.StatusLabel}\n文件：{document.OriginalFileName}\n语言：{document.DetectedLanguage}\n知识块：{document.ChunkCount}\n来源层：{document.SourceKind} / {document.EvidenceLevel}" +
            (string.IsNullOrWhiteSpace(document.ProcessingError) ? "" : $"\n处理说明：{document.ProcessingError}");
        RiskBanner.Visibility = document.RiskFlags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RiskText.Text = document.RiskFlags.Count == 0 ? "" : string.Join("\n", document.RiskFlags.Select(flag => $"• {flag}"));

        var versions = await _services.KnowledgeBase.GetVersionsAsync(document.Id);
        VersionGrid.ItemsSource = versions.Select(version => new VersionRow(
            $"V{version.Version}",
            version.OriginalFileName,
            version.Sha256.Length <= 16 ? version.Sha256 : version.Sha256[..16] + "…",
            version.ParserName,
            version.ChunkCount,
            version.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"))).ToList();
        var current = versions.FirstOrDefault(version => version.Id == document.CurrentVersionId) ?? versions.FirstOrDefault();
        PreviewTextBox.Text = current is null
            ? "尚无可预览版本。"
            : string.IsNullOrWhiteSpace(current.ExtractedText)
                ? string.Join("\n", current.Warnings.DefaultIfEmpty("当前版本未提取到文本。"))
                : current.ExtractedText;
        var chunks = await _services.KnowledgeBase.GetChunksAsync(document.Id, document.CurrentVersionId);
        ChunkGrid.ItemsSource = chunks.Select(chunk => new ChunkRow(
            chunk.Ordinal + 1,
            chunk.Locator,
            chunk.Heading,
            chunk.Content,
            string.Join("、", chunk.Keywords.Take(8)))).ToList();
        ConflictGrid.ItemsSource = await _services.KnowledgeBase.GetConflictsAsync(document.Id);
    }

    private void ClearDetail()
    {
        DetailTitleText.Text = "选择知识文件";
        DetailMetaText.Text = "查看版本、原文、知识块、冲突与检索结果";
        SummaryText.Text = "选择文件后显示";
        ProcessingText.Text = "";
        PreviewTextBox.Text = "";
        ChunkGrid.ItemsSource = null;
        VersionGrid.ItemsSource = null;
        ConflictGrid.ItemsSource = null;
        RiskBanner.Visibility = Visibility.Collapsed;
    }

    private void UpdateButtonState()
    {
        var selected = _selected;
        DownloadButton.IsEnabled = selected is not null;
        NewVersionButton.IsEnabled = selected is not null;
        DeleteButton.IsEnabled = selected is not null;
        SaveMetadataButton.IsEnabled = selected is not null;
        DisableButton.IsEnabled = selected?.Status == KnowledgeDocumentStatus.Active;
        ActivateButton.IsEnabled = selected?.CanActivate == true;
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择业务知识文件",
            Filter = "支持的知识文件|*.pdf;*.docx;*.txt;*.md;*.markdown;*.xlsx;*.csv;*.pptx;*.html;*.htm;*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|所有文件|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        await UploadFileAsync(dialog.FileName, null);
    }

    private async void NewVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var dialog = new OpenFileDialog
        {
            Title = $"为“{_selected.Title}”选择新版本",
            Filter = "支持的知识文件|*.pdf;*.docx;*.txt;*.md;*.markdown;*.xlsx;*.csv;*.pptx;*.html;*.htm;*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|所有文件|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        await UploadFileAsync(dialog.FileName, _selected);
    }

    private async Task UploadFileAsync(string path, KnowledgeDocument? existing)
    {
        var settings = new KnowledgeUploadWindow(_services, Path.GetFileName(path), existing)
        {
            Owner = Window.GetWindow(this)
        };
        if (settings.ShowDialog() != true || settings.Options is null) return;
        SetBusy(true, "正在安全检查、解析、分块并建立索引…");
        try
        {
            var processed = await _services.KnowledgeBase.UploadAsync(path, settings.Options);
            await RefreshAsync();
            DocumentList.SelectedItem = _documents.FirstOrDefault(item => item.Id == processed.Id);
            DataChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show(
                processed.Status == KnowledgeDocumentStatus.Failed
                    ? $"文件原件已安全保留，但解析失败：{processed.ProcessingError}"
                    : processed.ChunkCount == 0
                        ? $"文件已保留并进入人工审核：{processed.ProcessingError}"
                        : $"解析完成，共生成 {processed.ChunkCount} 个知识块。请审核摘要、风险和作用域后再启用。",
                "知识库",
                MessageBoxButton.OK,
                processed.Status == KnowledgeDocumentStatus.Failed ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "知识上传失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, "");
        }
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var confirm = MessageBox.Show(
            $"启用后，符合“{_selected.ScopeLabel}”的 AI 流程可以检索当前 V{_selected.CurrentVersion}。\n\n请确认已经人工核对原文、摘要、风险和作用域。",
            "审核并启用知识",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            await _services.KnowledgeBase.ActivateAsync(_selected.Id);
            await RefreshSelectedAsync();
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "知识未启用", MessageBoxButton.OK, MessageBoxImage.Warning);
            await RefreshSelectedAsync();
        }
    }

    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        await _services.KnowledgeBase.DisableAsync(_selected.Id);
        await RefreshSelectedAsync();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (MessageBox.Show(
                "删除后文档立即退出检索，但原件、版本和审计记录仍保留，避免无法追溯。是否继续？",
                "删除知识",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _services.KnowledgeBase.DeleteAsync(_selected.Id);
        _selected = null;
        await RefreshAsync();
        ClearDetail();
        UpdateButtonState();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            var source = await _services.KnowledgeBase.GetOriginalPathAsync(_selected.Id);
            var dialog = new SaveFileDialog
            {
                Title = "保存知识原件副本",
                FileName = _selected.OriginalFileName,
                Filter = "原始文件|*" + Path.GetExtension(_selected.OriginalFileName)
            };
            if (dialog.ShowDialog() != true) return;
            File.Copy(source, dialog.FileName, true);
            MessageBox.Show("知识原件副本已保存。", "知识库", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "下载原件失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SaveMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var category = (MetadataCategoryBox.SelectedItem as CategoryOption)?.Value ?? _selected.Category;
        var usage = (MetadataUsageBox.SelectedItem as UsageOption)?.Value ?? _selected.UsageMode;
        var tags = MetadataTagsBox.Text.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            await _services.KnowledgeBase.UpdateReviewMetadataAsync(
                _selected.Id,
                MetadataTitleBox.Text,
                category,
                usage,
                tags,
                EffectiveFromPicker.SelectedDate is { } from
                    ? new DateTimeOffset(from, TimeZoneInfo.Local.GetUtcOffset(from))
                    : null,
                EffectiveUntilPicker.SelectedDate is { } until
                    ? new DateTimeOffset(until.Date.AddDays(1).AddTicks(-1), TimeZoneInfo.Local.GetUtcOffset(until))
                    : null);
            await RefreshSelectedAsync();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "保存审核信息失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ResolveConflict_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || ConflictGrid.SelectedItem is not KnowledgeConflict conflict)
        {
            MessageBox.Show("请选择一项未解决冲突。", "知识冲突", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (conflict.Status != KnowledgeConflictStatus.Open)
        {
            MessageBox.Show("该冲突已经处理。", "知识冲突", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(
                $"确认“{_selected.Title}”是本次应保留的资料？\n\n另一份冲突资料会保持停用，当前资料回到待审核状态；不会自动启用。",
                "人工解决知识冲突",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _services.KnowledgeBase.ResolveConflictAsync(conflict.Id, _selected.Id);
            await RefreshSelectedAsync();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "冲突处理失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RetrievalTest_Click(object sender, RoutedEventArgs e)
    {
        var query = RetrievalQueryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            MessageBox.Show("请输入要验证的业务问题。", "检索测试", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var result = await _services.KnowledgeRetrieval.RetrieveAsync(new KnowledgeRetrievalRequest
            {
                Query = query,
                AccountId = RetrievalAccountBox.Text.Trim(),
                CustomerId = RetrievalCustomerBox.Text.Trim(),
                ConversationId = RetrievalConversationBox.Text.Trim(),
                UsageContext = "knowledge_retrieval_test",
                Limit = 12,
                MinimumScore = 0.12
            });
            RetrievalSummaryBorder.Visibility = Visibility.Visible;
            RetrievalSummaryText.Text = result.SufficientToAnswer
                ? $"可引用 {result.Hits.Count} 个知识块；检索 ID：{result.Id}。结果只表示相关性，不代表业务因果或自动批准。"
                : $"知识不足：{result.InsufficiencyReason} 检索 ID：{result.Id}";
            RetrievalGrid.ItemsSource = result.Hits.Select(hit => new RetrievalRow(
                hit.RelevanceScore.ToString("P0"),
                hit.CitationLabel,
                hit.Scope.Label,
                hit.Content)).ToList();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "检索测试失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CandidateGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedCandidate = CandidateGrid.SelectedItem as KnowledgeCandidate;
        ApproveCandidateButton.IsEnabled = _selectedCandidate?.Status == KnowledgeCandidateStatus.Proposed;
        RejectCandidateButton.IsEnabled = _selectedCandidate?.Status is KnowledgeCandidateStatus.Proposed or KnowledgeCandidateStatus.Approved;
        PublishCandidateButton.IsEnabled = _selectedCandidate?.Status == KnowledgeCandidateStatus.Approved;
    }

    private async void ApproveCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCandidate is null) return;
        await _services.KnowledgeLearning.ReviewAsync(_selectedCandidate.Id, true);
        CandidateGrid.ItemsSource = await _services.KnowledgeLearning.RefreshCandidatesAsync();
        CandidateGrid_SelectionChanged(this, null!);
    }

    private async void RejectCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCandidate is null) return;
        await _services.KnowledgeLearning.ReviewAsync(_selectedCandidate.Id, false);
        CandidateGrid.ItemsSource = await _services.KnowledgeLearning.RefreshCandidatesAsync();
        CandidateGrid_SelectionChanged(this, null!);
    }

    private async void PublishCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCandidate is null) return;
        try
        {
            var document = await _services.KnowledgeBase.PublishCandidateAsync(_selectedCandidate.Id);
            await RefreshAsync();
            DocumentList.SelectedItem = _documents.FirstOrDefault(item => item.Id == document.Id);
            MessageBox.Show(
                "候选已转换为待审核知识。请再次核对作用域、原文、样本证据和风险后再启用。",
                "知识候选",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "候选发布失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RefreshSelectedAsync()
    {
        var id = _selected?.Id;
        await RefreshAsync();
        if (!string.IsNullOrWhiteSpace(id))
        {
            _selected = _documents.FirstOrDefault(item => item.Id == id);
            DocumentList.SelectedItem = _selected;
            if (_selected is not null) await LoadDetailAsync(_selected);
        }
        UpdateButtonState();
    }

    private void SetBusy(bool busy, string message)
    {
        UploadButton.IsEnabled = !busy;
        NewVersionButton.IsEnabled = !busy && _selected is not null;
        if (!string.IsNullOrWhiteSpace(message)) LibraryStatusText.Text = message;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyFilters();
    }

    private static string ScopeLabel(KnowledgeScopeKind value) => value switch
    {
        KnowledgeScopeKind.Global => "全局",
        KnowledgeScopeKind.Account => "账号",
        KnowledgeScopeKind.Customer => "客户",
        KnowledgeScopeKind.Conversation => "会话",
        KnowledgeScopeKind.Temporary => "临时",
        _ => value.ToString()
    };

    private sealed record StatusOption(string Label, KnowledgeDocumentStatus? Value);
    private sealed record ScopeOption(string Label, KnowledgeScopeKind? Value);
    private sealed record CategoryOption(string Label, KnowledgeCategory Value);
    private sealed record UsageOption(string Label, KnowledgeUsageMode Value);
    private sealed record ChunkRow(int Ordinal, string Locator, string Heading, string Content, string KeywordsLabel);
    private sealed record VersionRow(string VersionLabel, string FileName, string HashShort, string Parser, int ChunkCount, string CreatedAt);
    private sealed record RetrievalRow(string ScoreLabel, string Citation, string Scope, string Content);
}
