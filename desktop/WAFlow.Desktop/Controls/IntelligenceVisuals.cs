using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace WAFlow.Desktop.Controls;

public sealed class GradeDonut : FrameworkElement
{
    private readonly int[] _values = new int[4];
    private static readonly Color[] SegmentColors =
    [
        Color.FromRgb(20, 166, 117), Color.FromRgb(77, 181, 148),
        Color.FromRgb(232, 172, 58), Color.FromRgb(210, 100, 103)
    ];

    public void SetValues(int a, int b, int c, int d)
    {
        _values[0] = Math.Max(0, a); _values[1] = Math.Max(0, b); _values[2] = Math.Max(0, c); _values[3] = Math.Max(0, d);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var size = Math.Max(24, Math.Min(ActualWidth, ActualHeight));
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(8, size / 2 - 13);
        dc.DrawEllipse(null, new Pen(ResourceBrush("CanvasDeep", Colors.LightGray), 11), center, radius, radius);
        var total = _values.Sum();
        if (total > 0)
        {
            var angle = -90d;
            for (var index = 0; index < _values.Length; index++)
            {
                if (_values[index] == 0) continue;
                var sweep = 360d * _values[index] / total;
                DrawArc(dc, center, radius, angle + 1.1, Math.Max(0.4, sweep - 2.2), new SolidColorBrush(SegmentColors[index]), 11);
                angle += sweep;
            }
        }
        DrawCenteredText(dc, total.ToString("N0"), center.Y - 15, 24, FontWeights.Bold, ResourceBrush("Ink", Colors.Black));
        DrawCenteredText(dc, "全部商机", center.Y + 15, 9.5, FontWeights.SemiBold, ResourceBrush("Muted", Colors.Gray));
    }

    private void DrawCenteredText(DrawingContext dc, string text, double y, double size, FontWeight weight, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point((ActualWidth - formatted.Width) / 2, y - formatted.Height / 2));
    }

    internal static void DrawArc(DrawingContext dc, Point center, double radius, double startDegrees, double sweepDegrees, Brush brush, double thickness)
    {
        if (sweepDegrees <= 0) return;
        if (sweepDegrees >= 359.9) { dc.DrawEllipse(null, new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, center, radius, radius); return; }
        var start = PointAt(center, radius, startDegrees);
        var end = PointAt(center, radius, startDegrees + sweepDegrees);
        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        dc.DrawGeometry(null, new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }

    private static Point PointAt(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180d;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    internal static Brush ResourceBrush(string key, Color fallback) => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}

public sealed class ScoreRing : FrameworkElement
{
    private int _score;
    private string _grade = "D";
    private double _confidence;

    public void SetScore(int score, string grade, double confidence)
    {
        _score = Math.Clamp(score, 0, 100); _grade = string.IsNullOrWhiteSpace(grade) ? "D" : grade; _confidence = Math.Clamp(confidence, 0, 1);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var size = Math.Max(32, Math.Min(ActualWidth, ActualHeight));
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(10, size / 2 - 11);
        dc.DrawEllipse(null, new Pen(GradeDonut.ResourceBrush("CanvasDeep", Colors.LightGray), 8), center, radius, radius);
        var accent = _grade switch
        {
            "A" => Color.FromRgb(14, 164, 112), "B" => Color.FromRgb(72, 177, 145),
            "C" => Color.FromRgb(229, 168, 50), _ => Color.FromRgb(204, 93, 98)
        };
        GradeDonut.DrawArc(dc, center, radius, -90, Math.Max(1.5, 359.8 * _score / 100d), new SolidColorBrush(accent), 8);
        DrawCentered(dc, _score.ToString(), center.Y - 18, 28, FontWeights.Bold, GradeDonut.ResourceBrush("Ink", Colors.Black));
        DrawCentered(dc, $"{_grade} 级 · 置信 {_confidence:P0}", center.Y + 17, 9.5, FontWeights.SemiBold, GradeDonut.ResourceBrush("Muted", Colors.Gray));
    }

    private void DrawCentered(DrawingContext dc, string text, double y, double size, FontWeight weight, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point((ActualWidth - formatted.Width) / 2, y - formatted.Height / 2));
    }
}

public sealed class LeadRadar : FrameworkElement
{
    private static readonly string[] Labels = ["付费意愿", "供应稳定", "电商基础", "私域流量", "销售能力", "素材准备"];
    private readonly double[] _values = new double[6];

    public void SetValues(IEnumerable<double> values)
    {
        var items = values.Take(6).ToArray();
        for (var i = 0; i < _values.Length; i++) _values[i] = i < items.Length ? Math.Clamp(items[i], 0, 1) : 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var center = new Point(ActualWidth / 2, ActualHeight / 2 + 2);
        var radius = Math.Max(18, Math.Min(ActualWidth * 0.34, ActualHeight * 0.37));
        var line = GradeDonut.ResourceBrush("LineStrong", Colors.LightGray);
        var text = GradeDonut.ResourceBrush("Muted", Colors.Gray);
        for (var ring = 1; ring <= 4; ring++) DrawPolygon(dc, center, radius * ring / 4d, Enumerable.Repeat(1d, 6).ToArray(), null, new Pen(line, ring == 4 ? 1.1 : 0.65));
        for (var index = 0; index < 6; index++)
        {
            dc.DrawLine(new Pen(line, 0.7), center, RadarPoint(center, radius, index, 1));
            var labelPoint = RadarPoint(center, radius + 19, index, 1);
            var formatted = new FormattedText(Labels[index], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Variable Text, Microsoft YaHei UI"), 8.5, text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(formatted, new Point(labelPoint.X - formatted.Width / 2, labelPoint.Y - formatted.Height / 2));
        }
        var accent = GradeDonut.ResourceBrush("AiAccent", Color.FromRgb(108, 99, 255));
        var fill = accent.Clone(); fill.Opacity = 0.18;
        DrawPolygon(dc, center, radius, _values, fill, new Pen(accent, 2));
        for (var index = 0; index < 6; index++) dc.DrawEllipse(accent, null, RadarPoint(center, radius, index, _values[index]), 3, 3);
    }

    private static void DrawPolygon(DrawingContext dc, Point center, double radius, IReadOnlyList<double> values, Brush? fill, Pen? pen)
    {
        var figure = new PathFigure { StartPoint = RadarPoint(center, radius, 0, values[0]), IsClosed = true, IsFilled = fill is not null };
        for (var index = 1; index < 6; index++) figure.Segments.Add(new LineSegment(RadarPoint(center, radius, index, values[index]), true));
        dc.DrawGeometry(fill, pen, new PathGeometry([figure]));
    }

    private static Point RadarPoint(Point center, double radius, int index, double value)
    {
        var angle = -Math.PI / 2 + index * Math.PI / 3;
        return new Point(center.X + Math.Cos(angle) * radius * value, center.Y + Math.Sin(angle) * radius * value);
    }
}
