using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Windows;

public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _modelFetchTimer;
    private readonly Dictionary<string, AiProviderProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pendingKeys = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _modelFetchCancellation;
    private AppSettings _settings = new();
    private List<string> _availableModels = [];
    private string _modelsBaseUrl = "";
    private DateTimeOffset? _modelsFetchedAt;
    private string _currentProviderId = "deepseek";
    private bool _loaded;
    private OnboardingState _onboardingState = new();

    public SettingsWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _modelFetchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _modelFetchTimer.Tick += async (_, _) =>
        {
            _modelFetchTimer.Stop();
            await FetchModelsAsync(false);
        };
        SettingsGuide.AllowGlobalLink = false;
        SettingsGuide.CloseRequested += SettingsGuide_CloseRequested;
        SettingsGuide.FinishedRequested += SettingsGuide_FinishedRequested;
        Loaded += SettingsWindow_Loaded;
        Closed += (_, _) =>
        {
            _modelFetchCancellation?.Cancel();
            SettingsGuide.CloseRequested -= SettingsGuide_CloseRequested;
            SettingsGuide.FinishedRequested -= SettingsGuide_FinishedRequested;
        };
    }

    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _services.Repository.GetAppSettingsAsync();
        ThemeModeBox.ItemsSource = new[]
        {
            new ThemeOption("跟随 Windows 系统", "System"),
            new ThemeOption("浅色", "Light"),
            new ThemeOption("深色", "Dark")
        };
        ThemeModeBox.DisplayMemberPath = nameof(ThemeOption.Label);
        ThemeModeBox.SelectedItem = ((IEnumerable<ThemeOption>)ThemeModeBox.ItemsSource)
            .First(item => item.Value == ThemeManager.Normalize(_settings.ThemeMode));

        foreach (var profile in _settings.ConfiguredAiProviders)
            _profiles[profile.ProviderId] = Clone(profile);

        MigrateLegacyProvider();
        AiProviderBox.ItemsSource = AiProviderCatalog.Supported;
        _currentProviderId = AiProviderCatalog.Resolve(_settings.ActiveProviderId).Id;
        AiProviderBox.SelectedItem = AiProviderCatalog.Resolve(_currentProviderId);
        LoadProvider(_currentProviderId);

        DatabasePathText.Text = _services.Repository.DatabasePath;
        _onboardingState = await _services.Repository.GetOnboardingStateAsync();
        if (GuideCatalog.MigrateLegacyState(_onboardingState))
            await _services.Repository.SaveOnboardingStateAsync(_onboardingState);
        _loaded = true;
        RefreshConfiguredProviders();

        if (HasProviderKey(_currentProviderId))
            await FetchModelsAsync(false);
        if (!GuideCatalog.IsSeen(_onboardingState, "settings"))
            SettingsGuide.ShowGuide(GuideCatalog.ForModule("settings"));
    }

    private void MigrateLegacyProvider()
    {
        var active = AiProviderCatalog.Resolve(_settings.ActiveProviderId);
        if (!_profiles.ContainsKey(active.Id))
        {
            _profiles[active.Id] = new AiProviderProfile
            {
                ProviderId = active.Id,
                DisplayName = active.DisplayName,
                BaseUrl = string.IsNullOrWhiteSpace(_settings.DeepSeekBaseUrl) ? active.DefaultBaseUrl : _settings.DeepSeekBaseUrl,
                Model = _settings.DeepSeekModel,
                AvailableModels = _settings.AvailableModels.ToList(),
                ModelsFetchedAt = _settings.ModelsFetchedAt,
                IsConfigured = _services.DeepSeek.HasApiKey()
            };
        }

        // Existing installations stored the active key under the historical
        // DeepSeek target. Copy it once to the provider-specific credential.
        var legacyKey = _services.Secrets.Read();
        var providerStore = ProviderCredentialStore(active.Id);
        if (!string.IsNullOrWhiteSpace(legacyKey) && string.IsNullOrWhiteSpace(providerStore.Read()))
            providerStore.Save(legacyKey);
    }

    private void AiProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || AiProviderBox.SelectedItem is not AiProviderDefinition selected
            || selected.Id.Equals(_currentProviderId, StringComparison.OrdinalIgnoreCase)) return;
        CaptureCurrentProvider();
        _currentProviderId = selected.Id;
        LoadProvider(_currentProviderId);
        RefreshConfiguredProviders();
    }

    private void LoadProvider(string providerId)
    {
        _modelFetchTimer.Stop();
        _modelFetchCancellation?.Cancel();
        var definition = AiProviderCatalog.Resolve(providerId);
        if (!_profiles.TryGetValue(providerId, out var profile))
        {
            profile = new AiProviderProfile
            {
                ProviderId = definition.Id,
                DisplayName = definition.DisplayName,
                BaseUrl = definition.DefaultBaseUrl,
                Model = definition.ExampleModels.FirstOrDefault() ?? ""
            };
            _profiles[providerId] = profile;
        }

        ProviderDescriptionText.Text = definition.Description
            + (definition.ExampleModels.Count == 0 ? "" : $"；常用模型示例：{string.Join("、", definition.ExampleModels)}。实际可用模型以 API 实时拉取结果为准。");
        BaseUrlBox.Text = string.IsNullOrWhiteSpace(profile.BaseUrl) ? definition.DefaultBaseUrl : profile.BaseUrl;
        ApiKeyBox.Clear();
        _availableModels = profile.AvailableModels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _modelsBaseUrl = profile.BaseUrl;
        _modelsFetchedAt = profile.ModelsFetchedAt;
        SetModelItems(profile.Model);
        ApiStatusText.Text = HasProviderKey(providerId) ? "已安全配置" : "未配置";
        ModelStatusText.Text = _availableModels.Count > 0
            ? $"已缓存 {_availableModels.Count} 个模型；点击“拉取”可验证 Key 并刷新。"
            : "填写 API Key 后点击“拉取”，验证连接并获取该账号可用模型。";
    }

    private void CaptureCurrentProvider()
    {
        var definition = AiProviderCatalog.Resolve(_currentProviderId);
        if (!_profiles.TryGetValue(_currentProviderId, out var profile))
            _profiles[_currentProviderId] = profile = new AiProviderProfile { ProviderId = definition.Id, DisplayName = definition.DisplayName };
        profile.DisplayName = definition.DisplayName;
        profile.BaseUrl = BaseUrlBox.Text.Trim();
        profile.Model = ModelBox.Text.Trim();
        profile.AvailableModels = _availableModels.ToList();
        profile.ModelsFetchedAt = _modelsFetchedAt;
        if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            _pendingKeys[_currentProviderId] = ApiKeyBox.Password.Trim();
            profile.IsConfigured = true;
        }
        else
        {
            profile.IsConfigured = HasProviderKey(_currentProviderId);
        }
    }

    private void ShowGuide_Click(object sender, RoutedEventArgs e) =>
        SettingsGuide.ShowGuide(GuideCatalog.ForModule("settings"));

    private async Task MarkSettingsGuideSeenAsync()
    {
        GuideCatalog.MarkSeen(_onboardingState, "settings");
        await _services.Repository.SaveOnboardingStateAsync(_onboardingState);
    }

    private async void SettingsGuide_CloseRequested(object? sender, EventArgs e)
    {
        await MarkSettingsGuideSeenAsync();
        SettingsGuide.HideGuide();
    }

    private async void SettingsGuide_FinishedRequested(object? sender, EventArgs e)
    {
        await MarkSettingsGuideSeenAsync();
        SettingsGuide.HideGuide();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        CaptureCurrentProvider();
        if (!_profiles.TryGetValue(_currentProviderId, out var active)) return;
        if (!Uri.TryCreate(active.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            MessageBox.Show("AI Base URL 必须是有效的 HTTPS 地址。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!HasProviderKey(_currentProviderId))
        {
            MessageBox.Show("请填写 API Key，并点击“拉取”完成连接验证。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(active.Model)
            || !active.AvailableModels.Contains(active.Model, StringComparer.OrdinalIgnoreCase)
            || !_modelsBaseUrl.Equals(active.BaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            if (!await FetchModelsAsync(true)) return;
            CaptureCurrentProvider();
            active = _profiles[_currentProviderId];
        }

        SaveButton.IsEnabled = false;
        try
        {
            foreach (var pending in _pendingKeys)
                ProviderCredentialStore(pending.Key).Save(pending.Value);

            var activeKey = ReadProviderKey(_currentProviderId);
            if (string.IsNullOrWhiteSpace(activeKey))
                throw new InvalidOperationException("当前 Provider 的 API Key 未通过验证。");

            // The existing provider service remains OpenAI-compatible and reads
            // this stable credential target. Keep it synchronized to the active
            // profile without exposing the key to settings or logs.
            _services.Secrets.Save(activeKey);
            foreach (var profile in _profiles.Values)
                profile.IsConfigured = HasProviderKey(profile.ProviderId);

            _settings.ActiveProviderId = _currentProviderId;
            _settings.ConfiguredAiProviders = _profiles.Values
                .Where(profile => profile.IsConfigured)
                .Select(Clone)
                .OrderBy(profile => profile.DisplayName)
                .ToList();
            _settings.DeepSeekBaseUrl = active.BaseUrl.TrimEnd('/');
            _settings.DeepSeekModel = active.Model;
            _settings.AvailableModels = active.AvailableModels.ToList();
            _settings.ModelsBaseUrl = active.BaseUrl.TrimEnd('/');
            _settings.ModelsFetchedAt = active.ModelsFetchedAt;
            _settings.ThemeMode = (ThemeModeBox.SelectedItem as ThemeOption)?.Value ?? "System";
            await _services.Repository.SaveAppSettingsAsync(_settings);
            ThemeManager.Apply(_settings.ThemeMode);
            await _services.LeadAutomation.NotifyProviderConfiguredAsync();
            DialogResult = true;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            SaveButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private async void ReloadModels_Click(object sender, RoutedEventArgs e) => await FetchModelsAsync(true);

    private void ProviderInput_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _modelFetchTimer.Stop();
        if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            _pendingKeys[_currentProviderId] = ApiKeyBox.Password.Trim();
        if (HasProviderKey(_currentProviderId)
            && Uri.TryCreate(BaseUrlBox.Text.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps)
            _modelFetchTimer.Start();
    }

    private async Task<bool> FetchModelsAsync(bool showError)
    {
        if (!Uri.TryCreate(BaseUrlBox.Text.Trim(), UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            if (showError) MessageBox.Show("AI Base URL 必须是有效的 HTTPS 地址。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var key = !string.IsNullOrWhiteSpace(ApiKeyBox.Password) ? ApiKeyBox.Password.Trim() : ReadProviderKey(_currentProviderId);
        if (string.IsNullOrWhiteSpace(key))
        {
            ModelStatusText.Text = "请先填写 API Key。";
            if (showError) MessageBox.Show("请先填写 API Key。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        _modelFetchCancellation?.Cancel();
        _modelFetchCancellation = new CancellationTokenSource();
        ReloadModelsButton.IsEnabled = false;
        ModelStatusText.Text = "正在验证 API Key 并拉取全部可用模型…";
        try
        {
            var selected = ModelBox.Text.Trim();
            var normalizedBaseUrl = baseUri.ToString().TrimEnd('/');
            var catalog = await _services.DeepSeek.DiscoverModelsAsync(normalizedBaseUrl, key, _modelFetchCancellation.Token);
            _pendingKeys[_currentProviderId] = key;
            _availableModels = catalog.Models.ToList();
            _modelsBaseUrl = normalizedBaseUrl;
            _modelsFetchedAt = catalog.FetchedAt;
            SetModelItems(_availableModels.Contains(selected, StringComparer.OrdinalIgnoreCase) ? selected : _availableModels.First());
            CaptureCurrentProvider();
            _profiles[_currentProviderId].IsConfigured = true;
            ApiStatusText.Text = "验证通过";
            ModelStatusText.Text = $"API Key 验证通过 · 已拉取 {_availableModels.Count} 个模型 · {catalog.FetchedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}";
            RefreshConfiguredProviders();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception error)
        {
            ApiStatusText.Text = "验证失败";
            ModelStatusText.Text = $"连接验证失败：{error.Message}";
            if (showError) MessageBox.Show(error.Message, "API 验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            ReloadModelsButton.IsEnabled = true;
        }
    }

    private void SetModelItems(string selected)
    {
        if (!string.IsNullOrWhiteSpace(selected)
            && !_availableModels.Contains(selected, StringComparer.OrdinalIgnoreCase))
            _availableModels.Insert(0, selected);
        ModelBox.ItemsSource = null;
        ModelBox.ItemsSource = _availableModels;
        ModelBox.SelectedItem = _availableModels.FirstOrDefault(model => model.Equals(selected, StringComparison.OrdinalIgnoreCase))
            ?? _availableModels.FirstOrDefault();
    }

    private void RefreshConfiguredProviders()
    {
        CaptureCurrentProvider();
        var rows = _profiles.Values
            .Where(profile => profile.IsConfigured || HasProviderKey(profile.ProviderId))
            .OrderBy(profile => profile.DisplayName)
            .Select(profile => new ConfiguredProviderRow(
                profile.DisplayName,
                string.IsNullOrWhiteSpace(profile.Model) ? "尚未选择模型" : profile.Model,
                profile.ProviderId.Equals(_currentProviderId, StringComparison.OrdinalIgnoreCase) ? "当前使用" : "已配置"))
            .ToList();
        ConfiguredProvidersItems.ItemsSource = rows;
        NoConfiguredProvidersText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool HasProviderKey(string providerId) =>
        _pendingKeys.TryGetValue(providerId, out var pending) && !string.IsNullOrWhiteSpace(pending)
        || !string.IsNullOrWhiteSpace(ProviderCredentialStore(providerId).Read());

    private string? ReadProviderKey(string providerId) =>
        _pendingKeys.TryGetValue(providerId, out var pending) && !string.IsNullOrWhiteSpace(pending)
            ? pending
            : ProviderCredentialStore(providerId).Read();

    private static WindowsCredentialStore ProviderCredentialStore(string providerId) =>
        new($"WAFlow/AiProvider/{providerId}");

    private static AiProviderProfile Clone(AiProviderProfile source) => new()
    {
        ProviderId = source.ProviderId,
        DisplayName = source.DisplayName,
        BaseUrl = source.BaseUrl,
        Model = source.Model,
        AvailableModels = source.AvailableModels.ToList(),
        ModelsFetchedAt = source.ModelsFetchedAt,
        IsConfigured = source.IsConfigured
    };

    private sealed record ThemeOption(string Label, string Value);
    private sealed record ConfiguredProviderRow(string DisplayName, string ModelLabel, string StatusLabel);
}
