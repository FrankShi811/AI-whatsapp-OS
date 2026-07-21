using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WAFlow.Desktop.Controls;

public partial class GuideOverlay : UserControl
{
    private int _stepIndex;

    internal GuideDefinition? CurrentDefinition { get; private set; }
    internal int CurrentStepIndex => _stepIndex;
    internal bool IsOpen => Visibility == Visibility.Visible;
    internal bool AllowGlobalLink { get; set; } = true;

    internal event EventHandler? CloseRequested;
    internal event EventHandler? FinishedRequested;
    internal event EventHandler? SettingsRequested;
    internal event EventHandler? GlobalRequested;

    public GuideOverlay() => InitializeComponent();

    internal void ShowGuide(GuideDefinition definition, int step = 0)
    {
        CurrentDefinition = definition;
        _stepIndex = Math.Clamp(step, 0, definition.Steps.Count - 1);
        Visibility = Visibility.Visible;
        Render();
    }

    internal void HideGuide() => Visibility = Visibility.Collapsed;

    private void Render()
    {
        if (CurrentDefinition is not { } definition) return;
        var step = definition.Steps[_stepIndex];
        GuideProductArea.Text = definition.ProductArea;
        GuideHeaderTitle.Text = definition.Title;
        GuideStepCounter.Text = $"第 {_stepIndex + 1} / {definition.Steps.Count} 步";
        GuideTitle.Text = step.Title;
        GuideSummary.Text = step.Summary;
        GuideFeature.Text = step.Feature;
        GuideTip.Text = step.Tip;
        GuideFooter.Text = definition.Footer;
        GuideSettingsButton.Visibility = step.ShowSettings ? Visibility.Visible : Visibility.Collapsed;
        GlobalGuideButton.Visibility = AllowGlobalLink && !definition.IsGlobal ? Visibility.Visible : Visibility.Collapsed;
        GuideBackButton.IsEnabled = _stepIndex > 0;
        GuideNextButton.Content = _stepIndex == definition.Steps.Count - 1
            ? definition.IsGlobal ? "完成新手入门" : "完成本页指南"
            : "下一步";
        GuideProgress.Value = 100d * (_stepIndex + 1) / definition.Steps.Count;
        GuideActionList.ItemsSource = step.Actions.Select((text, index) => new GuideActionItem((index + 1).ToString("00"), text)).ToList();

        var activeBackground = TryFindResource("AiSurface") as Brush ?? Brushes.White;
        var idleBackground = Brushes.Transparent;
        var accent = TryFindResource("AiAccent") as Brush ?? Brushes.MediumSlateBlue;
        var primary = TryFindResource("Primary") as Brush ?? Brushes.SeaGreen;
        var ink = TryFindResource("Ink") as Brush ?? Brushes.Black;
        var muted = TryFindResource("Muted") as Brush ?? Brushes.Gray;
        var soft = TryFindResource("PrimarySoft") as Brush ?? Brushes.Honeydew;
        GuideStepList.ItemsSource = definition.Steps.Select((item, index) => new GuideNavigationItem(
            (index + 1).ToString("00"),
            item.Title,
            index == _stepIndex ? activeBackground : idleBackground,
            index == _stepIndex ? soft : idleBackground,
            index == _stepIndex ? primary : muted,
            index == _stepIndex ? ink : muted,
            index == _stepIndex ? FontWeights.SemiBold : FontWeights.Normal)).ToList();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex <= 0) return;
        _stepIndex--;
        Render();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDefinition is null) return;
        if (_stepIndex < CurrentDefinition.Steps.Count - 1)
        {
            _stepIndex++;
            Render();
            return;
        }
        FinishedRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void Global_Click(object sender, RoutedEventArgs e) => GlobalRequested?.Invoke(this, EventArgs.Empty);

    private sealed record GuideActionItem(string Number, string Text);
    private sealed record GuideNavigationItem(string Number, string Title, Brush Background, Brush NumberBackground, Brush NumberForeground, Brush Foreground, FontWeight FontWeight);
}
