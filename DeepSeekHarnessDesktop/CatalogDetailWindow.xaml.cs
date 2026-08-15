using System.Diagnostics;
using System.Windows;
using DeepSeekHarnessDesktop.Models;
using Microsoft.Web.WebView2.Core;

namespace DeepSeekHarnessDesktop;

public partial class CatalogDetailWindow : Window
{
    private readonly Uri _uri;
    private readonly string _webViewData;

    public CatalogDetailWindow(Window owner, PluginCatalogItem item, string webViewData)
    {
        InitializeComponent();
        Owner = owner;
        ProjectTitle.Text = item.Name;
        AddressText.Text = item.RepositoryUrl;
        _uri = new Uri(item.RepositoryUrl);
        _webViewData = Path.Combine(webViewData, "project-details");
        Loaded += Window_Loaded;
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
            DetailBrowser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                DetailBrowser.CoreWebView2.Navigate(args.Uri);
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
    private void OpenExternalButton_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(_uri.AbsoluteUri) { UseShellExecute = true });
}
