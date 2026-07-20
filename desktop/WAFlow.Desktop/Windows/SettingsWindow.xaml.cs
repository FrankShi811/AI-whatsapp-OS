using System.Windows;
using WAFlow.Core;
using WAFlow.Core.Domain;

namespace WAFlow.Desktop.Windows;

public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    public SettingsWindow(AppServices services) { InitializeComponent(); _services = services; Loaded += SettingsWindow_Loaded; }

    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var profile = await _services.Repository.GetSalesProfileAsync(); var settings = await _services.Repository.GetAppSettingsAsync();
        if (profile is not null)
        {
            CompanyNameBox.Text = profile.CompanyName; ProductsBox.Text = string.Join(Environment.NewLine, profile.Products); AdvantagesBox.Text = string.Join(Environment.NewLine, profile.Advantages);
            LanguageBox.Text = profile.DefaultLanguage; MarketsBox.Text = string.Join(", ", profile.TargetMarkets);
        }
        BaseUrlBox.Text = settings.DeepSeekBaseUrl; ModelBox.Text = settings.DeepSeekModel;
        ApiStatusText.Text = _services.DeepSeek.HasApiKey() ? "已安全配置" : "未配置";
        DatabasePathText.Text = _services.Repository.DatabasePath;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var products = Lines(ProductsBox.Text); var advantages = Lines(AdvantagesBox.Text);
        if (string.IsNullOrWhiteSpace(CompanyNameBox.Text) || products.Count == 0 || advantages.Count == 0) { MessageBox.Show("请填写企业名称、至少一个主营产品和至少一个企业优势。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!Uri.TryCreate(BaseUrlBox.Text.Trim(), UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps) { MessageBox.Show("DeepSeek Base URL 必须是有效的 HTTPS 地址。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (string.IsNullOrWhiteSpace(ModelBox.Text)) { MessageBox.Show("请填写 DeepSeek 模型名称。", "AI Sales OS"); return; }
        SaveButton.IsEnabled = false;
        try
        {
            var profile = new SalesProfile { CompanyName=CompanyNameBox.Text.Trim(), Products=products, Advantages=advantages, DefaultLanguage=string.IsNullOrWhiteSpace(LanguageBox.Text) ? "en" : LanguageBox.Text.Trim(), TargetMarkets=MarketsBox.Text.Split([',','，',';','；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList() };
            await _services.Repository.SaveSalesProfileAsync(profile);
            await _services.Repository.SaveAppSettingsAsync(new AppSettings { DeepSeekBaseUrl=baseUri.ToString().TrimEnd('/'), DeepSeekModel=ModelBox.Text.Trim() });
            if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password)) _services.Secrets.Save(ApiKeyBox.Password.Trim());
            DialogResult = true;
        }
        catch (Exception error) { MessageBox.Show(error.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error); SaveButton.IsEnabled = true; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private static List<string> Lines(string value) => value.Split(["\r\n","\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
}
