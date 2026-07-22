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
    private bool _loading;

    public EmailAccountWindow(AppServices services, EmailAccount? account = null)
    {
        InitializeComponent();
        _services = services;
        _account = account ?? new EmailAccount();
        _loading = true;
        ProviderBox.ItemsSource = EmailService.ProviderPresets;
        ProviderBox.SelectedItem = EmailService.ProviderPresets.First(item => item.Provider == _account.Provider);
        DisplayNameBox.Text = _account.DisplayName;
        EmailBox.Text = _account.EmailAddress;
        UserNameBox.Text = _account.UserName;
        ImapHostBox.Text = _account.ImapHost;
        ImapPortBox.Text = _account.ImapPort.ToString(CultureInfo.InvariantCulture);
        ImapSslBox.IsChecked = _account.ImapUseSsl;
        SmtpHostBox.Text = _account.SmtpHost;
        SmtpPortBox.Text = _account.SmtpPort.ToString(CultureInfo.InvariantCulture);
        SmtpSslBox.IsChecked = _account.SmtpUseSsl;
        StatusText.Text = string.IsNullOrWhiteSpace(_account.LastError) ? _account.StatusLabel : $"上次状态：{_account.LastError}";
        DeleteButton.Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;
        _loading = false;
        if (account is null) ApplyPreset(EmailProviderKind.Gmail);
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProviderBox.SelectedItem is not EmailProviderPreset preset) return;
        ApplyPreset(preset.Provider);
    }

    private void ApplyPreset(EmailProviderKind provider)
    {
        var preset = EmailService.Preset(provider);
        if (provider == EmailProviderKind.Custom) return;
        ImapHostBox.Text = preset.ImapHost; ImapPortBox.Text = preset.ImapPort.ToString(CultureInfo.InvariantCulture); ImapSslBox.IsChecked = true;
        SmtpHostBox.Text = preset.SmtpHost; SmtpPortBox.Text = preset.SmtpPort.ToString(CultureInfo.InvariantCulture); SmtpSslBox.IsChecked = true;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveButton.IsEnabled = false; StatusText.Text = "正在验证 IMAP 与 SMTP 连接…";
            _account.Provider = (ProviderBox.SelectedItem as EmailProviderPreset)?.Provider ?? EmailProviderKind.Custom;
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
