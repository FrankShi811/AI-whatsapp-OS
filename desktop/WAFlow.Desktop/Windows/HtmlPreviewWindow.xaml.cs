using System.Diagnostics;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace WAFlow.Desktop.Windows;

/// <summary>Renders an email's original HTML body (tables, buttons, links) with WebView2.
/// External http(s) links open in the system browser instead of navigating the preview.</summary>
public partial class HtmlPreviewWindow : Window
{
    private readonly string _html;

    public HtmlPreviewWindow(string subject, string html)
    {
        InitializeComponent();
        _html = html;
        Title = string.IsNullOrWhiteSpace(subject) ? "邮件预览" : $"邮件预览 · {subject}";
        PreviewWeb.CoreWebView2InitializationCompleted += OnCoreWebView2Initialized;
        Loaded += async (_, _) =>
        {
            try
            {
                await PreviewWeb.EnsureCoreWebView2Async();
                PreviewWeb.NavigateToString(_html);
            }
            catch (Exception error)
            {
                MessageBox.Show($"无法打开邮件原格式预览：{error.Message}", "邮件预览", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
    }

    private void OnCoreWebView2Initialized(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        PreviewWeb.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        PreviewWeb.CoreWebView2.Settings.IsZoomControlEnabled = true;
        PreviewWeb.CoreWebView2.NavigationStarting += OnNavigationStarting;
        PreviewWeb.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            OpenExternal(e.Uri);
            e.Cancel = true;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        OpenExternal(e.Uri);
        e.Handled = true;
    }

    private static void OpenExternal(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // Opening the system browser is best-effort.
        }
    }
}
