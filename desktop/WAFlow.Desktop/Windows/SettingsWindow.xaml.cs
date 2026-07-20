using System.Windows;
using System.Windows.Threading;
using WAFlow.Core;
using WAFlow.Core.Domain;

namespace WAFlow.Desktop.Windows;

public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly bool _aiOnly;
    private readonly DispatcherTimer _modelFetchTimer;
    private CancellationTokenSource? _modelFetchCancellation;
    private List<string> _availableModels = [];
    private string _modelsBaseUrl = "";
    private DateTimeOffset? _modelsFetchedAt;
    private bool _loaded;

    public SettingsWindow(AppServices services, bool aiOnly = false)
    {
        InitializeComponent();
        _services = services;
        _aiOnly = aiOnly;
        _modelFetchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _modelFetchTimer.Tick += async (_, _) => { _modelFetchTimer.Stop(); await FetchModelsAsync(false); };
        Loaded += SettingsWindow_Loaded;
        Closed += (_, _) => _modelFetchCancellation?.Cancel();
    }

    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_aiOnly)
        {
            Title = "AI Sales OS · 配置 AI API";
            SettingsTitleText.Text = "接入 AI API";
            SettingsSubtitleText.Text = "支持 DeepSeek 或 OpenAI Chat Completions 兼容接口；保存 Key 后会自动拉取模型，无需填写企业资料。";
            SalesProfileCard.Visibility = Visibility.Collapsed;
            Height = 500;
        }
        var profile = await _services.Repository.GetSalesProfileAsync(); var settings = await _services.Repository.GetAppSettingsAsync();
        if (profile is not null)
        {
            CompanyNameBox.Text = profile.CompanyName; ProductsBox.Text = string.Join(Environment.NewLine, profile.Products); AdvantagesBox.Text = string.Join(Environment.NewLine, profile.Advantages);
            LanguageBox.Text = profile.DefaultLanguage; MarketsBox.Text = string.Join(", ", profile.TargetMarkets);
        }
        BaseUrlBox.Text = settings.DeepSeekBaseUrl;
        _availableModels = settings.AvailableModels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _modelsBaseUrl = settings.ModelsBaseUrl;
        _modelsFetchedAt = settings.ModelsFetchedAt;
        SetModelItems(settings.DeepSeekModel);
        ApiStatusText.Text = _services.DeepSeek.HasApiKey() ? "已安全配置" : "未配置";
        DatabasePathText.Text = _services.Repository.DatabasePath;
        _loaded = true;
        if (_services.DeepSeek.HasApiKey()) await FetchModelsAsync(false);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var products = Lines(ProductsBox.Text); var advantages = Lines(AdvantagesBox.Text);
        if (!_aiOnly && (string.IsNullOrWhiteSpace(CompanyNameBox.Text) || products.Count == 0 || advantages.Count == 0)) { MessageBox.Show("请填写企业名称、至少一个主营产品和至少一个企业优势。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!Uri.TryCreate(BaseUrlBox.Text.Trim(), UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps) { MessageBox.Show("AI Base URL 必须是有效的 HTTPS 地址。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (string.IsNullOrWhiteSpace(ModelBox.Text))
        {
            if (!await FetchModelsAsync(true)) return;
        }
        if (string.IsNullOrWhiteSpace(ModelBox.Text)) { MessageBox.Show("请从自动拉取的模型列表中选择一个工作模型。", "AI Sales OS"); return; }
        if (!_services.DeepSeek.HasApiKey() && string.IsNullOrWhiteSpace(ApiKeyBox.Password)) { MessageBox.Show("首次配置请填写 API Key。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var normalizedBaseUrl = baseUri.ToString().TrimEnd('/');
        if (!_modelsBaseUrl.Equals(normalizedBaseUrl, StringComparison.OrdinalIgnoreCase) || !_availableModels.Contains(ModelBox.Text.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            if (!await FetchModelsAsync(true)) return;
        }
        SaveButton.IsEnabled = false;
        try
        {
            if (!_aiOnly)
            {
                var profile = new SalesProfile { CompanyName=CompanyNameBox.Text.Trim(), Products=products, Advantages=advantages, DefaultLanguage=string.IsNullOrWhiteSpace(LanguageBox.Text) ? "en" : LanguageBox.Text.Trim(), TargetMarkets=MarketsBox.Text.Split([',','，',';','；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList() };
                await _services.Repository.SaveSalesProfileAsync(profile);
            }
            if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password)) _services.Secrets.Save(ApiKeyBox.Password.Trim());
            await _services.Repository.SaveAppSettingsAsync(new AppSettings
            {
                DeepSeekBaseUrl=normalizedBaseUrl,
                DeepSeekModel=ModelBox.Text.Trim(),
                AvailableModels=_availableModels,
                ModelsBaseUrl=_modelsBaseUrl,
                ModelsFetchedAt=_modelsFetchedAt
            });
            await _services.LeadAutomation.NotifyProviderConfiguredAsync();
            DialogResult = true;
        }
        catch (Exception error) { MessageBox.Show(error.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error); SaveButton.IsEnabled = true; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private async void ReloadModels_Click(object sender, RoutedEventArgs e) => await FetchModelsAsync(true);

    private void ProviderInput_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _modelFetchTimer.Stop();
        if ((ApiKeyBox.Password.Trim().Length >= 6 || _services.DeepSeek.HasApiKey()) && Uri.TryCreate(BaseUrlBox.Text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            _modelFetchTimer.Start();
    }

    private async Task<bool> FetchModelsAsync(bool showError)
    {
        if (!Uri.TryCreate(BaseUrlBox.Text.Trim(), UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            if (showError) MessageBox.Show("AI Base URL 必须是有效的 HTTPS 地址。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!_services.DeepSeek.HasApiKey() && string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            ModelStatusText.Text = "填写 API Key 后将自动拉取可用模型。";
            if (showError) MessageBox.Show("请先填写 API Key。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        _modelFetchCancellation?.Cancel();
        _modelFetchCancellation = new CancellationTokenSource();
        ReloadModelsButton.IsEnabled = false;
        ModelStatusText.Text = "正在从 API 拉取全部可用模型…";
        try
        {
            var selected = ModelBox.Text.Trim();
            var catalog = await _services.DeepSeek.DiscoverModelsAsync(baseUri.ToString().TrimEnd('/'), ApiKeyBox.Password, _modelFetchCancellation.Token);
            _availableModels = catalog.Models.ToList();
            _modelsBaseUrl = baseUri.ToString().TrimEnd('/');
            _modelsFetchedAt = catalog.FetchedAt;
            SetModelItems(_availableModels.Contains(selected, StringComparer.OrdinalIgnoreCase) ? selected : _availableModels.First());
            ModelStatusText.Text = $"已拉取 {_availableModels.Count} 个模型 · {catalog.FetchedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}；请选择本程序使用的模型。";
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error)
        {
            ModelStatusText.Text = $"模型拉取失败：{error.Message}";
            if (showError) MessageBox.Show(error.Message, "模型拉取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { ReloadModelsButton.IsEnabled = true; }
    }

    private void SetModelItems(string selected)
    {
        if (!string.IsNullOrWhiteSpace(selected) && !_availableModels.Contains(selected, StringComparer.OrdinalIgnoreCase)) _availableModels.Insert(0, selected);
        ModelBox.ItemsSource = null;
        ModelBox.ItemsSource = _availableModels;
        ModelBox.SelectedItem = _availableModels.FirstOrDefault(model => model.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? _availableModels.FirstOrDefault();
        if (_availableModels.Count > 0)
        {
            var age = _modelsFetchedAt is null ? "本地缓存" : _modelsFetchedAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            ModelStatusText.Text = $"已缓存 {_availableModels.Count} 个模型 · {age}；打开设置时会自动刷新。";
        }
    }

    private static List<string> Lines(string value) => value.Split(["\r\n","\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
}
