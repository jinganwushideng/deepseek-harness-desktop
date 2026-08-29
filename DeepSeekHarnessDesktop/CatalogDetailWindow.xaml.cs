using System.Diagnostics;
using System.Windows;
using DeepSeekHarnessDesktop.Models;
using Microsoft.Web.WebView2.Core;

namespace DeepSeekHarnessDesktop;

public partial class CatalogDetailWindow : Window
{
    private readonly Uri _uri;
    private readonly string _webViewData;
    private bool _cleaned;

    public CatalogDetailWindow(Window owner, PluginCatalogItem item, string webViewData)
    {
        InitializeComponent();
        Owner = owner;
        ProjectTitle.Text = item.Name;
        AddressText.Text = item.RepositoryUrl;
        if (!Uri.TryCreate(item.RepositoryUrl, UriKind.Absolute, out var repository) || !IsSafeRepositoryUri(repository))
            throw new ArgumentException("项目主页必须是有效的 HTTPS 地址。", nameof(item));
        _uri = repository;
        _webViewData = Path.Combine(webViewData, "project-details", Guid.NewGuid().ToString("N"));
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_webViewData);
            var environment = await CoreWebView2Environment.CreateAsync(null, _webViewData);
            await DetailBrowser.EnsureCoreWebView2Async(environment);
            DetailBrowser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            DetailBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            DetailBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            DetailBrowser.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            DetailBrowser.CoreWebView2.Settings.IsWebMessageEnabled = false;
            DetailBrowser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            DetailBrowser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            DetailBrowser.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) || IsSameOrigin(_uri, target)) return;
                args.Cancel = true;
                OpenExternalSafe(target);
            };
            DetailBrowser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target)) return;
                if (IsSameOrigin(_uri, target)) DetailBrowser.CoreWebView2.Navigate(target.AbsoluteUri);
                else OpenExternalSafe(target);
            };
            DetailBrowser.CoreWebView2.PermissionRequested += (_, args) =>
            {
                args.State = CoreWebView2PermissionState.Deny;
                args.Handled = true;
            };
            DetailBrowser.CoreWebView2.DownloadStarting += (_, args) =>
            {
                args.Cancel = true;
                if (Uri.TryCreate(args.DownloadOperation.Uri, UriKind.Absolute, out var target)) OpenExternalSafe(target);
            };
            DetailBrowser.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    DetailBrowser.Visibility = Visibility.Visible;
                }
                else LoadingText.Text = $"项目主页加载失败：{args.WebErrorStatus}";
            };
            DetailBrowser.Source = _uri;
        }
        catch (Exception ex)
        {
            LoadingText.Text = "项目主页加载失败：" + Services.LogService.Redact(ex.Message);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => DetailBrowser.CoreWebView2?.Reload();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void OpenExternalButton_Click(object sender, RoutedEventArgs e) => OpenExternalSafe(_uri);

    internal static bool IsSafeRepositoryUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(uri.Host);

    internal static bool IsSameOrigin(Uri origin, Uri target) =>
        IsSafeRepositoryUri(target) &&
        origin.Scheme.Equals(target.Scheme, StringComparison.OrdinalIgnoreCase) &&
        origin.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase) &&
        origin.Port == target.Port;

    private void OpenExternalSafe(Uri target)
    {
        if (target.Scheme is not ("http" or "https"))
        {
            ShellDialog.Show(this, "已阻止不受支持的外部协议。", "项目主页", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { ShellDialog.Show(this, "无法打开系统浏览器：" + Services.LogService.Redact(ex.Message), "项目主页"); }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_cleaned) return;
        _cleaned = true;
        try { DetailBrowser.Dispose(); } catch { }
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                try { if (Directory.Exists(_webViewData)) Directory.Delete(_webViewData, true); return; }
                catch { await Task.Delay(250 * (attempt + 1)); }
            }
        });
    }
}
