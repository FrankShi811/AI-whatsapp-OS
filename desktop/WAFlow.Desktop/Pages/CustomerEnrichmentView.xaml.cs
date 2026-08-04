using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Pages;

public partial class CustomerEnrichmentView : UserControl, IRefreshableView
{
    private const int FactsPerPage = 3;
    private readonly AppServices _services;
    private readonly ObservableCollection<CustomerRow> _customers = [];
    private readonly ObservableCollection<FactRow> _facts = [];
    private readonly ObservableCollection<SourceRow> _sources = [];
    private readonly DispatcherTimer _pollTimer;
    private readonly CustomerEnrichmentService _enrichmentService;
    private List<Lead> _allLeads = [];
    private Dictionary<string, CustomerEnrichmentQueueSummary> _queueSummaries = new(StringComparer.OrdinalIgnoreCase);
    private List<FactRow> _filteredFacts = [];
    private CustomerEnrichmentSnapshot _snapshot = new();
    private Lead? _selectedLead;
    private FactRow? _selectedFact;
    private SourceRow? _selectedSource;
    private CancellationTokenSource? _selectionCancellation;
    private int _selectionGeneration;
    private bool _loaded;
    private bool _refreshing;
    private bool _polling;
    private bool _renderingFactPage;
    private bool _evidenceExpanded = true;
    private bool _providerAvailable;
    private int _factPage;
    private string _availabilityMessage = "正在检查联网调查状态";
    private bool _queueSummaryRefreshRunning;
    private bool _queueSummaryRefreshPending;

    public event EventHandler? DataChanged;
    public event EventHandler? ImportRequested;
    public event EventHandler? SettingsRequested;

    public CustomerEnrichmentView(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _enrichmentService = services.CustomerEnrichment;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _pollTimer.Tick += PollTimer_Tick;
        CustomerList.ItemsSource = _customers;
        FactGrid.ItemsSource = _facts;
        SourceList.ItemsSource = _sources;
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        SetPageLoading(true, "正在刷新客户与调查数据");
        var selectedId = _selectedLead?.Id;
        try
        {
            var availabilityTask = RefreshAvailabilityAsync();
            var leadsTask = _services.Repository.GetLeadsAsync();
            var summariesTask = _services.Repository.GetCustomerEnrichmentQueueSummariesAsync();
            await Task.WhenAll(availabilityTask, leadsTask, summariesTask);
            _allLeads = (await leadsTask).
                OrderBy(lead => GradeOrder(lead.Grade))
                .ThenByDescending(lead => lead.UpdatedAt)
                .ToList();
            _queueSummaries = (await summariesTask).ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.OrdinalIgnoreCase);
            ApplyCustomerFilter(selectedId);

            if (CustomerList.SelectedItem is CustomerRow selected)
                await LoadCustomerSnapshotAsync(selected.Lead, showLoading: false);
            else
                ShowNoCustomerState();
        }
        catch (Exception ex)
        {
            SetInlineStatus(ToUserMessage(ex), "danger");
            ShowFactEmpty("无法加载调查工作台", "本地客户数据仍然安全。请稍后重试。", false);
        }
        finally
        {
            _refreshing = false;
            SetPageLoading(false);
            UpdateActionAvailability();
        }
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        SubscribeToServiceChanges();
        ApplyResponsiveLayout();
        await RefreshAsync();
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _pollTimer.Stop();
        _selectionCancellation?.Cancel();
        UnsubscribeFromServiceChanges();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Import_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke(this, EventArgs.Empty);

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void CustomerSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyCustomerFilter(_selectedLead?.Id);
        e.Handled = true;
    }

    private void CustomerFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        ApplyCustomerFilter(_selectedLead?.Id);
    }

    private void ApplyCustomerFilter(string? preferredCustomerId = null)
    {
        var search = CustomerSearchBox.Text.Trim();
        var grade = (GradeFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var filtered = _allLeads.Where(lead =>
            (string.IsNullOrWhiteSpace(search)
             || Contains(lead.DisplayName, search)
             || Contains(lead.Company, search)
             || Contains(lead.Country, search)
             || Contains(lead.Email, search))
            && (string.IsNullOrWhiteSpace(grade) || string.Equals(lead.Grade, grade, StringComparison.OrdinalIgnoreCase)))
            .Select(lead => new CustomerRow(lead, _queueSummaries.GetValueOrDefault(lead.Id)))
            .ToList();

        _customers.Clear();
        foreach (var row in filtered) _customers.Add(row);
        CustomerCountText.Text = $"{_customers.Count} 位客户";
        CustomerEmptyState.Visibility = _customers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CustomerList.Visibility = _customers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var preferred = _customers.FirstOrDefault(row =>
            string.Equals(row.Lead.Id, preferredCustomerId, StringComparison.OrdinalIgnoreCase));
        CustomerList.SelectedItem = preferred ?? _customers.FirstOrDefault();
    }

    private async void CustomerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomerList.SelectedItem is not CustomerRow row)
        {
            ShowNoCustomerState();
            return;
        }

        await LoadCustomerSnapshotAsync(row.Lead, showLoading: true);
    }

    private async Task LoadCustomerSnapshotAsync(Lead lead, bool showLoading)
    {
        var generation = ++_selectionGeneration;
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = new CancellationTokenSource();
        var cancellationToken = _selectionCancellation.Token;
        _selectedLead = lead;
        _selectedFact = null;
        _selectedSource = null;
        SelectedCustomerNameText.Text = lead.DisplayName;
        SelectedCustomerIdentityText.Text = BuildIdentityLine(lead);
        SetInlineStatus($"正在读取 {lead.DisplayName} 的调查记录", "info");
        if (showLoading) SetPageLoading(true, $"正在加载 {lead.DisplayName} 的调查记录");
        UpdateActionAvailability();

        try
        {
            var snapshot = await GetSnapshotAsync(lead.Id, cancellationToken);
            if (generation != _selectionGeneration || cancellationToken.IsCancellationRequested) return;
            _snapshot = snapshot;
            ApplyFactFilter();
            UpdateSnapshotSummary();
            UpdateSelectedCustomerRow();
            ConfigurePolling();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation != _selectionGeneration) return;
            _snapshot = new CustomerEnrichmentSnapshot();
            _filteredFacts.Clear();
            _facts.Clear();
            ClearEvidence();
            SetInlineStatus(ToUserMessage(ex), "danger");
            ShowFactEmpty("调查记录读取失败", "请刷新后重试；已有客户数据不会受到影响。", false);
        }
        finally
        {
            if (generation == _selectionGeneration)
            {
                if (showLoading) SetPageLoading(false);
                UpdateActionAvailability();
            }
        }
    }

    private async Task<CustomerEnrichmentSnapshot> GetSnapshotAsync(string customerId, CancellationToken cancellationToken)
    {
        return await _enrichmentService.GetSnapshotAsync(customerId, cancellationToken);
    }

    private void ApplyFactFilter(string? preferredFactId = null)
    {
        preferredFactId ??= _selectedFact?.Fact.Id;
        var category = (FactCategoryFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var status = (FactStatusFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var rows = _snapshot.Facts
            .Select(fact => new FactRow(fact))
            .Where(row => string.IsNullOrWhiteSpace(category) || string.Equals(row.CategoryKey, category, StringComparison.OrdinalIgnoreCase))
            .Where(row => string.IsNullOrWhiteSpace(status) || string.Equals(row.StatusKey, status, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => StatusOrder(row.Fact.VerificationStatus))
            .ThenByDescending(row => row.Fact.ConfidenceScore)
            .ThenBy(row => row.FieldLabel, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _filteredFacts = rows;
        _facts.Clear();

        if (_filteredFacts.Count == 0)
        {
            _factPage = 0;
            var hasAny = _snapshot.Facts.Count > 0;
            if (!hasAny && _snapshot.Sources.Count > 0) ShowSourceCandidates();
            else ClearEvidence();
            if (hasAny)
                ShowFactEmpty("没有符合筛选条件的事实", "调整分类或核验状态，查看其他调查结果。", false);
            else if (_snapshot.Sources.Count > 0)
                ShowFactEmpty(
                    "已找到公开来源，尚未形成事实",
                    $"右侧可核对 {_snapshot.Sources.Count} 个来源；来源不会在未经人工确认时写入客户档案。",
                    false);
            else if (_snapshot.LatestJob?.Status == CustomerEnrichmentJobStatus.NoResults)
                ShowFactEmpty("没有找到可靠公开结果", "本次调查未形成可核验事实。可补充公司或国家信息后重新调查。", true);
            else if (_snapshot.LatestJob?.Status is CustomerEnrichmentJobStatus.Queued or CustomerEnrichmentJobStatus.Running)
                ShowFactEmpty("调查正在进行", "公开搜索、网页读取和事实核验完成后会自动刷新。", false);
            else
                ShowFactEmpty("尚无公开调查事实", "开始调查后，这里会显示事实、置信度与来源。", true);
        }
        else
        {
            FactEmptyState.Visibility = Visibility.Collapsed;
            var preferredIndex = string.IsNullOrWhiteSpace(preferredFactId)
                ? -1
                : _filteredFacts.FindIndex(row =>
                    string.Equals(row.Fact.Id, preferredFactId, StringComparison.OrdinalIgnoreCase));
            _factPage = preferredIndex >= 0 ? preferredIndex / FactsPerPage : 0;
            RenderFactPage(preferredFactId);
        }
    }

    private void RenderFactPage(string? preferredFactId = null)
    {
        var pageCount = Math.Max(1, (int)Math.Ceiling(_filteredFacts.Count / (double)FactsPerPage));
        _factPage = Math.Clamp(_factPage, 0, pageCount - 1);

        _renderingFactPage = true;
        try
        {
            _facts.Clear();
            foreach (var row in _filteredFacts.Skip(_factPage * FactsPerPage).Take(FactsPerPage))
                _facts.Add(row);

            var selected = _facts.FirstOrDefault(row =>
                string.Equals(row.Fact.Id, preferredFactId, StringComparison.OrdinalIgnoreCase))
                ?? _facts.FirstOrDefault();
            FactGrid.SelectedItem = selected;
        }
        finally
        {
            _renderingFactPage = false;
        }

        FactGrid.Visibility = Visibility.Visible;
        FactPager.Visibility = _filteredFacts.Count > FactsPerPage ? Visibility.Visible : Visibility.Collapsed;
        FactPageText.Text = $"第 {_factPage + 1} / {pageCount} 页 · 共 {_filteredFacts.Count} 条";
        PreviousFactPageButton.IsEnabled = _factPage > 0;
        NextFactPageButton.IsEnabled = _factPage + 1 < pageCount;
        _ = SelectFactAsync(FactGrid.SelectedItem as FactRow);
    }

    private void FactFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _selectedLead is null) return;
        ApplyFactFilter();
    }

    private async void FactGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingFactPage) return;
        await SelectFactAsync(FactGrid.SelectedItem as FactRow);
    }

    private void PreviousFactPage_Click(object sender, RoutedEventArgs e)
    {
        if (_factPage <= 0) return;
        _factPage--;
        RenderFactPage();
    }

    private void NextFactPage_Click(object sender, RoutedEventArgs e)
    {
        var pageCount = Math.Max(1, (int)Math.Ceiling(_filteredFacts.Count / (double)FactsPerPage));
        if (_factPage + 1 >= pageCount) return;
        _factPage++;
        RenderFactPage();
    }

    private async Task SelectFactAsync(FactRow? row)
    {
        if (row is null)
        {
            ClearEvidence();
            return;
        }
        _selectedFact = row;
        ShowFactEvidence(row);
        await LoadSourcesForFactAsync(row);
        UpdateActionAvailability();
    }

    private void ShowFactEvidence(FactRow row)
    {
        EvidenceContent.IsEnabled = true;
        EvidenceFactTitleText.Text = $"{row.CategoryLabel} · {row.FieldLabel}";
        EvidenceStatusText.Text = row.StatusLabel;
        EvidenceValueText.Text = row.Value;
        EvidenceConfidenceBar.Value = row.ConfidencePercent;
        EvidenceConfidenceText.Text = row.ConfidenceLabel;
        EvidenceQuoteText.Text = string.IsNullOrWhiteSpace(row.Fact.EvidenceQuote)
            ? row.Fact.VerificationStatus == CustomerEnrichmentVerificationStatus.HumanConfirmed
                ? (string.IsNullOrWhiteSpace(row.Fact.ReviewNote)
                    ? "该值由人工确认；没有把原公开来源声明为编辑后值的直接证据。"
                    : row.Fact.ReviewNote)
                : "暂无可引用原文，请核对下方公开来源。"
            : row.Fact.EvidenceQuote.Trim();
        EvidenceFreshnessText.Text = FreshnessLabel(row.Fact);
        ToggleEvidenceButton.IsEnabled = true;
    }

    private async Task LoadSourcesForFactAsync(FactRow row)
    {
        var selectedFactId = row.Fact.Id;
        var sourceIds = row.Fact.SourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = _snapshot.Sources
            .Where(source => sourceIds.Contains(source.Id))
            .ToList();

        if (sourceIds.Count > 0 && candidates.Count == 0 && _selectedLead is not null)
        {
            try
            {
                candidates = (await _services.Repository.GetCustomerEnrichmentSourcesAsync(
                        _selectedLead.Id,
                        row.Fact.JobId,
                        _selectionCancellation?.Token ?? CancellationToken.None))
                    .Where(source => sourceIds.Contains(source.Id))
                    .ToList();
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (_selectedFact?.Fact.Id != selectedFactId) return;
        _sources.Clear();
        foreach (var source in candidates.OrderByDescending(source => source.IdentityMatchScore).ThenBy(source => source.Rank))
            _sources.Add(new SourceRow(source));
        SourceList.SelectedIndex = _sources.Count > 0 ? 0 : -1;
        if (_sources.Count == 0)
        {
            SourceTitleText.Text = "没有关联的公开来源";
            SourceMetaText.Text = "该事实可能来自已清理的缓存，或尚未完成来源关联。";
            SourceMatchText.Text = "实体匹配：暂无";
            SourceConflictText.Text = "";
            SourceSnippetText.Text = "";
            SourceDetailPanel.IsEnabled = false;
            OpenSourceButton.IsEnabled = false;
        }
    }

    private void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceList.SelectedItem is not SourceRow row)
        {
            _selectedSource = null;
            SourceDetailPanel.IsEnabled = false;
            OpenSourceButton.IsEnabled = false;
            return;
        }

        _selectedSource = row;
        SourceDetailPanel.IsEnabled = true;
        SourceTitleText.Text = row.Title;
        SourceMetaText.Text = row.MetaLabel;
        SourceMatchText.Text = row.MatchDetail;
        SourceConflictText.Text = row.ConflictLabel;
        SourceSnippetText.Text = row.Snippet;
        OpenSourceButton.IsEnabled = IsSafeWebUrl(row.Source.Url);
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        var url = _selectedSource?.Source.Url;
        if (!IsSafeWebUrl(url))
        {
            SetInlineStatus("该来源地址无效，未打开浏览器。", "warning");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
            SetInlineStatus("已在默认浏览器打开公开来源。", "success");
        }
        catch
        {
            SetInlineStatus("无法打开默认浏览器，请稍后重试。", "danger");
        }
    }

    private async void Investigate_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLead is null)
        {
            SetInlineStatus("请先选择一位客户。", "warning");
            return;
        }
        if (!_providerAvailable)
        {
            SetInlineStatus(_availabilityMessage, "warning");
            SettingsRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        InvestigateButton.IsEnabled = false;
        JobProgress.Visibility = Visibility.Visible;
        SetInlineStatus($"已提交 {_selectedLead.DisplayName} 的公开调查任务", "info");
        try
        {
            await _enrichmentService.QueueAsync(
                _selectedLead.Id,
                CustomerEnrichmentTriggerType.Manual,
                ForceRefreshCheckBox.IsChecked == true,
                CancellationToken.None);
            await LoadCustomerSnapshotAsync(_selectedLead, showLoading: false);
            _pollTimer.Start();
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            SetInlineStatus(ToUserMessage(ex), "danger");
            JobProgress.Visibility = Visibility.Collapsed;
        }
        finally
        {
            UpdateActionAvailability();
        }
    }

    private async void ConfirmFact_Click(object sender, RoutedEventArgs e) =>
        await ReviewSelectedFactAsync(CustomerEnrichmentReviewAction.Confirm);

    private async void RejectFact_Click(object sender, RoutedEventArgs e) =>
        await ReviewSelectedFactAsync(CustomerEnrichmentReviewAction.Reject);

    private async void OutdateFact_Click(object sender, RoutedEventArgs e) =>
        await ReviewSelectedFactAsync(CustomerEnrichmentReviewAction.MarkOutdated);

    private async void EditFact_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFact is null) return;
        var value = ShowEditFactDialog(_selectedFact.Value);
        if (value is null) return;
        await ReviewSelectedFactAsync(CustomerEnrichmentReviewAction.EditAndConfirm, value);
    }

    private async Task ReviewSelectedFactAsync(CustomerEnrichmentReviewAction action, string? newValue = null)
    {
        if (_selectedFact is null || _selectedLead is null) return;
        var factId = _selectedFact.Fact.Id;
        SetReviewButtonsEnabled(false);
        SetInlineStatus("正在保存人工复核记录", "info");
        try
        {
            await _enrichmentService.ReviewAsync(
                factId,
                action,
                newValue,
                reason: null,
                cancellationToken: CancellationToken.None);
            await LoadCustomerSnapshotAsync(_selectedLead, showLoading: false);
            ApplyFactFilter(factId);
            SetInlineStatus(ReviewSuccessMessage(action), "success");
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            SetInlineStatus(ToUserMessage(ex), "danger");
        }
        finally
        {
            SetReviewButtonsEnabled(_selectedFact is not null);
            UpdateActionAvailability();
        }
    }

    private string? ShowEditFactDialog(string currentValue)
    {
        var dialog = new Window
        {
            Title = "编辑并确认调查事实",
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height,
            Width = 520,
            MinWidth = 440,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TextBlock
        {
            Text = "编辑调查值",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold
        };
        var description = new TextBlock
        {
            Text = "保存后该事实会标记为“人工确认”。原值与公开来源保留在任务及审核历史中，但不会被声明为编辑后值的直接证据。",
            Margin = new Thickness(0, 6, 0, 12),
            TextWrapping = TextWrapping.Wrap
        };
        description.SetResourceReference(ForegroundProperty, "Muted");
        Grid.SetRow(description, 1);
        var editor = new TextBox
        {
            Text = currentValue,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 94,
            MaxHeight = 220,
            MaxLength = 2000,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetName(editor, "编辑后的调查事实值");
        Grid.SetRow(editor, 2);
        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var validation = new TextBlock { Text = "", VerticalAlignment = VerticalAlignment.Center };
        validation.SetResourceReference(ForegroundProperty, "Danger");
        var cancel = new Button { Content = "取消", Margin = new Thickness(8, 0, 0, 0) };
        cancel.Style = (Style)FindResource("SecondaryButton");
        cancel.Click += (_, _) => { dialog.DialogResult = false; };
        AutomationProperties.SetName(cancel, "取消编辑调查事实");
        Grid.SetColumn(cancel, 1);
        var save = new Button { Content = "保存并确认", Margin = new Thickness(8, 0, 0, 0) };
        save.Style = (Style)FindResource("PrimaryButton");
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(editor.Text))
            {
                validation.Text = "调查值不能为空。";
                editor.Focus();
                return;
            }
            dialog.DialogResult = true;
        };
        AutomationProperties.SetName(save, "保存编辑并确认调查事实");
        Grid.SetColumn(save, 2);
        footer.Children.Add(validation);
        footer.Children.Add(cancel);
        footer.Children.Add(save);
        Grid.SetRow(footer, 3);
        root.Children.Add(title);
        root.Children.Add(description);
        root.Children.Add(editor);
        root.Children.Add(footer);
        dialog.Content = root;
        dialog.Loaded += (_, _) => { editor.Focus(); editor.SelectAll(); };
        return dialog.ShowDialog() == true ? editor.Text.Trim() : null;
    }

    private async Task RefreshAvailabilityAsync()
    {
        try
        {
            var localSearch = await PrepareLocalSearchAsync(CancellationToken.None);
            var healthItems = await _enrichmentService.GetAvailabilityAsync(CancellationToken.None);
            _providerAvailable = healthItems.Any(item => item.Available) && !localSearch.OnlyLocalUnavailable;
            ProviderText.Text = _providerAvailable
                ? localSearch.AutoEnabled ? "本机已自动启用" : "已就绪"
                : "需要启用";
            _availabilityMessage = _providerAvailable
                ? localSearch.AutoEnabled
                    ? "已自动启用本机搜索服务，可以直接开始调查。"
                    : "联网调查已就绪，系统会自动选择可用的搜索方式。"
                : "联网调查尚未启用。点击“立即启用”，填写任一联网搜索密钥并保存；这一步只收集公开来源。";
            EnrichmentSettingsButton.Content = _providerAvailable ? "高级设置" : "立即启用";
            AutomationProperties.SetName(
                EnrichmentSettingsButton,
                _providerAvailable ? "打开客户外部调查高级设置" : "立即启用联网调查");
        }
        catch (Exception ex)
        {
            _providerAvailable = false;
            _availabilityMessage = $"联网调查状态检查失败。{ToUserMessage(ex)}";
            ProviderText.Text = "检查失败";
            EnrichmentSettingsButton.Content = "立即启用";
            AutomationProperties.SetName(EnrichmentSettingsButton, "立即启用联网调查");
        }
    }

    private async Task<(bool AutoEnabled, bool OnlyLocalUnavailable)> PrepareLocalSearchAsync(
        CancellationToken cancellationToken)
    {
        var settings = await _enrichmentService.GetSettingsAsync(cancellationToken);
        var remoteConfigured = _enrichmentService.HasProviderKey("tavily")
                               || _enrichmentService.HasProviderKey("brave");
        if (remoteConfigured) return (false, false);

        var provider = new SearXngSearchProvider(
            settings.SearXngBaseUrl,
            options: new CustomerSearchProviderOptions
            {
                RequestTimeout = TimeSpan.FromMilliseconds(1200),
                MinimumRequestInterval = TimeSpan.Zero,
                MaximumAttempts = 1,
                CircuitFailureThreshold = 1
            });
        var health = await provider.CheckHealthAsync(cancellationToken);
        if (!health.Available) return (false, settings.SearXngEnabled);
        if (settings.SearXngEnabled) return (false, false);

        settings.SearXngEnabled = true;
        settings.ProviderOrder = new[] { "searxng" }
            .Concat(settings.ProviderOrder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        await _enrichmentService.SaveSettingsAsync(settings, cancellationToken);
        return (true, false);
    }

    private void SubscribeToServiceChanges()
    {
        _enrichmentService.Changed -= Enrichment_Changed;
        _enrichmentService.Changed += Enrichment_Changed;
    }

    private void UnsubscribeFromServiceChanges()
    {
        _enrichmentService.Changed -= Enrichment_Changed;
    }

    private void Enrichment_Changed(object? sender, CustomerEnrichmentChangedEventArgs e) => HandleEnrichmentChange(e);
    private void HandleEnrichmentChange(CustomerEnrichmentChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (!_loaded) return;
            QueueCustomerSummaryRefresh();
            if (_selectedLead is null || !string.Equals(_selectedLead.Id, e.CustomerId, StringComparison.OrdinalIgnoreCase)) return;
            SetInlineStatus(string.IsNullOrWhiteSpace(e.Message) ? JobStatusLabel(e.Status) : e.Message, StatusTone(e.Status));
            await LoadCustomerSnapshotAsync(_selectedLead, showLoading: false);
        });
    }

    private void QueueCustomerSummaryRefresh()
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (_queueSummaryRefreshRunning)
            {
                _queueSummaryRefreshPending = true;
                return;
            }

            _queueSummaryRefreshRunning = true;
            try
            {
                do
                {
                    _queueSummaryRefreshPending = false;
                    await Task.Delay(120);
                    var summaries = await _services.Repository.GetCustomerEnrichmentQueueSummariesAsync();
                    _queueSummaries = summaries.ToDictionary(
                        item => item.Key,
                        item => item.Value,
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var row in _customers)
                        row.ApplySummary(_queueSummaries.GetValueOrDefault(row.Lead.Id));
                }
                while (_queueSummaryRefreshPending && _loaded);
            }
            catch
            {
                // A later service event, manual refresh or page navigation retries the local summary read.
            }
            finally
            {
                _queueSummaryRefreshRunning = false;
            }
        });
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_polling || _selectedLead is null) return;
        _polling = true;
        try
        {
            await LoadCustomerSnapshotAsync(_selectedLead, showLoading: false);
        }
        finally
        {
            _polling = false;
        }
    }

    private void ConfigurePolling()
    {
        var running = _snapshot.LatestJob?.Status is CustomerEnrichmentJobStatus.Queued or CustomerEnrichmentJobStatus.Running;
        JobProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (running && _loaded) _pollTimer.Start();
        else _pollTimer.Stop();
    }

    private void UpdateSnapshotSummary()
    {
        var job = _snapshot.LatestJob;
        FactCountText.Text = $"{_snapshot.Facts.Count} 条";
        SourceCountText.Text = $"{_snapshot.Sources.Count} 个";
        CostText.Text = $"${job?.CostUsd ?? 0m:0.000}";
        CostText.ToolTip = $"本程序本月本地估算：${_snapshot.Usage.MonthEstimatedCostUsd:0.000}；本月请求：{_snapshot.Usage.MonthRequests} 次。不含账号在其他工具中的用量，实际账单以 Provider 为准。";
        JobStatusText.Text = job is null ? "尚未调查" : JobStatusLabel(job.Status);
        LastUpdatedText.Text = job is null ? "暂无" : job.UpdatedAt.LocalDateTime.ToString("MM-dd HH:mm");
        if (job is null)
            SetInlineStatus(_providerAvailable ? "已就绪，选择客户后可开始调查。" : _availabilityMessage, _providerAvailable ? "neutral" : "warning");
        else if (job.Status == CustomerEnrichmentJobStatus.Failed)
            SetInlineStatus(ToJobFailureMessage(job), "danger");
        else if (job.Status == CustomerEnrichmentJobStatus.NoResults)
            SetInlineStatus("未找到足够可靠的公开结果，本次调查未形成事实。", "warning");
        else if (job.Status == CustomerEnrichmentJobStatus.NeedsReview
                 && _snapshot.Facts.Count == 0
                 && _snapshot.Sources.Count > 0)
            SetInlineStatus(SourceOnlyGuidance(job), "warning");
        else if (job.Status == CustomerEnrichmentJobStatus.Succeeded)
            SetInlineStatus(job.ReusedCache ? "已载入缓存调查结果。" : $"调查完成，形成 {_snapshot.Facts.Count} 条事实。", "success");
        else
            SetInlineStatus(JobStatusLabel(job.Status), StatusTone(job.Status));
    }

    private void UpdateSelectedCustomerRow()
    {
        if (_selectedLead is null) return;
        var row = _customers.FirstOrDefault(item => string.Equals(item.Lead.Id, _selectedLead.Id, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        if (_queueSummaries.TryGetValue(_selectedLead.Id, out var current)
            && string.Equals(current.LatestJob?.Id, _snapshot.LatestJob?.Id, StringComparison.OrdinalIgnoreCase))
        {
            row.ApplySummary(current);
            return;
        }

        var latestHistoricalJob = _snapshot.Jobs
            .OrderByDescending(job => job.CreatedAt)
            .FirstOrDefault();
        var factCount = _snapshot.Facts
            .Select(fact => $"{fact.FieldType}|{fact.NormalizedValue}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var summary = new CustomerEnrichmentQueueSummary(
            _selectedLead.Id,
            _snapshot.LatestJob,
            factCount,
            latestHistoricalJob);
        _queueSummaries[_selectedLead.Id] = summary;
        row.ApplySummary(summary);
        QueueCustomerSummaryRefresh();
    }

    private void ShowNoCustomerState()
    {
        _selectedLead = null;
        _snapshot = new CustomerEnrichmentSnapshot();
        _filteredFacts.Clear();
        _factPage = 0;
        _facts.Clear();
        _sources.Clear();
        SelectedCustomerNameText.Text = "请选择客户";
        SelectedCustomerIdentityText.Text = "事实仅用于决策辅助，需核验后再写入客户档案。";
        FactCountText.Text = "0 条";
        SourceCountText.Text = "0 个";
        CostText.Text = "$0.000";
        CostText.ToolTip = null;
        JobStatusText.Text = "尚未调查";
        LastUpdatedText.Text = "暂无";
        ShowFactEmpty("选择客户开始调查", "调查结果会以事实、置信度和公开来源呈现。", false);
        ClearEvidence();
        SetInlineStatus(_customers.Count == 0 ? "当前没有可调查客户。" : "选择客户后查看公开调查结果。", "neutral");
        UpdateActionAvailability();
    }

    private void ShowFactEmpty(string title, string message, bool showAction)
    {
        FactEmptyTitle.Text = title;
        FactEmptyMessage.Text = message;
        FactEmptyActionButton.Visibility = showAction ? Visibility.Visible : Visibility.Collapsed;
        FactEmptyActionButton.Content = _providerAvailable ? "开始调查" : "立即启用";
        AutomationProperties.SetName(
            FactEmptyActionButton,
            _providerAvailable ? "从空状态开始调查" : "从空状态立即启用联网调查");
        FactEmptyActionButton.IsEnabled = showAction && _selectedLead is not null && !_refreshing;
        FactEmptyState.Visibility = Visibility.Visible;
        FactGrid.Visibility = Visibility.Collapsed;
        FactPager.Visibility = Visibility.Collapsed;
    }

    private void ClearEvidence()
    {
        _selectedFact = null;
        _selectedSource = null;
        _sources.Clear();
        EvidenceContent.IsEnabled = false;
        EvidenceFactTitleText.Text = "选择一条事实查看证据链";
        EvidenceStatusText.Text = "待核验";
        EvidenceValueText.Text = "暂无";
        EvidenceConfidenceBar.Value = 0;
        EvidenceConfidenceText.Text = "0%";
        EvidenceQuoteText.Text = "暂无可引用原文。";
        EvidenceFreshnessText.Text = "时效信息：暂无";
        SourceTitleText.Text = "未选择来源";
        SourceMetaText.Text = "";
        SourceMatchText.Text = "实体匹配：暂无";
        SourceConflictText.Text = "";
        SourceSnippetText.Text = "";
        SourceDetailPanel.IsEnabled = false;
        OpenSourceButton.IsEnabled = false;
        ToggleEvidenceButton.IsEnabled = false;
        SetReviewButtonsEnabled(false);
    }

    private void ShowSourceCandidates()
    {
        _selectedFact = null;
        _selectedSource = null;
        _sources.Clear();
        foreach (var source in _snapshot.Sources
                     .OrderByDescending(item => item.IdentityMatchScore)
                     .ThenBy(item => item.Rank))
            _sources.Add(new SourceRow(source));

        EvidenceContent.IsEnabled = true;
        EvidenceFactTitleText.Text = "来源候选，尚未形成事实";
        EvidenceStatusText.Text = "来源候选";
        EvidenceValueText.Text = "尚未形成可审核事实";
        EvidenceConfidenceBar.Value = 0;
        EvidenceConfidenceText.Text = "待提取";
        EvidenceQuoteText.Text = SourceOnlyGuidance(_snapshot.LatestJob);
        EvidenceFreshnessText.Text = _snapshot.LatestJob is null
            ? "时效信息：来源抓取时间见下方详情"
            : $"时效信息：最近任务更新于 {_snapshot.LatestJob.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm}";
        ToggleEvidenceButton.IsEnabled = true;
        SetReviewButtonsEnabled(false);
        SourceList.SelectedIndex = _sources.Count > 0 ? 0 : -1;
    }

    private void SetReviewButtonsEnabled(bool enabled)
    {
        ConfirmFactButton.IsEnabled = enabled;
        RejectFactButton.IsEnabled = enabled;
        EditFactButton.IsEnabled = enabled;
        OutdateFactButton.IsEnabled = enabled;
    }

    private void UpdateActionAvailability()
    {
        var running = _snapshot.LatestJob?.Status is CustomerEnrichmentJobStatus.Queued or CustomerEnrichmentJobStatus.Running;
        InvestigateButton.Content = _providerAvailable ? "开始调查" : "立即启用";
        AutomationProperties.SetName(
            InvestigateButton,
            _providerAvailable ? "开始调查当前客户" : "立即启用联网调查");
        InvestigateButton.IsEnabled = _selectedLead is not null && !running && !_refreshing;
        FactEmptyActionButton.IsEnabled = InvestigateButton.IsEnabled;
        SetReviewButtonsEnabled(_selectedFact is not null && !running);
    }

    private void SetPageLoading(bool loading, string? title = null)
    {
        if (!string.IsNullOrWhiteSpace(title)) LoadingTitleText.Text = title;
        PageLoadingOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetInlineStatus(string message, string tone)
    {
        LiveStatusText.Text = message;
        LiveStatusText.SetResourceReference(ForegroundProperty, tone switch
        {
            "success" => "Success",
            "warning" => "Warning",
            "danger" => "Danger",
            "info" => "Info",
            _ => "Muted"
        });
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();

    private void ApplyResponsiveLayout()
    {
        if (!IsLoaded) return;
        var narrow = ActualWidth < 1180;
        if (narrow && _evidenceExpanded)
        {
            CustomerColumn.Width = new GridLength(0);
            CustomerGapColumn.Width = new GridLength(0);
            CustomerPanel.Visibility = Visibility.Collapsed;
            SourceColumn.Width = new GridLength(360);
        }
        else
        {
            CustomerPanel.Visibility = Visibility.Visible;
            CustomerColumn.Width = new GridLength(narrow ? 240 : 285);
            CustomerGapColumn.Width = new GridLength(12);
            SourceColumn.Width = _evidenceExpanded ? new GridLength(380) : new GridLength(40);
        }
        SourceGapColumn.Width = new GridLength(12);
        EvidencePanel.Visibility = _evidenceExpanded ? Visibility.Visible : Visibility.Collapsed;
        EvidenceRail.Visibility = _evidenceExpanded ? Visibility.Collapsed : Visibility.Visible;
        ToggleEvidenceButton.Content = _evidenceExpanded ? "收起证据" : "证据详情";
        AutomationProperties.SetName(
            ToggleEvidenceButton,
            _evidenceExpanded ? "收起选中事实的证据详情" : "显示选中事实的证据详情");
    }

    private void ToggleEvidence_Click(object sender, RoutedEventArgs e)
    {
        _evidenceExpanded = !_evidenceExpanded;
        ApplyResponsiveLayout();
        if (_evidenceExpanded && _sources.Count > 0) SourceList.Focus();
        else ToggleEvidenceButton.Focus();
    }

    private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_evidenceExpanded) return;
        _evidenceExpanded = false;
        ApplyResponsiveLayout();
        ToggleEvidenceButton.Focus();
        e.Handled = true;
    }

    private static int GradeOrder(string? grade) => grade?.Trim().ToUpperInvariant() switch
    {
        "A" => 0,
        "B" => 1,
        "C" => 2,
        _ => 3
    };

    private static int StatusOrder(CustomerEnrichmentVerificationStatus status) => status switch
    {
        CustomerEnrichmentVerificationStatus.HumanConfirmed => 0,
        CustomerEnrichmentVerificationStatus.Verified => 1,
        CustomerEnrichmentVerificationStatus.LikelyMatch => 2,
        CustomerEnrichmentVerificationStatus.PossibleMatch => 3,
        CustomerEnrichmentVerificationStatus.Conflicting => 4,
        CustomerEnrichmentVerificationStatus.Outdated => 5,
        _ => 6
    };

    private static bool Contains(string? value, string search) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private static string BuildIdentityLine(Lead lead)
    {
        var parts = new[] { lead.Company, lead.Country, lead.Email, lead.PhoneE164 }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var identity = string.Join(" · ", parts);
        return string.IsNullOrWhiteSpace(identity) ? "客户身份信息不足，建议先补充公司、国家或联系方式。" : identity;
    }

    private static string ReviewSuccessMessage(CustomerEnrichmentReviewAction action) => action switch
    {
        CustomerEnrichmentReviewAction.Confirm => "事实已人工确认，并保留来源审计记录。",
        CustomerEnrichmentReviewAction.Reject => "事实已拒绝，不会用于客户决策。",
        CustomerEnrichmentReviewAction.EditAndConfirm => "编辑值已确认，原值仍保留在审计记录中。",
        CustomerEnrichmentReviewAction.MarkOutdated => "事实已标记过期，等待后续重新调查。",
        _ => "人工复核已保存。"
    };

    private static string JobStatusLabel(CustomerEnrichmentJobStatus status) => status switch
    {
        CustomerEnrichmentJobStatus.Queued => "等待调查",
        CustomerEnrichmentJobStatus.Running => "调查中",
        CustomerEnrichmentJobStatus.NeedsReview => "等待人工复核",
        CustomerEnrichmentJobStatus.Succeeded => "调查完成",
        CustomerEnrichmentJobStatus.Failed => "调查失败",
        CustomerEnrichmentJobStatus.Cancelled => "调查已取消",
        CustomerEnrichmentJobStatus.NoResults => "没有可靠结果",
        _ => status.ToString()
    };

    private static bool NeedsAiBudgetAuthorization(CustomerEnrichmentJob? job) =>
        job is { Status: CustomerEnrichmentJobStatus.NeedsReview }
        && string.Equals(
            job.ErrorCode,
            CustomerEnrichmentErrorCodes.AiAnalysisPaymentNotAuthorized,
            StringComparison.OrdinalIgnoreCase);

    private static bool NeedsAiProviderSetup(CustomerEnrichmentJob? job) =>
        job is { Status: CustomerEnrichmentJobStatus.NeedsReview }
        && string.Equals(
            job.ErrorCode,
            CustomerEnrichmentErrorCodes.AnalysisProviderUnavailable,
            StringComparison.OrdinalIgnoreCase);

    private static string SourceOnlyGuidance(CustomerEnrichmentJob? job)
    {
        if (NeedsAiProviderSetup(job))
            return "公开来源已保存，尚未生成事实。请打开左侧“设置”，完成 AI API 对接，并为“客户外部调查”选择可用模型后重新调查。";
        if (NeedsAiBudgetAuthorization(job))
            return "公开来源已保存，尚未生成事实。请打开“设置”，启用 AI 事实整理并填写本程序本地月度估算提醒额度后重新调查。";
        return "公开来源已保存，但尚未形成可审核事实。请核对右侧来源，并按页面提示补齐条件后重新调查。";
    }

    private static string StatusTone(CustomerEnrichmentJobStatus status) => status switch
    {
        CustomerEnrichmentJobStatus.Succeeded => "success",
        CustomerEnrichmentJobStatus.Queued or CustomerEnrichmentJobStatus.Running => "info",
        CustomerEnrichmentJobStatus.NeedsReview or CustomerEnrichmentJobStatus.NoResults or CustomerEnrichmentJobStatus.Cancelled => "warning",
        CustomerEnrichmentJobStatus.Failed => "danger",
        _ => "neutral"
    };

    private static string FreshnessLabel(CustomerEnrichmentFact fact)
    {
        var verified = fact.LastVerifiedAt is null
            ? $"发现于 {fact.FirstDiscoveredAt.LocalDateTime:yyyy-MM-dd}"
            : $"核验于 {fact.LastVerifiedAt.Value.LocalDateTime:yyyy-MM-dd}";
        if (fact.ExpiresAt is null) return $"时效信息：{verified} · 未设置到期日";
        var state = fact.ExpiresAt <= DateTimeOffset.Now ? "已过期" : $"有效至 {fact.ExpiresAt.Value.LocalDateTime:yyyy-MM-dd}";
        return $"时效信息：{verified} · {state}";
    }

    private static string ToJobFailureMessage(CustomerEnrichmentJob job)
    {
        if (string.Equals(job.ErrorCode, CustomerEnrichmentErrorCodes.ProviderQuotaExhausted, StringComparison.OrdinalIgnoreCase))
            return "本地账号额度估算已用完，程序已停止额外调用；实际额度与账单以 Provider 为准。";
        if (string.Equals(job.ErrorCode, CustomerEnrichmentErrorCodes.PaidRequestBlocked, StringComparison.OrdinalIgnoreCase))
            return "下一次付费搜索已被本地估算提醒规则阻止；实际账单以 Provider 为准。";
        if (string.Equals(job.ErrorCode, CustomerEnrichmentErrorCodes.SearchProviderUnavailable, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(job.ErrorMessage)
                ? "搜索服务暂时不可用，请检查网络或 Provider 状态后重试。"
                : job.ErrorMessage;
        if (string.Equals(job.ErrorCode, CustomerEnrichmentErrorCodes.ProviderRequestRejected, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(job.ErrorMessage)
                ? "搜索服务拒绝了本次请求，请刷新后重试。"
                : job.ErrorMessage;
        if (string.Equals(job.ErrorCode, CustomerEnrichmentErrorCodes.AiAnalysisPaymentNotAuthorized, StringComparison.OrdinalIgnoreCase))
            return "公开来源已保存。如需整理为客户事实，请打开设置，启用 AI 事实整理并填写本程序本地月度估算提醒额度。";
        return string.IsNullOrWhiteSpace(job.ErrorMessage) ? "调查失败，请稍后重试。" : job.ErrorMessage;
    }

    private static string ToUserMessage(Exception exception)
    {
        if (exception is CustomerEnrichmentException enrichment)
        {
            return enrichment.Code switch
            {
                CustomerEnrichmentErrorCodes.ProviderQuotaExhausted => "本地账号额度估算已用完，程序已停止额外调用；实际额度与账单以 Provider 为准。",
                CustomerEnrichmentErrorCodes.PaidRequestBlocked => "下一次付费搜索已被本地估算提醒规则阻止；实际账单以 Provider 为准。",
                CustomerEnrichmentErrorCodes.SearchProviderUnavailable => enrichment.Message,
                CustomerEnrichmentErrorCodes.ProviderRequestRejected => enrichment.Message,
                CustomerEnrichmentErrorCodes.SearXngNotRunning => "本机搜索服务未运行。点击“立即启用”选择另一种联网搜索方式，或启动本机服务。",
                CustomerEnrichmentErrorCodes.CustomerIdentityMissing => "客户身份信息不足，请先补充姓名、公司、国家或联系方式。",
                CustomerEnrichmentErrorCodes.NoPublicResults => "没有找到足够可靠的公开结果。",
                CustomerEnrichmentErrorCodes.WebFetchTimeout => "部分公开网页读取超时，请稍后重试。",
                CustomerEnrichmentErrorCodes.WebFetchBlocked => "公开网页拒绝访问，系统未绕过站点限制。",
                CustomerEnrichmentErrorCodes.InvalidModelResponse => "事实提取结果格式异常，可重新调查。",
                CustomerEnrichmentErrorCodes.AiAnalysisPaymentNotAuthorized => "公开来源已保存。如需整理为客户事实，请打开设置，启用 AI 事实整理并填写本程序本地月度估算提醒额度。",
                _ => string.IsNullOrWhiteSpace(enrichment.Message) ? "调查操作失败，请稍后重试。" : enrichment.Message
            };
        }
        if (exception is InvalidOperationException invalid && !string.IsNullOrWhiteSpace(invalid.Message))
            return invalid.Message;
        return "操作未完成，请稍后重试。";
    }

    private static bool IsSafeWebUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));

    private static string ProviderDisplayName(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "tavily" => "Tavily",
        "brave" => "Brave Search",
        "searxng" => "SearXNG",
        "google" => "Google",
        "bing" => "Bing",
        null or "" => "未知服务",
        _ => provider
    };

    private sealed class CustomerRow : INotifyPropertyChanged
    {
        private string _investigationLabel = "正在读取状态";
        public Lead Lead { get; }
        public string DisplayName => string.IsNullOrWhiteSpace(Lead.DisplayName) ? "未命名客户" : Lead.DisplayName;
        public string CompanyAndCountry
        {
            get
            {
                var value = string.Join(" · ", new[] { Lead.Company, Lead.Country }.Where(item => !string.IsNullOrWhiteSpace(item)));
                return string.IsNullOrWhiteSpace(value) ? "身份信息待补充" : value;
            }
        }
        public string GradeLabel => string.IsNullOrWhiteSpace(Lead.Grade) ? "D" : Lead.Grade.ToUpperInvariant();
        public string AutomationName => $"{DisplayName}，{CompanyAndCountry}，客户等级 {GradeLabel}，{InvestigationLabel}";
        public string InvestigationLabel
        {
            get => _investigationLabel;
            set
            {
                if (_investigationLabel == value) return;
                _investigationLabel = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InvestigationLabel)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationName)));
            }
        }
        public CustomerRow(Lead lead, CustomerEnrichmentQueueSummary? summary)
        {
            Lead = lead;
            ApplySummary(summary);
        }

        public void ApplySummary(CustomerEnrichmentQueueSummary? summary)
        {
            InvestigationLabel = summary switch
            {
                { NeedsRefresh: true } => "资料已变化 · 请重新调查",
                null or { LatestJob: null } => "尚未调查",
                { LatestJob: { } job } when NeedsAiProviderSetup(job) => "来源已保存 · 待完成 AI 对接",
                { LatestJob: { } job } when NeedsAiBudgetAuthorization(job) => "来源已保存 · 待启用 AI 整理",
                _ => $"{JobStatusLabel(summary.LatestJob!.Status)} · {summary.FactCount} 条事实"
            };
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class FactRow
    {
        public CustomerEnrichmentFact Fact { get; }
        public string CategoryKey => CategoryKeyFor(Fact.Category, Fact.FieldType);
        public string CategoryLabel => CategoryLabelFor(CategoryKey);
        public string FieldLabel => FieldLabelFor(Fact.FieldType);
        public string Value => string.IsNullOrWhiteSpace(Fact.FieldValue) ? "未提供" : Fact.FieldValue.Trim();
        public string StatusKey => StatusKeyFor(Fact.VerificationStatus);
        public string StatusLabel => StatusLabelFor(Fact.VerificationStatus);
        public string StatusTone => StatusToneFor(Fact.VerificationStatus);
        public int ConfidencePercent => Math.Clamp(Fact.ConfidenceScore, 0, 100);
        public string ConfidenceLabel => $"{ConfidencePercent}%";
        public string SourceCountLabel => $"{Fact.SourceCount} 个";
        public string AutomationName => $"{CategoryLabel}，{FieldLabel}，{Value}，{StatusLabel}，置信度 {ConfidencePercent}%";

        public FactRow(CustomerEnrichmentFact fact) => Fact = fact;

        private static string CategoryKeyFor(string? category, string? fieldType)
        {
            var value = $"{category} {fieldType}".ToLowerInvariant();
            if (Any(value, "identity", "company", "legal", "domain", "website", "address", "headquarter", "name")) return "identity";
            if (Any(value, "contact", "email", "phone", "person", "founder", "director")) return "contact";
            if (Any(value, "business", "product", "industry", "service", "market")) return "business";
            if (Any(value, "scale", "employee", "revenue", "capacity", "facility")) return "scale";
            if (Any(value, "signal", "news", "risk", "activity", "event", "growth")) return "signal";
            return "other";
        }

        private static bool Any(string value, params string[] needles) => needles.Any(value.Contains);
        private static string CategoryLabelFor(string key) => key switch
        {
            "identity" => "公司身份",
            "contact" => "联系人",
            "business" => "业务与产品",
            "scale" => "规模与能力",
            "signal" => "动态与风险",
            _ => "其他"
        };
        private static string FieldLabelFor(string? fieldType)
        {
            if (string.IsNullOrWhiteSpace(fieldType)) return "公开事实";
            return fieldType.Trim().ToLowerInvariant() switch
            {
                "company_name" or "legal_name" => "公司名称",
                "website" or "company_website" => "官方网站",
                "industry" => "所属行业",
                "products" or "product" => "主营产品",
                "address" or "headquarters" => "公司地址",
                "company_size" or "employees" => "企业规模",
                "revenue" => "公开营收",
                "contact_name" => "联系人姓名",
                "contact_title" => "联系人职位",
                "email" => "公开邮箱",
                "phone" => "公开电话",
                "news" or "latest_news" => "近期动态",
                "risk" or "risk_signal" => "风险信号",
                _ => fieldType.Replace('_', ' ').Trim()
            };
        }
        private static string StatusKeyFor(CustomerEnrichmentVerificationStatus status) => status switch
        {
            CustomerEnrichmentVerificationStatus.Verified => "verified",
            CustomerEnrichmentVerificationStatus.HumanConfirmed => "human",
            CustomerEnrichmentVerificationStatus.LikelyMatch => "likely",
            CustomerEnrichmentVerificationStatus.PossibleMatch => "possible",
            CustomerEnrichmentVerificationStatus.Conflicting => "conflict",
            CustomerEnrichmentVerificationStatus.Rejected => "rejected",
            CustomerEnrichmentVerificationStatus.Outdated => "outdated",
            _ => "possible"
        };
        private static string StatusLabelFor(CustomerEnrichmentVerificationStatus status) => status switch
        {
            CustomerEnrichmentVerificationStatus.Verified => "已核验",
            CustomerEnrichmentVerificationStatus.HumanConfirmed => "人工确认",
            CustomerEnrichmentVerificationStatus.LikelyMatch => "高可能",
            CustomerEnrichmentVerificationStatus.PossibleMatch => "待核验",
            CustomerEnrichmentVerificationStatus.Conflicting => "存在冲突",
            CustomerEnrichmentVerificationStatus.Rejected => "已拒绝",
            CustomerEnrichmentVerificationStatus.Outdated => "已过期",
            _ => "待核验"
        };
        private static string StatusToneFor(CustomerEnrichmentVerificationStatus status) => status switch
        {
            CustomerEnrichmentVerificationStatus.Verified or CustomerEnrichmentVerificationStatus.HumanConfirmed => "success",
            CustomerEnrichmentVerificationStatus.LikelyMatch => "info",
            CustomerEnrichmentVerificationStatus.PossibleMatch or CustomerEnrichmentVerificationStatus.Outdated => "warning",
            CustomerEnrichmentVerificationStatus.Conflicting or CustomerEnrichmentVerificationStatus.Rejected => "danger",
            _ => "warning"
        };
    }

    private sealed class SourceRow
    {
        public CustomerEnrichmentSource Source { get; }
        public string Title => string.IsNullOrWhiteSpace(Source.Title) ? Source.Domain : Source.Title.Trim();
        public string DomainAndProvider => string.Join(" · ", new[] { Source.Domain, ProviderDisplayName(Source.Provider) }.Where(item => !string.IsNullOrWhiteSpace(item)));
        public string MatchLabel => $"{MatchStatusLabel(Source.IdentityMatchStatus)} {Math.Clamp(Source.IdentityMatchScore, 0, 100)}%";
        public string MetaLabel
        {
            get
            {
                var date = Source.PublishedAt ?? Source.RetrievedAt;
                return $"{DomainAndProvider} · {date.LocalDateTime:yyyy-MM-dd} · 排名 {Math.Max(1, Source.Rank)}";
            }
        }
        public string MatchDetail
        {
            get
            {
                var reasons = Source.IdentityMatchReasons.Count == 0 ? "暂无匹配说明" : string.Join("；", Source.IdentityMatchReasons);
                return $"实体匹配：{MatchStatusLabel(Source.IdentityMatchStatus)} · {Math.Clamp(Source.IdentityMatchScore, 0, 100)}% · {reasons}";
            }
        }
        public string ConflictLabel => Source.IdentityConflicts.Count == 0 ? "" : $"冲突：{string.Join("；", Source.IdentityConflicts)}";
        public string Snippet => !string.IsNullOrWhiteSpace(Source.Snippet)
            ? Source.Snippet.Trim()
            : !string.IsNullOrWhiteSpace(Source.ContentText)
                ? Source.ContentText.Trim()
                : "该来源未提供可显示摘要。";
        public string AutomationName => $"{Title}，{DomainAndProvider}，{MatchLabel}";
        public SourceRow(CustomerEnrichmentSource source) => Source = source;

        private static string MatchStatusLabel(CustomerEnrichmentVerificationStatus status) => status switch
        {
            CustomerEnrichmentVerificationStatus.Verified => "已核验",
            CustomerEnrichmentVerificationStatus.HumanConfirmed => "人工确认",
            CustomerEnrichmentVerificationStatus.LikelyMatch => "高可能",
            CustomerEnrichmentVerificationStatus.PossibleMatch => "待核验",
            CustomerEnrichmentVerificationStatus.Conflicting => "有冲突",
            CustomerEnrichmentVerificationStatus.Rejected => "不匹配",
            CustomerEnrichmentVerificationStatus.Outdated => "已过期",
            _ => "待核验"
        };
    }
}
