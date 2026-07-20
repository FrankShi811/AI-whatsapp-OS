using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Pages;

public partial class CampaignsView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private readonly ObservableCollection<WhatsAppCampaign> _campaigns = [];
    private readonly ObservableCollection<AudienceRow> _rows = [];
    private readonly ObservableCollection<WhatsAppAccount> _accounts = [];
    private WhatsAppCampaign? _current;

    public event EventHandler? DataChanged;

    public CampaignsView(AppServices services)
    {
        InitializeComponent();
        _services = services;
        CampaignList.ItemsSource = _campaigns;
        AudienceGrid.ItemsSource = _rows;
        AccountBox.ItemsSource = _accounts;
        GradeBox.ItemsSource = new[] { "全部", "A", "B", "C", "D" };
        StageBox.ItemsSource = new[] { new StageChoice("全部阶段", (LeadStage?)null) }.Concat(Enum.GetValues<LeadStage>().Select(x => new StageChoice(Labels.Stage(x), x))).ToList();
        _services.Campaigns.CampaignChanged += (_, _) => Dispatcher.InvokeAsync(async () => await RefreshAsync());
        _services.WhatsApp.EventReceived += (_, e) => { if (e.Name == "connection") Dispatcher.InvokeAsync(UpdateConnectionState); };
        ResetForm();
    }

    public async Task RefreshAsync()
    {
        var selectedId = _current?.Id;
        var accounts = await _services.Repository.GetWhatsAppAccountsAsync();
        _accounts.Clear(); foreach (var account in accounts) _accounts.Add(account);
        if (AccountBox.SelectedItem is null) AccountBox.SelectedItem = _accounts.FirstOrDefault(x => x.Id == _current?.AccountId) ?? _accounts.FirstOrDefault();
        var loaded = await _services.Repository.GetCampaignsAsync(null);
        _campaigns.Clear(); foreach (var campaign in loaded) _campaigns.Add(campaign);
        CampaignCountText.Text = loaded.Count.ToString(CultureInfo.InvariantCulture);
        ActiveCountText.Text = loaded.Count(x => x.Status is CampaignStatus.Scheduled or CampaignStatus.Running).ToString(CultureInfo.InvariantCulture);
        var sent = 0;
        foreach (var campaign in loaded) sent += (await _services.Repository.GetCampaignRecipientsAsync(campaign.Id)).Count(x => x.Status == CampaignRecipientStatus.Sent);
        SentCountText.Text = sent.ToString(CultureInfo.InvariantCulture);
        UpdateConnectionState();
        if (selectedId is not null && loaded.FirstOrDefault(x => x.Id == selectedId) is { } selected)
        {
            _current = selected; CampaignList.SelectedItem = selected; await LoadCampaignAsync(selected);
        }
        else if (loaded.Count > 0 && _current is not null)
        {
            CampaignList.SelectedIndex = 0;
        }
    }

    private void New_Click(object sender, RoutedEventArgs e) => ResetForm();

    private async void CampaignList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CampaignList.SelectedItem is WhatsAppCampaign campaign) await LoadCampaignAsync(campaign);
    }

    private async Task LoadCampaignAsync(WhatsAppCampaign campaign)
    {
        _current = campaign;
        AccountBox.SelectedItem = _accounts.FirstOrDefault(x => x.Id == campaign.AccountId) ?? _accounts.FirstOrDefault();
        NameBox.Text = campaign.Name; GradeBox.SelectedItem = campaign.GradeFilter;
        StageBox.SelectedItem = StageBox.Items.Cast<StageChoice>().FirstOrDefault(x => x.Value == campaign.StageFilter);
        TagBox.Text = campaign.TagFilter; OwnerBox.Text = campaign.OwnerFilter; TemplateBox.Text = campaign.MessageTemplate;
        StartAtBox.Text = FormatBeijing(campaign.StartsAt); IntervalBox.Text = campaign.IntervalMinutes.ToString(CultureInfo.InvariantCulture); DailyLimitBox.Text = campaign.DailyLimit.ToString(CultureInfo.InvariantCulture);
        var recipients = await _services.Repository.GetCampaignRecipientsAsync(campaign.Id);
        _rows.Clear();
        foreach (var item in recipients) _rows.Add(new AudienceRow(item.DisplayName, "", item.StatusLabel, string.IsNullOrWhiteSpace(item.LastError) ? item.ScheduledLabel : item.LastError));
        EligibleCountText.Text = $"{recipients.Count(x => x.Status is CampaignRecipientStatus.Queued or CampaignRecipientStatus.Sending or CampaignRecipientStatus.Sent)} 可发送";
        PreviewSummaryText.Text = recipients.Count == 0 ? "草稿尚未生成发送队列" : $"队列 {recipients.Count} 人 · 已发送 {recipients.Count(x => x.Status == CampaignRecipientStatus.Sent)} · 失败 {recipients.Count(x => x.Status == CampaignRecipientStatus.Failed)}";
        ApplyState(campaign);
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var campaign = ReadForm();
            var audience = await _services.Campaigns.PreviewAudienceAsync(campaign);
            _rows.Clear();
            foreach (var item in audience) _rows.Add(new AudienceRow(item.DisplayName, item.Grade, item.EligibilityLabel, item.Reason));
            var eligible = audience.Count(x => x.Eligible);
            EligibleCountText.Text = $"{eligible} 可发送";
            PreviewSummaryText.Text = $"筛选命中 {audience.Count} 人 · 排除 {audience.Count - eligible} 人";
        }
        catch (Exception error) { ShowError(error, "预览失败"); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var campaign = ReadForm();
            await _services.Campaigns.SaveDraftAsync(campaign);
            _current = campaign; await RefreshAsync(); DataChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show("Campaign 草稿已保存。", "Campaign Automation", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { ShowError(error, "保存失败"); }
    }

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var campaign = ReadForm();
            var audience = await _services.Campaigns.PreviewAudienceAsync(campaign);
            var eligible = audience.Count(x => x.Eligible);
            if (eligible == 0) throw new InvalidOperationException("没有符合发送条件的客户。");
            var confirmation = $"将向 {eligible} 位已同意客户自动逐条发送。\n\n开始：{FormatBeijing(campaign.StartsAt)}（北京时间）\n固定间隔：{campaign.IntervalMinutes} 分钟\n每日上限：{campaign.DailyLimit}\n\n个人号非官方连接可能被限制或封号。确认批准并启动排期吗？";
            if (MessageBox.Show(confirmation, "批准自动发送", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var count = await _services.Campaigns.ApproveAndScheduleAsync(campaign);
            _current = campaign; await RefreshAsync(); DataChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show($"已生成 {count} 条逐人发送任务。请保持 AI Sales OS 运行并在 WhatsApp Inbox 完成连接。", "Campaign 已排期", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { ShowError(error, "排期失败"); }
    }

    private async void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        try
        {
            if (_current.Status == CampaignStatus.Paused) await _services.Campaigns.ResumeAsync(_current);
            else await _services.Campaigns.PauseAsync(_current);
            await RefreshAsync();
        }
        catch (Exception error) { ShowError(error, "操作失败"); }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || MessageBox.Show("取消后，尚未发送的客户将不会再发送。是否继续？", "取消 Campaign", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _services.Campaigns.CancelAsync(_current); await RefreshAsync(); }
        catch (Exception error) { ShowError(error, "取消失败"); }
    }

    private WhatsAppCampaign ReadForm()
    {
        var campaign = _current is { Status: CampaignStatus.Draft } ? _current : new WhatsAppCampaign();
        campaign.AccountId = (AccountBox.SelectedItem as WhatsAppAccount)?.Id ?? "primary";
        campaign.Name = NameBox.Text.Trim(); campaign.GradeFilter = GradeBox.SelectedItem as string ?? "全部";
        campaign.StageFilter = (StageBox.SelectedItem as StageChoice)?.Value; campaign.TagFilter = TagBox.Text.Trim(); campaign.OwnerFilter = OwnerBox.Text.Trim();
        campaign.MessageTemplate = TemplateBox.Text.Trim(); campaign.StartsAt = ParseBeijing(StartAtBox.Text);
        if (!int.TryParse(IntervalBox.Text, out var interval)) throw new InvalidOperationException("发送间隔必须是整数分钟。");
        if (!int.TryParse(DailyLimitBox.Text, out var limit)) throw new InvalidOperationException("每日上限必须是整数。");
        campaign.IntervalMinutes = interval; campaign.DailyLimit = limit; return campaign;
    }

    private void ResetForm()
    {
        CampaignList.SelectedItem = null; _current = new WhatsAppCampaign { Name = $"新 Campaign {DateTime.Now:MM-dd}" };
        AccountBox.SelectedItem = _accounts.FirstOrDefault(x => x.Id == _services.WhatsApp.ActiveAccountId) ?? _accounts.FirstOrDefault();
        NameBox.Text = _current.Name; GradeBox.SelectedIndex = 0; StageBox.SelectedIndex = 0; TagBox.Clear(); OwnerBox.Clear();
        TemplateBox.Text = _current.MessageTemplate; StartAtBox.Text = FormatBeijing(_current.StartsAt); IntervalBox.Text = "5"; DailyLimitBox.Text = "50";
        _rows.Clear(); PreviewSummaryText.Text = "尚未预览"; EligibleCountText.Text = "0 可发送"; CampaignStateText.Text = "新草稿"; PauseReasonText.Text = "";
        ApplyState(_current);
    }

    private void ApplyState(WhatsAppCampaign campaign)
    {
        var editable = campaign.Status == CampaignStatus.Draft;
        foreach (var control in new Control[] { AccountBox, NameBox, GradeBox, StageBox, TagBox, OwnerBox, TemplateBox, StartAtBox, IntervalBox, DailyLimitBox }) control.IsEnabled = editable;
        PreviewButton.IsEnabled = editable; SaveButton.IsEnabled = editable; ApproveButton.IsEnabled = editable;
        PauseResumeButton.IsEnabled = campaign.Status is CampaignStatus.Scheduled or CampaignStatus.Running or CampaignStatus.Paused;
        PauseResumeButton.Content = campaign.Status == CampaignStatus.Paused ? "继续" : "暂停";
        CancelButton.IsEnabled = campaign.Status is CampaignStatus.Scheduled or CampaignStatus.Running or CampaignStatus.Paused;
        CampaignStateText.Text = campaign.StatusLabel + (campaign.ApprovedAt is null ? "" : $" · {campaign.ApprovedBy} 已批准");
        PauseReasonText.Text = campaign.PauseReason;
        EditHintText.Text = editable ? "先保存草稿并预览受众，再一次性批准自动发送。" : "发送快照已锁定；暂停不会改变已生成的话术和受众。";
    }

    private void UpdateConnectionState()
    {
        var accountId = (AccountBox.SelectedItem as WhatsAppAccount)?.Id ?? "primary";
        var connected = _services.WhatsApp.IsConnectedFor(accountId); var state = _services.WhatsApp.ConnectionStateFor(accountId);
        ConnectionText.Text = connected ? "已连接" : state switch { "connecting" => "连接中 / 等待扫码", "logged_out" => "登录已失效", _ => "未连接" };
        ConnectionText.Foreground = connected ? (System.Windows.Media.Brush)FindResource("Primary") : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 92, 22));
    }

    private static DateTimeOffset ParseBeijing(string value)
    {
        if (!DateTime.TryParse(value.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var local) && !DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out local))
            throw new InvalidOperationException("开始时间格式无效，请填写例如 2026-07-18 09:30。");
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified); var zone = BeijingZone();
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static string FormatBeijing(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, BeijingZone()).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private static TimeZoneInfo BeijingZone()
    {
        foreach (var id in new[] { "China Standard Time", "Asia/Shanghai" }) try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.Local;
    }

    private static void ShowError(Exception error, string title) => MessageBox.Show(error.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    private sealed record StageChoice(string Label, LeadStage? Value);
    private sealed record AudienceRow(string DisplayName, string Grade, string StatusText, string Detail);
}
