using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WAFlow.Desktop;

/// <summary>
/// Small, interruption-safe motion primitives shared by interactive shell controls.
/// The behavior only changes rendering, so layout and hit targets remain stable.
/// </summary>
public static class MotionAssist
{
    private static readonly ConditionalWeakTable<FrameworkElement, MotionState> States = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(MotionAssist),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty HoverScaleProperty = DependencyProperty.RegisterAttached(
        "HoverScale",
        typeof(double),
        typeof(MotionAssist),
        new PropertyMetadata(1.012d));

    public static readonly DependencyProperty PressedScaleProperty = DependencyProperty.RegisterAttached(
        "PressedScale",
        typeof(double),
        typeof(MotionAssist),
        new PropertyMetadata(0.975d));

    public static readonly DependencyProperty HoverOffsetXProperty = DependencyProperty.RegisterAttached(
        "HoverOffsetX",
        typeof(double),
        typeof(MotionAssist),
        new PropertyMetadata(0d));

    public static readonly DependencyProperty HoverOffsetYProperty = DependencyProperty.RegisterAttached(
        "HoverOffsetY",
        typeof(double),
        typeof(MotionAssist),
        new PropertyMetadata(-1d));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached(
        "IsSelected",
        typeof(bool),
        typeof(MotionAssist),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetHoverScale(DependencyObject element, double value) => element.SetValue(HoverScaleProperty, value);
    public static double GetHoverScale(DependencyObject element) => (double)element.GetValue(HoverScaleProperty);
    public static void SetPressedScale(DependencyObject element, double value) => element.SetValue(PressedScaleProperty, value);
    public static double GetPressedScale(DependencyObject element) => (double)element.GetValue(PressedScaleProperty);
    public static void SetHoverOffsetX(DependencyObject element, double value) => element.SetValue(HoverOffsetXProperty, value);
    public static double GetHoverOffsetX(DependencyObject element) => (double)element.GetValue(HoverOffsetXProperty);
    public static void SetHoverOffsetY(DependencyObject element, double value) => element.SetValue(HoverOffsetYProperty, value);
    public static double GetHoverOffsetY(DependencyObject element) => (double)element.GetValue(HoverOffsetYProperty);
    public static void SetIsSelected(DependencyObject element, bool value) => element.SetValue(IsSelectedProperty, value);
    public static bool GetIsSelected(DependencyObject element) => (bool)element.GetValue(IsSelectedProperty);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not FrameworkElement element) return;
        if ((bool)args.NewValue)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            _ = GetOrCreateState(element);
            element.MouseEnter += Element_MouseEnter;
            element.MouseLeave += Element_MouseLeave;
            element.PreviewMouseLeftButtonDown += Element_PreviewMouseLeftButtonDown;
            element.PreviewMouseLeftButtonUp += Element_PreviewMouseLeftButtonUp;
            element.LostMouseCapture += Element_LostMouseCapture;
            element.PreviewKeyDown += Element_PreviewKeyDown;
            element.PreviewKeyUp += Element_PreviewKeyUp;
            element.IsEnabledChanged += Element_IsEnabledChanged;
            element.Unloaded += Element_Unloaded;
            return;
        }

        element.MouseEnter -= Element_MouseEnter;
        element.MouseLeave -= Element_MouseLeave;
        element.PreviewMouseLeftButtonDown -= Element_PreviewMouseLeftButtonDown;
        element.PreviewMouseLeftButtonUp -= Element_PreviewMouseLeftButtonUp;
        element.LostMouseCapture -= Element_LostMouseCapture;
        element.PreviewKeyDown -= Element_PreviewKeyDown;
        element.PreviewKeyUp -= Element_PreviewKeyUp;
        element.IsEnabledChanged -= Element_IsEnabledChanged;
        element.Unloaded -= Element_Unloaded;
        if (States.TryGetValue(element, out var state))
            SetImmediately(state, 1, 0, 0);
    }

    private static MotionState GetOrCreateState(FrameworkElement element) =>
        States.GetValue(element, static owner =>
        {
            var scale = new ScaleTransform(1, 1);
            var translate = new TranslateTransform();
            var group = new TransformGroup();
            if (owner.RenderTransform is { } existing &&
                existing != Transform.Identity &&
                !existing.Value.IsIdentity)
            {
                group.Children.Add(existing.CloneCurrentValue());
            }
            group.Children.Add(scale);
            group.Children.Add(translate);
            owner.RenderTransform = group;
            return new MotionState(scale, translate);
        });

    private static void Element_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element && element.IsEnabled)
            AnimateState(element, pressed: false, pointerInside: true);
    }

    private static void Element_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element)
            AnimateState(element, pressed: false, pointerInside: false);
    }

    private static void Element_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.IsEnabled)
            AnimateState(element, pressed: true, pointerInside: true);
    }

    private static void Element_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element)
            AnimateState(element, pressed: false, pointerInside: element.IsMouseOver);
    }

    private static void Element_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element)
            AnimateState(element, pressed: false, pointerInside: element.IsMouseOver);
    }

    private static void Element_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is FrameworkElement element &&
            element.IsEnabled &&
            e.Key is Key.Space or Key.Enter &&
            !e.IsRepeat)
        {
            AnimateState(element, pressed: true, pointerInside: element.IsMouseOver);
        }
    }

    private static void Element_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is FrameworkElement element && e.Key is Key.Space or Key.Enter)
            AnimateState(element, pressed: false, pointerInside: element.IsMouseOver);
    }

    private static void Element_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element && !(bool)e.NewValue)
            AnimateState(element, pressed: false, pointerInside: false);
    }

    private static void Element_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && States.TryGetValue(element, out var state))
            SetImmediately(state, 1, 0, 0);
    }

    private static void AnimateState(FrameworkElement element, bool pressed, bool pointerInside)
    {
        var state = GetOrCreateState(element);
        var scale = pressed ? GetPressedScale(element) : pointerInside ? GetHoverScale(element) : 1;
        var offsetX = pressed ? 0 : pointerInside ? GetHoverOffsetX(element) : 0;
        var offsetY = pressed ? 0.75 : pointerInside ? GetHoverOffsetY(element) : 0;
        var duration = TimeSpan.FromMilliseconds(pressed ? 86 : pointerInside ? 170 : 215);

        if (!SystemParameters.ClientAreaAnimation)
        {
            SetImmediately(state, scale, offsetX, offsetY);
            return;
        }

        var version = ++state.Version;
        var easing = new SineEase { EasingMode = EasingMode.EaseOut };
        AnimateAndCommit(state.Scale, ScaleTransform.ScaleXProperty, scale, duration, easing, state, version);
        AnimateAndCommit(state.Scale, ScaleTransform.ScaleYProperty, scale, duration, easing, state, version);
        AnimateAndCommit(state.Translate, TranslateTransform.XProperty, offsetX, duration, easing, state, version);
        AnimateAndCommit(state.Translate, TranslateTransform.YProperty, offsetY, duration, easing, state, version);
    }

    private static void AnimateAndCommit(
        Animatable target,
        DependencyProperty property,
        double destination,
        TimeSpan duration,
        IEasingFunction easing,
        MotionState state,
        long version)
    {
        var current = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, current);
        var animation = new DoubleAnimation(current, destination, new Duration(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            if (version != state.Version) return;
            target.SetValue(property, destination);
            target.BeginAnimation(property, null);
        };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void SetImmediately(MotionState state, double scale, double offsetX, double offsetY)
    {
        state.Version++;
        state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        state.Translate.BeginAnimation(TranslateTransform.XProperty, null);
        state.Translate.BeginAnimation(TranslateTransform.YProperty, null);
        state.Scale.ScaleX = scale;
        state.Scale.ScaleY = scale;
        state.Translate.X = offsetX;
        state.Translate.Y = offsetY;
    }

    private sealed record MotionState(ScaleTransform Scale, TranslateTransform Translate)
    {
        public long Version { get; set; }
    }
}
