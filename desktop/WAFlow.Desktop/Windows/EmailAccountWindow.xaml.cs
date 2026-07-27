using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Windows;

public partial class EmailAccountWindow : Window
{
    private readonly AppServices _services;
    private readonly EmailAccount _account;
    private readonly bool _isEditing;
    private bool _loading = true;
    private bool _userNameFollowsEmail;

    public EmailAccountWindow(AppServices services, EmailAccount? account = null)
    {
        InitializeComponent();
        _services = services;
        _account = account ?? new EmailAccount();
        _isEditing = account is not null;
        _loading = true;
        ProviderBox.ItemsSource = EmailService.ProviderPresets;
        var initialProvider = account?.Provider ?? EmailProviderKind.Gmail;
        ProviderBox.SelectedItem = EmailService.ProviderPresets.First(item => item.Provider == initialProvider);
        DisplayNameBox.Text = _account.DisplayName;
        EmailBox.Text = _account.EmailAddress;
        UserNameBox.Text = _account.UserName;
        _userNameFollowsEmail = account is null
            || string.IsNullOrWhiteSpace(_account.UserName)
            || _account.UserName.Equals(_account.EmailAddress, StringComparison.OrdinalIgnoreCase);
        ImapHostBox.Text = _account.ImapHost;
        ImapPortBox.Text = _account.ImapPort.ToString(CultureInfo.InvariantCulture);
        ImapSslBox.IsChecked = _account.ImapUseSsl;
        SmtpHostBox.Text = _account.SmtpHost;
        SmtpPortBox.Text = _account.SmtpPort.ToString(CultureInfo.InvariantCulture);
        SmtpSslBox.IsChecked = _account.SmtpUseSsl;
        StatusText.Text = string.IsNullOrWhiteSpace(_account.LastError) ? _account.StatusLabel : $"上次状态：{_account.LastError}";
        DeleteButton.Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;
        _loading = false;
        if (account is null) ApplyPreset(initialProvider);
        else ApplyGuide(initialProvider);
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProviderBox.SelectedItem is not EmailProviderPreset preset) return;
        ApplyPreset(preset.Provider);
    }

    private void ApplyPreset(EmailProviderKind provider)
    {
        var preset = EmailService.Preset(provider);
        if (provider != EmailProviderKind.Custom)
        {
            ImapHostBox.Text = preset.ImapHost;
            ImapPortBox.Text = preset.ImapPort.ToString(CultureInfo.InvariantCulture);
            ImapSslBox.IsChecked = true;
            SmtpHostBox.Text = preset.SmtpHost;
            SmtpPortBox.Text = preset.SmtpPort.ToString(CultureInfo.InvariantCulture);
            SmtpSslBox.IsChecked = true;
        }
        else
        {
            ImapHostBox.Clear();
            ImapPortBox.Clear();
            ImapSslBox.IsChecked = true;
            SmtpHostBox.Clear();
            SmtpPortBox.Clear();
            SmtpSslBox.IsChecked = true;
        }
        ApplyGuide(provider);
    }

    private void ApplyGuide(EmailProviderKind provider)
    {
        var guide = EmailService.Guide(provider);
        GuideTitleText.Text = guide.Title;
        GuideBadgeText.Text = guide.Badge;
        GuideSummaryText.Text = guide.Summary;
        GuideStepsText.Text = string.Join(Environment.NewLine, guide.Steps.Select((step, index) => $"{index + 1}. {step}"));
        GuideCompatibilityText.Text = guide.CompatibilityNote;
        EmailHintText.Text = guide.EmailHint;
        UserNameHintText.Text = guide.UserNameHint;
        PasswordLabelText.Text = _isEditing ? $"{guide.PasswordLabel}（留空则保留现有凭据）" : guide.PasswordLabel;
        PasswordHintText.Text = _isEditing ? $"{guide.PasswordHint} 本次不更换凭据时可留空。" : guide.PasswordHint;
        ProviderSetupButton.Content = guide.SetupButtonLabel;
        ProviderSetupButton.Visibility = string.IsNullOrWhiteSpace(guide.SetupUrl) ? Visibility.Collapsed : Visibility.Visible;
        ProviderHelpButton.Content = guide.HelpButtonLabel;
        ProviderHelpButton.Visibility = string.IsNullOrWhiteSpace(guide.HelpUrl) ? Visibility.Collapsed : Visibility.Visible;
        ResetPresetButton.Visibility = provider == EmailProviderKind.Custom ? Visibility.Collapsed : Visibility.Visible;
        ServerPresetText.Text = provider == EmailProviderKind.Custom
            ? "请按邮箱服务商或企业管理员提供的参数填写；不要猜测主机与端口。"
            : $"已按 {EmailService.Preset(provider).Label} 自动填写推荐参数；仅在官方或管理员明确要求时修改。";
    }

    private void EmailBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!_userNameFollowsEmail && !string.IsNullOrWhiteSpace(UserNameBox.Text)) return;
        _loading = true;
        UserNameBox.Text = EmailBox.Text.Trim();
        UserNameBox.CaretIndex = UserNameBox.Text.Length;
        _loading = false;
        _userNameFollowsEmail = true;
    }

    private void UserNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _userNameFollowsEmail = string.IsNullOrWhiteSpace(UserNameBox.Text)
            || UserNameBox.Text.Trim().Equals(EmailBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void ProviderSetup_Click(object sender, RoutedEventArgs e) => OpenGuideUrl(EmailService.Guide(SelectedProvider()).SetupUrl);

    private void ProviderHelp_Click(object sender, RoutedEventArgs e) => OpenGuideUrl(EmailService.Guide(SelectedProvider()).HelpUrl);

    private void ResetPreset_Click(object sender, RoutedEventArgs e) => ApplyPreset(SelectedProvider());

    private EmailProviderKind SelectedProvider() =>
        (ProviderBox.SelectedItem as EmailProviderPreset)?.Provider ?? EmailProviderKind.Custom;

    private void OpenGuideUrl(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            MessageBox.Show($"无法打开网页：{error.Message}", "邮件平台引导", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveButton.IsEnabled = false; StatusText.Text = "正在验证 IMAP 与 SMTP 连接…";
            _account.Provider = SelectedProvider();
            _account.DisplayName = DisplayNameBox.Text.Trim(); _account.EmailAddress = EmailBox.Text.Trim();
            _account.UserName = string.IsNullOrWhiteSpace(UserNameBox.Text) ? _account.EmailAddress : UserNameBox.Text.Trim();
            _account.ImapHost = ImapHostBox.Text.Trim(); _account.SmtpHost = SmtpHostBox.Text.Trim();
            if (!int.TryParse(ImapPortBox.Text, out var imapPort) || !int.TryParse(SmtpPortBox.Text, out var smtpPort)) throw new InvalidOperationException("服务器端口必须是整数。");
            _account.ImapPort = imapPort; _account.SmtpPort = smtpPort;
            _account.ImapUseSsl = ImapSslBox.IsChecked == true; _account.SmtpUseSsl = SmtpSslBox.IsChecked == true;
            await _services.Email.SaveAndTestAccountAsync(_account, PasswordBox.Password);
            DialogResult = true;
        }
        catch (Exception error) { StatusText.Text = error.Message; MessageBox.Show(error.Message, "邮件连接失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SaveButton.IsEnabled = true; }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show($"删除邮件账号“{_account.DisplayLabel}”吗？本地邮件历史会同时删除。", "删除邮件账号", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _services.Email.DeleteAccountAsync(_account);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
