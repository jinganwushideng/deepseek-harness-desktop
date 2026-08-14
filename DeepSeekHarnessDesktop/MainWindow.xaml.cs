using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using DeepSeekHarnessDesktop.Models;
using DeepSeekHarnessDesktop.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DeepSeekHarnessDesktop;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths = new();
    private readonly SettingsService _settingsService;
    private readonly LogService _log;
    private readonly RuntimeService _runtime;
    private readonly HarnessProcessService _server;
    private readonly NodeHelperService _helper;
    private readonly PluginService _plugins;
    private readonly SkillService _skills;
    private readonly BackupService _backup;
    private readonly DiagnosticService _diagnostics;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly DispatcherTimer _resourceTimer;
    private LauncherSettings _settings;
    private Forms.NotifyIcon? _tray;
    private string? _latestVersion;
    private bool _browserInitialized;
    private bool _showingSettings;
    private bool _explicitExit;
    private bool _externalBrowserOpened;
    private bool _pluginsLoaded;
    private bool _skillsLoaded;
    private bool _themeUiReady;
    private bool? _webIsLight;
    private bool? _isLightTheme;
    private HwndSource? _windowSource;
    private CancellationTokenSource? _dataUsageCts;
    private DateTimeOffset _lastResponseNotification = DateTimeOffset.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        _paths.EnsureDirectories();
        _settingsService = new SettingsService(_paths);
        _settings = _settingsService.Load();
        _log = new LogService(_paths);
        _runtime = new RuntimeService(_paths, _log);
        _server = new HarnessProcessService(_paths, _log);
        _helper = new NodeHelperService(_paths, _log);
        _skills = new SkillService();
        _plugins = new PluginService(_paths, _server, _helper, _log, _skills);
        _backup = new BackupService(_paths, _log);
        _diagnostics = new DiagnosticService(_paths, _runtime, _server);

        _server.StateChanged += Server_StateChanged;
        _server.RestartRequested += AutoRestartAsync;
        _log.LineAdded += line => Dispatcher.BeginInvoke(() => AppendLog(line));
        _resourceTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => UpdateResourceText(), Dispatcher);

        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        SettingsView.SizeChanged += (_, _) => UpdateResponsiveSettingsLayout();
        StateChanged += MainWindow_StateChanged;
        SystemEvents.SessionEnding += SystemEvents_SessionEnding;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        ApplyResolvedShellTheme();
        ConfigureTray();
        ApplySettingsToControls();
        _themeUiReady = true;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        // Ask DWM to clip the real top-level HWND. This keeps WebView2 on its
        // native high-performance HWND while giving the shell Windows 11 corners.
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        UpdateWindowVisualState();
        ApplyDwmTheme();
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0024) // WM_GETMINMAXINFO
        {
            ConstrainMaximizedWindowToWorkArea(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ConstrainMaximizedWindowToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, 0x00000002); // MONITOR_DEFAULTTONEAREST
        if (monitor == IntPtr.Zero) return;

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return;

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = monitorInfo.WorkArea;
        var bounds = monitorInfo.MonitorArea;
        minMax.MaxPosition.X = work.Left - bounds.Left;
        minMax.MaxPosition.Y = work.Top - bounds.Top;
        minMax.MaxSize.X = work.Right - work.Left;
        minMax.MaxSize.Y = work.Bottom - work.Top;
        minMax.MaxTrackSize = minMax.MaxSize;
        var dpi = Math.Max(96u, GetDpiForWindow(hwnd));
        var scale = dpi / 96d;
        minMax.MinTrackSize.X = Math.Min(minMax.MaxTrackSize.X, (int)Math.Ceiling(MinWidth * scale));
        minMax.MinTrackSize.Y = Math.Min(minMax.MaxTrackSize.Y, (int)Math.Ceiling(MinHeight * scale));
        Marshal.StructureToPtr(minMax, lParam, false);
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) Activate();
        UpdateWindowVisualState();
    }

    private void UpdateWindowVisualState()
    {
        var maximized = WindowState == WindowState.Maximized;
        WindowFrame.CornerRadius = new CornerRadius(0);
        WindowFrame.BorderThickness = new Thickness(0);
        TitleBarBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(0, 0, 12, 12);
        TitleBarBorder.BorderThickness = maximized ? new Thickness(0, 0, 0, 1) : new Thickness(1, 0, 1, 1);
        ContentFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(12, 12, 0, 0);
        ContentFrame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1, 1, 1, 0);
        ContentCornerMasks.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        SyncBrowserCornerStyle(!maximized);

        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome is not null) chrome.CornerRadius = new CornerRadius(0);

        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        MaximizeButton.ToolTip = maximized ? "还原窗口" : "最大化";
        AutomationProperties.SetName(MaximizeButton, maximized ? "还原窗口" : "最大化窗口");

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            var cornerPreference = maximized ? 1 : 2; // DWMWCP_DONOTROUND / DWMWCP_ROUND
            _ = DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_settings.Initialized)
        {
            SetupWorkspaceBox.Text = _settings.Workspace;
            SetupDshHomeBox.Text = _settings.DshHome;
            SetupPortBox.Text = _settings.Port.ToString();
            SetupOverlay.Visibility = Visibility.Visible;
            LoadingText.Text = "请完成首次初始化";
            return;
        }
        await InitializeDesktopAsync();
    }

    private async Task InitializeDesktopAsync()
    {
        await RunGuardedAsync(async () =>
        {
            ShowLoading("正在准备离线运行环境…");
            var progress = new Progress<string>(ShowLoading);
            await _runtime.EnsureSeedAsync(progress);
            if (!_runtime.IsInstalled(_settings.CurrentRuntimeVersion))
            {
                _settings.CurrentRuntimeVersion = RuntimeInfo.SeedVersion;
                _settingsService.Save(_settings);
            }
            await Task.Run(() => _plugins.SyncStoredSkills(_settings));
            await InitializeBrowserAsync();
            RefreshRuntimeView();
            if (_settings.AutoStartServer) await StartServerCoreAsync();
            else ShowSettings(0);
            if (_settings.CheckUpdates) _ = CheckUpdatesSilentAsync();
        }, showFault: true);
    }

    private async Task InitializeBrowserAsync()
    {
        if (_browserInitialized) return;
        ShowLoading("正在初始化内嵌浏览器…");
        Directory.CreateDirectory(_paths.WebViewData);
        var environment = await CoreWebView2Environment.CreateAsync(null, _paths.WebViewData);
        UpdateBrowserBackground();
        await Browser.EnsureCoreWebView2Async(environment);
        Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
        Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
        Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        Browser.CoreWebView2.NavigationStarting += Browser_NavigationStarting;
        Browser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
        Browser.CoreWebView2.WebMessageReceived += Browser_WebMessageReceived;
        Browser.CoreWebView2.NavigationCompleted += (_, _) => SyncBrowserCornerStyle(WindowState != WindowState.Maximized);
        Browser.CoreWebView2.ProcessFailed += (_, e) => _log.Warn("webview", $"render process failed: {e.ProcessFailedKind}");
        await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ResponseCompletionMonitor.Script);
        await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(WebThemeMonitor.Script);
        await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(WebCornerStyle.Script);
        _browserInitialized = true;
    }

    private void SyncBrowserCornerStyle(bool rounded)
    {
        if (!_browserInitialized || Browser.CoreWebView2 is null) return;
        _ = Browser.CoreWebView2.ExecuteScriptAsync($"window.__dshDesktopSetRoundedCorners?.({(rounded ? "true" : "false")});");
    }

    private void UpdateBrowserBackground()
    {
        if (System.Windows.Application.Current.TryFindResource("ChromeBg") is not SolidColorBrush brush) return;
        var color = brush.Color;
        Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private async Task StartServerCoreAsync()
    {
        if (_server.State is ServerState.Running or ServerState.Starting) return;
        ShowLoading("正在启动 DeepSeek Harness…");
        await _server.StartAsync(_settings);
        await NavigateHarnessAsync();
    }

    private async Task NavigateHarnessAsync()
    {
        await InitializeBrowserAsync();
        var uri = new Uri($"http://127.0.0.1:{_settings.Port}/");
        Browser.Source = uri;
        LoadingView.Visibility = Visibility.Collapsed;
        FaultView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Visible;
        _showingSettings = false;
        TitleStatusText.Text = uri.ToString();
        if (_settings.OpenExternalBrowser && !_externalBrowserOpened)
        {
            OpenExternal(uri.ToString());
            _externalBrowserOpened = true;
        }
    }

    private async Task AutoRestartAsync()
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(900);
            await RunGuardedAsync(StartServerCoreAsync, showFault: true, waitForTurn: true);
        }).Task.Unwrap();
    }

    private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        var local = uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) && uri.Port == _settings.Port;
        if (local || uri.Scheme is "about" or "data") return;
        e.Cancel = true;
        OpenExternal(uri.ToString());
    }

    private void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) && uri.Port == _settings.Port)
            Browser.Source = uri;
        else OpenExternal(e.Uri);
    }

    private void Browser_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) ||
            !source.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) || source.Port != _settings.Port) return;

        string? message;
        try { message = JsonSerializer.Deserialize<string>(e.WebMessageAsJson); }
        catch { return; }
        if (WebThemeMonitor.TryReadMessage(message, out var isLight))
        {
            Dispatcher.BeginInvoke(() =>
            {
                _webIsLight = isLight;
                _log.Info("theme", $"Harness resolved theme: {(isLight ? "light" : "dark")}");
                if (ShellThemeService.NormalizeMode(_settings.ShellThemeMode) == ShellThemeService.FollowWeb)
                    ApplyResolvedShellTheme();
                else UpdateThemeModeStatus();
            });
            return;
        }
        if (_settings.NotifyOnResponseComplete && ResponseCompletionMonitor.IsCompletionMessage(message))
            Dispatcher.BeginInvoke(() => ShowResponseCompletionNotification());
    }

    private void ShowResponseCompletionNotification(bool force = false)
    {
        var viewingHarness = IsActive && IsVisible && WindowState != WindowState.Minimized &&
            Browser.Visibility == Visibility.Visible && !_showingSettings;
        if (_tray is null || (!force && (viewingHarness || !_settings.NotifyOnResponseComplete))) return;

        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastResponseNotification < TimeSpan.FromSeconds(3)) return;
        _lastResponseNotification = now;
        _tray.ShowBalloonTip(5000, "Harness 回复完成", "DeepSeek Harness 已完成回复，点击即可查看。", Forms.ToolTipIcon.Info);
        _log.Info("notification", force ? "test notification shown" : "response completion notification shown");
    }

    private void TestResponseNotificationButton_Click(object sender, RoutedEventArgs e) =>
        ShowResponseCompletionNotification(force: true);

    private void Server_StateChanged(ServerState state, string? message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var (label, color) = state switch
            {
                ServerState.Running => ("运行中", "#36D399"),
                ServerState.Starting => ("启动中", "#F7C948"),
                ServerState.Stopping => ("停止中", "#F7C948"),
                ServerState.Maintenance => ("维护中", "#8B7CFF"),
                ServerState.Faulted => ("故障", "#FF6577"),
                ServerState.NotInstalled => ("未安装", "#8791A8"),
                _ => ("已停止", "#8791A8")
            };
            ServerStateText.Text = label;
            ServerDot.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
            StartServerButton.IsEnabled = state is ServerState.Stopped or ServerState.Faulted or ServerState.NotInstalled;
            StopServerButton.IsEnabled = state is ServerState.Starting or ServerState.Running;
            RestartServerButton.IsEnabled = state == ServerState.Running;
            RefreshButton.IsEnabled = state == ServerState.Running || _showingSettings;
            if (!string.IsNullOrWhiteSpace(message)) TitleStatusText.Text = message;
            _tray!.Text = $"DeepSeek Harness Desktop - {label}";
            UpdateResourceText();
            if (state == ServerState.Faulted) ShowFault(message ?? "Harness 服务异常退出。 ");
        });
    }

    private void ApplySettingsToControls()
    {
        var restoreThemeEvents = _themeUiReady;
        _themeUiReady = false;
        WorkspaceBox.Text = _settings.Workspace;
        DshHomeBox.Text = _settings.DshHome;
        PortBox.Text = _settings.Port.ToString();
        CloseToTrayCheck.IsChecked = _settings.CloseToTray;
        ResponseNotificationCheck.IsChecked = _settings.NotifyOnResponseComplete;
        LaunchAtLoginCheck.IsChecked = _settings.LaunchAtLogin;
        AutoStartCheck.IsChecked = _settings.AutoStartServer;
        ExternalBrowserCheck.IsChecked = _settings.OpenExternalBrowser;
        TelemetryCheck.IsChecked = _settings.ForceTelemetryOff;
        switch (ShellThemeService.NormalizeMode(_settings.ShellThemeMode))
        {
            case ShellThemeService.FollowSystem: FollowSystemThemeRadio.IsChecked = true; break;
            case ShellThemeService.Light: LightThemeRadio.IsChecked = true; break;
            case ShellThemeService.Dark: DarkThemeRadio.IsChecked = true; break;
            default: FollowWebThemeRadio.IsChecked = true; break;
        }
        _themeUiReady = restoreThemeEvents;
        UpdateThemeModeStatus();
        CurrentVersionText.Text = _settings.CurrentRuntimeVersion;
        UserPluginDirectoryText.Text = "用户插件目录：" + _plugins.UserPluginDirectory(_settings);
        OfficialPluginDirectoryText.Text = "官方组件目录：" + _plugins.OfficialPluginDirectory(_settings);
    }

    private void ThemeModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!_themeUiReady || sender is not FrameworkElement { Tag: string mode }) return;
        var previous = _settings.ShellThemeMode;
        try
        {
            _settings.ShellThemeMode = ShellThemeService.NormalizeMode(mode);
            _settingsService.Save(_settings);
            ApplyResolvedShellTheme();
            _log.Info("theme", $"shell theme mode changed: {_settings.ShellThemeMode}");
        }
        catch (Exception ex)
        {
            _settings.ShellThemeMode = previous;
            ApplySettingsToControls();
            _log.Warn("theme", $"theme preference save failed: {ex.Message}");
            ShellDialog.Show(this, "主题设置保存失败：" + ex.Message, "DeepSeek Harness Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyResolvedShellTheme()
    {
        var light = ShellThemeService.ResolveLight(_settings.ShellThemeMode, _webIsLight, ShellThemeService.IsSystemLight());
        if (_isLightTheme != light)
        {
            ShellThemeService.ApplyPalette(light);
            _isLightTheme = light;
            ApplyDwmTheme();
            UpdateBrowserBackground();
        }
        UpdateThemeModeStatus();
    }

    private void ApplyDwmTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = _isLightTheme == true ? 0 : 1;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    private void UpdateThemeModeStatus()
    {
        if (ThemeModeStatusText is null) return;
        var actual = _isLightTheme == true ? "浅色" : "深色";
        ThemeModeStatusText.Text = ShellThemeService.NormalizeMode(_settings.ShellThemeMode) switch
        {
            ShellThemeService.FollowWeb when _webIsLight is null => $"当前：{actual}（等待 Harness 主题信号）",
            ShellThemeService.FollowWeb => $"当前：{actual} · 已跟随 Harness 网页",
            ShellThemeService.FollowSystem => $"当前：{actual} · 已跟随 Windows 应用模式",
            ShellThemeService.Light => "当前：固定浅色",
            _ => "当前：固定深色"
        };
    }

    private void ShowLoading(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LoadingText.Text = message;
            LoadingView.Visibility = Visibility.Visible;
            FaultView.Visibility = Visibility.Collapsed;
            if (!_showingSettings) Browser.Visibility = Visibility.Collapsed;
        });
    }

    private void ShowFault(string message)
    {
        FaultMessage.Text = message;
        FaultView.Visibility = Visibility.Visible;
        LoadingView.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        _showingSettings = false;
    }

    private async Task RunGuardedAsync(Func<Task> action, bool showFault = false, bool waitForTurn = false)
    {
        if (waitForTurn) await _operationLock.WaitAsync();
        else if (!await _operationLock.WaitAsync(0)) return;
        SettingsBusyProgress.Visibility = Visibility.Visible;
        SettingsTabs.IsEnabled = false;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try { await action(); }
        catch (Exception ex)
        {
            _log.Error("desktop", ex.ToString());
            if (showFault) ShowFault(LogService.Redact(ex.Message));
            else ShellDialog.Show(this, LogService.Redact(ex.Message), "DeepSeek Harness Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SettingsBusyProgress.Visibility = Visibility.Collapsed; SettingsTabs.IsEnabled = true; Mouse.OverrideCursor = null; _operationLock.Release(); }
    }

    private void ShowSettings(int tab = -1)
    {
        _showingSettings = true;
        Browser.Visibility = Visibility.Collapsed;
        LoadingView.Visibility = Visibility.Collapsed;
        FaultView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        if (tab >= 0) SettingsTabs.SelectedIndex = tab;
        Dispatcher.BeginInvoke(UpdateResponsiveSettingsLayout, DispatcherPriority.Loaded);
        RefreshRuntimeView();
        RefreshLogs();
        _ = UpdateDataUsageAsync();
        _ = EnsurePluginsLoadedAsync();
        _ = EnsureSkillsLoadedAsync();
    }

    private void UpdateResponsiveSettingsLayout()
    {
        var width = SettingsView.ActualWidth;
        if (width <= 0) return;

        var compact = width < 1120;
        var narrow = width < 930;
        // At the supported minimum width, the plugin cards still have enough
        // room side by side. Keeping them horizontal preserves list height.
        var stackPluginChrome = width < 760;
        var shortViewport = SettingsView.ActualHeight < 720;

        SettingsTabs.ApplyTemplate();
        if (SettingsTabs.Template.FindName("SettingsSidebarColumn", SettingsTabs) is ColumnDefinition sidebar)
            sidebar.Width = new GridLength(narrow ? 168 : compact ? 190 : 218);

        var tabWidth = narrow ? 137d : compact ? 157d : 185d;
        foreach (var item in SettingsTabs.Items.OfType<TabItem>()) item.Width = tabWidth;

        var pageMargin = narrow
            ? new Thickness(22, 25, 22, 34)
            : compact
                ? new Thickness(32, 30, 32, 42)
                : new Thickness(44, 34, 44, 50);
        GeneralContent.Margin = pageMargin;
        RuntimeContent.Margin = pageMargin;
        BackupContent.Margin = pageMargin;
        LogsContent.Margin = new Thickness(pageMargin.Left, pageMargin.Top, pageMargin.Right, Math.Min(38, pageMargin.Bottom));
        DiagnosticsContent.Margin = LogsContent.Margin;
        PluginContent.Margin = narrow
            ? new Thickness(18, 22, 18, 27)
            : compact
                ? new Thickness(28, 26, 28, 30)
                : new Thickness(38, 28, 38, 32);
        SkillsContent.Margin = PluginContent.Margin;

        ArrangeResponsivePair(RuntimeSummaryGrid, RuntimeProcessCard, compact, 14);
        ArrangeResponsivePair(PluginInstallGrid, LocalPluginCard, stackPluginChrome, 12);
        ArrangeResponsivePair(SkillActionGrid, SkillCreateCard, stackPluginChrome, 12);
        ArrangeResponsivePair(BackupSummaryGrid, BackupSecurityCard, compact, 14);
        ArrangeResponsiveFooter(UserPluginFooter, UserPluginActions, stackPluginChrome);
        ArrangeResponsiveFooter(OfficialPluginFooter, OfficialPluginActions, stackPluginChrome);
        ArrangeResponsiveFooter(SkillFooter, SkillActions, stackPluginChrome);
        ArrangeOfficialPluginHeader(stackPluginChrome);
        UserPluginListHost.MinHeight = shortViewport ? 105 : 180;
        OfficialPluginListHost.MinHeight = shortViewport ? 170 : 300;
        SkillListHost.MinHeight = shortViewport ? 120 : 180;
        ArrangeLogFilters(compact);
    }

    private static void ArrangeResponsivePair(Grid grid, FrameworkElement second, bool stacked, double gap)
    {
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : gap);
        grid.ColumnDefinitions[2].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(second, stacked ? 0 : 2);
        Grid.SetRow(second, stacked ? 1 : 0);
        second.Margin = stacked ? new Thickness(0, 12, 0, 0) : new Thickness(0);
    }

    private static void ArrangeResponsiveFooter(Grid footer, FrameworkElement actions, bool stacked)
    {
        footer.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        footer.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : GridLength.Auto;
        Grid.SetColumn(actions, stacked ? 0 : 1);
        Grid.SetRow(actions, stacked ? 1 : 0);
        actions.HorizontalAlignment = stacked ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        actions.Margin = stacked ? new Thickness(0, 10, 0, 0) : new Thickness(0);
    }

    private void ArrangeOfficialPluginHeader(bool stacked)
    {
        OfficialPluginHeader.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        OfficialPluginHeader.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : GridLength.Auto;
        Grid.SetColumn(OpenOfficialDirectoryButton, stacked ? 0 : 1);
        Grid.SetRow(OpenOfficialDirectoryButton, stacked ? 1 : 0);
        OpenOfficialDirectoryButton.HorizontalAlignment = stacked ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        OpenOfficialDirectoryButton.Margin = stacked ? new Thickness(0, 12, 0, 0) : new Thickness(0);
    }

    private void ArrangeLogFilters(bool stacked)
    {
        LogFilterGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        LogFilterGrid.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : GridLength.Auto;
        LogFilterGrid.ColumnDefinitions[2].Width = stacked ? new GridLength(0) : GridLength.Auto;

        Grid.SetColumn(OnlyWarningsCheck, stacked ? 0 : 1);
        Grid.SetRow(OnlyWarningsCheck, stacked ? 1 : 0);
        OnlyWarningsCheck.Margin = stacked ? new Thickness(0, 10, 0, 7) : new Thickness(14, 0, 14, 0);

        Grid.SetColumn(LogActionPanel, stacked ? 0 : 2);
        Grid.SetRow(LogActionPanel, stacked ? 2 : 0);
        LogActionPanel.HorizontalAlignment = stacked ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
    }

    private void RefreshRuntimeView()
    {
        CurrentVersionText.Text = _settings.CurrentRuntimeVersion;
        RuntimeVersionsList.ItemsSource = _runtime.InstalledVersions();
        UserPluginDirectoryText.Text = "用户插件目录：" + _plugins.UserPluginDirectory(_settings);
        OfficialPluginDirectoryText.Text = "官方组件目录：" + _plugins.OfficialPluginDirectory(_settings);
    }

    private void UpdateResourceText()
    {
        var process = _server.Process;
        if (process is null) { ResourceText.Text = "未运行"; return; }
        try
        {
            process.Refresh();
            var uptime = DateTime.Now - process.StartTime;
            ResourceText.Text = $"PID {process.Id} · {process.WorkingSet64 / 1024d / 1024d:N0} MiB · 运行 {uptime:hh\\:mm\\:ss}";
        }
        catch { ResourceText.Text = "正在刷新…"; }
    }

    private void ConfigureTray()
    {
        var executablePath = Environment.ProcessPath;
        var icon = !string.IsNullOrWhiteSpace(executablePath)
            ? System.Drawing.Icon.ExtractAssociatedIcon(executablePath)
            : null;
        _tray = new Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "DeepSeek Harness Desktop",
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => Dispatcher.Invoke(ShowAndActivate));
        menu.Items.Add("重启服务器", null, async (_, _) => await Dispatcher.InvokeAsync(async () => await RunGuardedAsync(() => _server.RestartAsync(_settings), true)).Task.Unwrap());
        menu.Items.Add("在浏览器中打开", null, (_, _) => OpenExternal($"http://127.0.0.1:{_settings.Port}/"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("彻底退出", null, async (_, _) => await Dispatcher.InvokeAsync(ExitApplicationAsync).Task.Unwrap());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowAndActivate);
        _tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(ShowAndActivate);
    }

    private void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private async Task ExitApplicationAsync()
    {
        if (_explicitExit) return;
        _explicitExit = true;
        Hide();
        try { await _server.StopAsync(); } catch (Exception ex) { _log.Warn("shutdown", ex.Message); }
        _tray?.Dispose();
        _dataUsageCts?.Cancel();
        _dataUsageCts?.Dispose();
        _windowSource?.RemoveHook(WindowMessageHook);
        _log.Dispose();
        SystemEvents.SessionEnding -= SystemEvents_SessionEnding;
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        System.Windows.Application.Current.Shutdown();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_explicitExit) return;
        if (_settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            _tray?.ShowBalloonTip(1500, "DeepSeek Harness Desktop", "服务器仍在后台运行。", Forms.ToolTipIcon.Info);
            return;
        }
        e.Cancel = true;
        await ExitApplicationAsync();
    }

    private async void SystemEvents_SessionEnding(object sender, SessionEndingEventArgs e) => await ExitApplicationAsync();

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color)) return;
        Dispatcher.BeginInvoke(() =>
        {
            var mode = ShellThemeService.NormalizeMode(_settings.ShellThemeMode);
            if (mode == ShellThemeService.FollowSystem || mode == ShellThemeService.FollowWeb && _webIsLight is null)
                ApplyResolvedShellTheme();
        });
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void RefreshButton_Click(object sender, RoutedEventArgs e) { if (_showingSettings) RefreshRuntimeView(); else Browser.Reload(); }
    private async void SettingsButton_Click(object sender, RoutedEventArgs e) { if (_showingSettings && _server.State == ServerState.Running) await NavigateHarnessAsync(); else ShowSettings(); }
    private async void ReturnHarnessButton_Click(object sender, RoutedEventArgs e) { if (_server.State != ServerState.Running) await RunGuardedAsync(StartServerCoreAsync, true); else await NavigateHarnessAsync(); }
    private async void StartButton_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(StartServerCoreAsync, true);
    private async void StopButton_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(async () => { await _server.StopAsync(); ShowSettings(); });
    private async void RestartButton_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(async () => { ShowLoading("正在重启服务器…"); await _server.RestartAsync(_settings); await NavigateHarnessAsync(); }, true);
    private async void RetryButton_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(StartServerCoreAsync, true);
    private void OpenDiagnosticsButton_Click(object sender, RoutedEventArgs e) => ShowSettings(6);
    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) => OpenFolder(_paths.Logs);

    private string? SelectFolder(string description, string initial)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = description,
            InitialDirectory = Directory.Exists(initial) ? initial : string.Empty,
            Multiselect = false
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
    private void BrowseWorkspace_Click(object sender, RoutedEventArgs e) { var path = SelectFolder("选择默认工作目录", WorkspaceBox.Text); if (path is not null) WorkspaceBox.Text = path; }
    private void BrowseDshHome_Click(object sender, RoutedEventArgs e) { var path = SelectFolder("选择 DSH_HOME", DshHomeBox.Text); if (path is not null) DshHomeBox.Text = path; }
    private void SetupBrowseWorkspace_Click(object sender, RoutedEventArgs e) { var path = SelectFolder("选择默认工作目录", SetupWorkspaceBox.Text); if (path is not null) SetupWorkspaceBox.Text = path; }
    private void SetupBrowseDshHome_Click(object sender, RoutedEventArgs e) { var path = SelectFolder("选择 DSH_HOME", SetupDshHomeBox.Text); if (path is not null) SetupDshHomeBox.Text = path; }

    private async void CompleteSetupButton_Click(object sender, RoutedEventArgs e)
    {
        SetupErrorText.Text = "";
        CompleteSetupButton.IsEnabled = false;
        try
        {
            var (workspace, dshHome, port) = SettingsService.ValidateConnectionInput(SetupWorkspaceBox.Text, SetupDshHomeBox.Text, SetupPortBox.Text);
            if (HarnessProcessService.IsPortInUse(port)) throw new InvalidOperationException($"端口 {port} 已被其他程序占用，请换一个端口。");
            _settings.Workspace = workspace;
            _settings.DshHome = dshHome;
            _settings.Port = port;
            _settings.Initialized = true;
            _settings.AutoStartServer = true;
            _settingsService.Save(_settings);
            Directory.CreateDirectory(_settings.Workspace);
            Directory.CreateDirectory(_settings.DshHome);
            ApplySettingsToControls();
            SetupOverlay.Visibility = Visibility.Collapsed;
            ShowLoading("正在释放完全离线运行环境…");
            await _runtime.EnsureSeedAsync(new Progress<string>(ShowLoading));
            await InitializeBrowserAsync();
            await StartServerCoreAsync();
        }
        catch (Exception ex)
        {
            _settings.Initialized = false;
            try { _settingsService.Save(_settings); } catch { }
            SetupOverlay.Visibility = Visibility.Visible;
            SetupErrorText.Text = LogService.Redact(ex.Message);
            _log.Error("setup", ex.ToString());
        }
        finally { CompleteSetupButton.IsEnabled = true; }
    }

    private async void SaveGeneralButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            GeneralStatusPanel.Visibility = Visibility.Collapsed;
            var (workspace, dshHome, port) = SettingsService.ValidateConnectionInput(WorkspaceBox.Text, DshHomeBox.Text, PortBox.Text);
            var wasRunning = _server.State == ServerState.Running;
            var portBelongsToHarness = wasRunning && port == _settings.Port;
            if (!portBelongsToHarness && HarnessProcessService.IsPortInUse(port)) throw new InvalidOperationException($"端口 {port} 已被其他程序占用，请换一个端口。");

            var old = (_settings.Workspace, _settings.DshHome, _settings.Port, _settings.CloseToTray, _settings.NotifyOnResponseComplete,
                _settings.LaunchAtLogin, _settings.AutoStartServer, _settings.OpenExternalBrowser, _settings.ForceTelemetryOff);
            var connectionChanged = port != _settings.Port || !workspace.Equals(_settings.Workspace, StringComparison.OrdinalIgnoreCase) || !dshHome.Equals(_settings.DshHome, StringComparison.OrdinalIgnoreCase);
            var forceTelemetryOff = TelemetryCheck.IsChecked == true;
            var restartRequired = connectionChanged || forceTelemetryOff != _settings.ForceTelemetryOff;
            try
            {
                Directory.CreateDirectory(workspace);
                Directory.CreateDirectory(dshHome);
                _settings.Workspace = workspace; _settings.DshHome = dshHome; _settings.Port = port;
                _settings.CloseToTray = CloseToTrayCheck.IsChecked == true;
                _settings.NotifyOnResponseComplete = ResponseNotificationCheck.IsChecked == true;
                _settings.LaunchAtLogin = LaunchAtLoginCheck.IsChecked == true;
                _settings.AutoStartServer = AutoStartCheck.IsChecked == true;
                _settings.OpenExternalBrowser = ExternalBrowserCheck.IsChecked == true;
                _settings.ForceTelemetryOff = forceTelemetryOff;
                _settingsService.Save(_settings);
                if (restartRequired && wasRunning) await _server.RestartAsync(_settings);
            }
            catch
            {
                (_settings.Workspace, _settings.DshHome, _settings.Port, _settings.CloseToTray, _settings.NotifyOnResponseComplete,
                    _settings.LaunchAtLogin, _settings.AutoStartServer, _settings.OpenExternalBrowser, _settings.ForceTelemetryOff) = old;
                _settingsService.Save(_settings);
                ApplySettingsToControls();
                if (wasRunning && _server.State != ServerState.Running) try { await _server.StartAsync(_settings); } catch (Exception rollbackError) { _log.Error("settings", "rollback start failed: " + rollbackError); }
                throw;
            }

            _pluginsLoaded = false;
            _skillsLoaded = false;
            RefreshRuntimeView();
            _ = UpdateDataUsageAsync();
            GeneralStatusText.Text = restartRequired && wasRunning ? "设置已保存，Harness 已使用新配置重新启动。" : "设置已保存并立即生效。";
            GeneralStatusPanel.Visibility = Visibility.Visible;
        });
    }

    private async void ClearWebViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (ShellDialog.Show(this, "将清除内嵌浏览器缓存和登录状态，但不会删除 Harness 数据。是否继续？", "清理 WebView2", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunGuardedAsync(async () =>
        {
            if (_browserInitialized) await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            ShellDialog.Show(this, "WebView2 浏览数据已清理。", "完成");
        });
    }

    private async Task CheckUpdatesSilentAsync()
    {
        try { await CheckUpdatesCoreAsync(false); } catch (Exception ex) { _log.Warn("update", ex.Message); }
    }
    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(() => CheckUpdatesCoreAsync(true));
    private async Task CheckUpdatesCoreAsync(bool notify)
    {
        LatestVersionText.Text = "正在查询 npm…";
        _latestVersion = await _runtime.CheckLatestAsync();
        _settings.LastUpdateCheck = DateTimeOffset.Now;
        _settingsService.Save(_settings);
        var available = !string.IsNullOrWhiteSpace(_latestVersion) && !_latestVersion.Equals(_settings.CurrentRuntimeVersion, StringComparison.OrdinalIgnoreCase);
        LatestVersionText.Text = available ? $"可用版本：{_latestVersion}（当前 {_settings.CurrentRuntimeVersion}）" : $"当前已是 npm 最新版：{_settings.CurrentRuntimeVersion}";
        InstallUpdateButton.IsEnabled = available;
        if (notify && !available) ShellDialog.Show(this, "当前已是 npm 最新版。", "检查更新");
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_latestVersion)) return;
        if (ShellDialog.Show(this, $"将安装并健康验证 Harness {_latestVersion}，失败会自动回滚。继续吗？", "安装更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunGuardedAsync(async () =>
        {
            var old = _settings.CurrentRuntimeVersion;
            var wasRunning = _server.State == ServerState.Running;
            if (wasRunning) await _server.StopAsync();
            ShowLoading($"正在安装 Harness {_latestVersion}…");
            try
            {
                await _runtime.InstallVersionAsync(_latestVersion!, old, new Progress<string>(ShowLoading));
                _settings.CurrentRuntimeVersion = _latestVersion!;
                _settingsService.Save(_settings);
                await _server.StartAsync(_settings);
                await NavigateHarnessAsync();
            }
            catch
            {
                await _runtime.SwitchAsync(old);
                _settings.CurrentRuntimeVersion = old;
                _settingsService.Save(_settings);
                if (wasRunning) { await _server.StartAsync(_settings); await NavigateHarnessAsync(); }
                throw;
            }
            RefreshRuntimeView();
        }, true);
    }

    private async void RollbackButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = RuntimeVersionsList.SelectedItem as string;
        selected ??= _runtime.InstalledVersions().FirstOrDefault(x => !x.Equals(_settings.CurrentRuntimeVersion, StringComparison.OrdinalIgnoreCase));
        if (selected is null) { ShellDialog.Show(this, "没有可回滚的旧版本。", "版本回滚"); return; }
        await RunGuardedAsync(async () =>
        {
            var wasRunning = _server.State == ServerState.Running;
            if (wasRunning) await _server.StopAsync();
            await _runtime.SwitchAsync(selected);
            _settings.CurrentRuntimeVersion = selected;
            _settingsService.Save(_settings);
            if (wasRunning) { await _server.StartAsync(_settings); await NavigateHarnessAsync(); }
            RefreshRuntimeView();
        });
    }

    private async Task WithServerStoppedAsync(Func<Task> operation)
    {
        var wasRunning = _server.State == ServerState.Running;
        if (wasRunning) await _server.StopAsync();
        try { await operation(); }
        finally { if (wasRunning) await _server.StartAsync(_settings); }
    }

    private async Task RefreshPluginsAsync()
    {
        PluginEmptyState.Visibility = Visibility.Visible;
        OfficialPluginEmptyState.Visibility = Visibility.Visible;
        PluginEmptyTitle.Text = "正在读取用户插件";
        PluginEmptySubtitle.Text = "从 DSH_HOME 的 web profile 读取依赖…";
        PluginCountText.Text = "正在加载…";
        OfficialPluginCountText.Text = "正在加载…";
        try
        {
            var rows = await _plugins.InspectAsync(_settings);
            var official = rows.Where(x => x.IsBuiltIn).OrderBy(x => x.Source).ThenBy(x => x.Id).ToArray();
            var user = rows.Where(x => !x.IsBuiltIn).OrderBy(x => x.Package).ThenBy(x => x.Id).ToArray();
            PluginsList.ItemsSource = user;
            OfficialPluginsList.ItemsSource = official;
            PluginCountText.Text = user.Length == 0 ? "尚未安装用户插件" : $"用户插件 {user.Length} 个";
            OfficialPluginCountText.Text = $"官方组件 {official.Length} 个 · @deepseek-ai/dsh-base 与 dsh-web-app";
            PluginEmptyState.Visibility = user.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            OfficialPluginEmptyState.Visibility = official.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            PluginEmptyTitle.Text = "尚未安装用户插件";
            PluginEmptySubtitle.Text = "用户依赖只安装到 DSH_HOME 的 web profile";
            _pluginsLoaded = true;
        }
        catch (Exception ex)
        {
            PluginEmptyState.Visibility = Visibility.Visible;
            PluginEmptyTitle.Text = "插件列表读取失败";
            PluginEmptySubtitle.Text = LogService.Redact(ex.Message);
            PluginCountText.Text = "读取失败";
            OfficialPluginCountText.Text = "读取失败";
            throw;
        }
    }
    private async Task EnsurePluginsLoadedAsync()
    {
        if (_pluginsLoaded || !_settings.Initialized || !_runtime.IsInstalled(_settings.CurrentRuntimeVersion)) return;
        try { await RefreshPluginsAsync(); } catch (Exception ex) { _log.Warn("plugin", "initial list refresh failed: " + ex.Message); }
    }
    private void ShowUserPluginsButton_Click(object sender, RoutedEventArgs e) => SetPluginSection(false);
    private void ShowOfficialPluginsButton_Click(object sender, RoutedEventArgs e) => SetPluginSection(true);
    private void SetPluginSection(bool official)
    {
        UserPluginSection.Visibility = official ? Visibility.Collapsed : Visibility.Visible;
        OfficialPluginSection.Visibility = official ? Visibility.Visible : Visibility.Collapsed;
        if (official)
        {
            UserPluginViewButton.Background = System.Windows.Media.Brushes.Transparent;
            OfficialPluginViewButton.SetResourceReference(BackgroundProperty, "SegmentSelected");
        }
        else
        {
            UserPluginViewButton.SetResourceReference(BackgroundProperty, "SegmentSelected");
            OfficialPluginViewButton.Background = System.Windows.Media.Brushes.Transparent;
        }
    }
    private async void RefreshPluginsButton_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(async () => { _pluginsLoaded = false; await RefreshPluginsAsync(); });
    private async void InstallPluginButton_Click(object sender, RoutedEventArgs e)
    {
        var raw = PluginSpecBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw)) { ShellDialog.Show(this, "请输入包名、仓库链接或安装指令。", "安装插件"); PluginSpecBox.Focus(); return; }
        string spec;
        try { spec = PluginService.NormalizeInstallSpec(raw); }
        catch (ArgumentException ex) { ShellDialog.Show(this, ex.Message, "无法识别安装来源", MessageBoxButton.OK, MessageBoxImage.Warning); PluginSpecBox.Focus(); return; }
        var parsed = raw.Equals(spec, StringComparison.Ordinal) ? string.Empty : $"\n解析后：{spec}";
        var sourceKind = PluginService.DescribeInstallSource(spec);
        if (ShellDialog.Show(this, $"输入：{raw}{parsed}\n来源类型：{sourceKind}\n安装目标：web profile 用户插件目录\n\n插件及其生命周期脚本会以当前用户权限执行。仅安装你信任的来源。", "确认插件来源", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunGuardedAsync(async () => { await WithServerStoppedAsync(async () => { var exit = await _plugins.InstallAsync(_settings, spec, false); if (exit != 0) throw new InvalidOperationException($"插件安装失败，退出代码 {exit}。"); }); PluginSpecBox.Clear(); await RefreshPluginAndSkillListsAsync(); });
    }
    private void BrowseLocalPlugin_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectFolder("选择包含 package.json 的插件目录", LocalPluginPathBox.Text);
        if (path is null) return;
        LocalPluginPathBox.Text = path;
        LocalPluginPathBox.ToolTip = path;
    }
    private async void InstallLocalPluginButton_Click(object sender, RoutedEventArgs e)
    {
        var path = LocalPluginPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) { ShellDialog.Show(this, "请先选择有效的本地插件目录。", "安装本地插件"); return; }
        if (!File.Exists(Path.Combine(path, "package.json"))) { ShellDialog.Show(this, "所选目录不包含 package.json，请选择插件项目根目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var link = PluginLinkCheck.IsChecked == true;
        var mode = link ? "link: 开发链接（目录变化实时生效）" : "file: 稳定本地安装";
        if (ShellDialog.Show(this, $"目录：{path}\n方式：{mode}\n\n本地插件会以当前用户权限执行。确认信任并安装吗？", "确认本地插件", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunGuardedAsync(async () => { await WithServerStoppedAsync(async () => { var exit = await _plugins.InstallAsync(_settings, path, link); if (exit != 0) throw new InvalidOperationException($"本地插件安装失败，退出代码 {exit}。"); }); LocalPluginPathBox.Clear(); await RefreshPluginAndSkillListsAsync(); });
    }
    private async void UpdatePluginsButton_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(async () => { await WithServerStoppedAsync(async () => { var exit = await _plugins.UpdateAllAsync(_settings); if (exit != 0) throw new InvalidOperationException($"插件更新失败，退出代码 {exit}。"); }); await RefreshPluginAndSkillListsAsync(); });
    private async void EnablePluginButton_Click(object sender, RoutedEventArgs e) => await SetSelectedPluginDisabledAsync(false);
    private async void DisablePluginButton_Click(object sender, RoutedEventArgs e) => await SetSelectedPluginDisabledAsync(true);
    private async Task SetSelectedPluginDisabledAsync(bool disabled)
    {
        if (PluginsList.SelectedItem is not PluginItem plugin) { ShellDialog.Show(this, "请先在列表中选择一个插件。", disabled ? "禁用插件" : "启用插件"); return; }
        await SetPluginDisabledAsync(plugin, disabled);
    }
    private async void EnableOfficialPluginButton_Click(object sender, RoutedEventArgs e) => await SetSelectedOfficialPluginDisabledAsync(false);
    private async void DisableOfficialPluginButton_Click(object sender, RoutedEventArgs e) => await SetSelectedOfficialPluginDisabledAsync(true);
    private async Task SetSelectedOfficialPluginDisabledAsync(bool disabled)
    {
        if (OfficialPluginsList.SelectedItem is not PluginItem plugin) { ShellDialog.Show(this, "请先在官方组件列表中选择一个条目。", disabled ? "禁用官方组件" : "启用官方组件"); return; }
        await SetPluginDisabledAsync(plugin, disabled);
    }
    private async Task SetPluginDisabledAsync(PluginItem plugin, bool disabled) =>
        await RunGuardedAsync(async () => { await WithServerStoppedAsync(() => _plugins.SetDisabledAsync(_settings, plugin, disabled)); await RefreshPluginsAsync(); });
    private async void RemovePluginButton_Click(object sender, RoutedEventArgs e)
    {
        if (PluginsList.SelectedItem is not PluginItem plugin) { ShellDialog.Show(this, "请先在列表中选择一个用户插件。", "卸载插件"); return; }
        if (plugin.IsBuiltIn) { ShellDialog.Show(this, "官方插件受保护，只能禁用，不能卸载。", "插件保护"); return; }
        if (ShellDialog.Show(this, $"确定卸载 {plugin.Package}？", "卸载插件", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunGuardedAsync(async () => { await WithServerStoppedAsync(async () => { var exit = await _plugins.RemoveAsync(_settings, plugin); if (exit != 0) throw new InvalidOperationException($"卸载失败，退出代码 {exit}。"); }); await RefreshPluginAndSkillListsAsync(); });
    }
    private void OpenUserPluginsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_plugins.UserPluginDirectory(_settings));
    private void OpenOfficialPluginsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_plugins.OfficialPluginDirectory(_settings));

    private async Task RefreshPluginAndSkillListsAsync()
    {
        await RefreshPluginsAsync();
        _skillsLoaded = false;
        await RefreshSkillsAsync();
    }

    private async Task RefreshSkillsAsync()
    {
        SkillEmptyState.Visibility = Visibility.Visible;
        SkillCountText.Text = "正在加载…";
        SkillRootText.Text = $"用户：{_skills.UserRoot(_settings)}  ·  项目：{_skills.ProjectRoot(_settings)}";
        var items = await Task.Run(() => _skills.Inspect(_settings));
        SkillsList.ItemsSource = items;
        SkillEmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var userCount = items.Count(x => x.Scope == "用户");
        var projectCount = items.Count - userCount;
        var invalidCount = items.Count(x => !x.IsValid);
        SkillCountText.Text = $"共 {items.Count} 个 · 用户 {userCount} · 项目 {projectCount}" + (invalidCount == 0 ? string.Empty : $" · 格式错误 {invalidCount}");
        _skillsLoaded = true;
    }

    private async Task EnsureSkillsLoadedAsync()
    {
        if (_skillsLoaded || !_settings.Initialized) return;
        try { await RefreshSkillsAsync(); }
        catch (Exception ex)
        {
            SkillCountText.Text = "读取失败";
            SkillRootText.Text = LogService.Redact(ex.Message);
            _log.Warn("skill", "initial list refresh failed: " + ex.Message);
        }
    }

    private async void RefreshSkillsButton_Click(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(async () => { _skillsLoaded = false; await RefreshSkillsAsync(); });

    private void BrowseSkillDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectFolder("选择根目录中包含 SKILL.md 的 Skill", SkillImportPathBox.Text);
        if (path is null) return;
        SkillImportPathBox.Text = path;
        SkillImportPathBox.ToolTip = path;
    }

    private void BrowseSkillMarkdownButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择 Skill Markdown", Filter = "Skill Markdown (*.md)|*.md|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        SkillImportPathBox.Text = dialog.FileName;
        SkillImportPathBox.ToolTip = dialog.FileName;
    }

    private async void ImportUserSkillButton_Click(object sender, RoutedEventArgs e) => await ImportSkillAsync(true);
    private async void ImportProjectSkillButton_Click(object sender, RoutedEventArgs e) => await ImportSkillAsync(false);
    private async Task ImportSkillAsync(bool userScope)
    {
        var path = SkillImportPathBox.Text.Trim();
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            ShellDialog.Show(this, "请先选择有效的 Skill 目录或 Markdown 文件。", "导入 Skill", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var scope = userScope ? "用户级" : "项目级";
        if (ShellDialog.Show(this, $"来源：{path}\n目标：{scope} Skill 目录\n\nSkill 中的指令会提供给模型，并可能引导工具操作。请只导入你信任的内容。", "确认导入 Skill", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunGuardedAsync(async () =>
        {
            var target = await Task.Run(() => Directory.Exists(path) ? _skills.ImportDirectory(_settings, path, userScope) : _skills.ImportMarkdown(_settings, path, userScope));
            SkillImportPathBox.Clear();
            _skillsLoaded = false;
            await RefreshSkillsAsync();
            ShellDialog.Show(this, "Skill 已导入：\n" + target, "导入完成");
        });
    }

    private async void CreateUserSkillButton_Click(object sender, RoutedEventArgs e) => await CreateSkillAsync(true);
    private async void CreateProjectSkillButton_Click(object sender, RoutedEventArgs e) => await CreateSkillAsync(false);
    private async Task CreateSkillAsync(bool userScope)
    {
        var name = NewSkillNameBox.Text.Trim();
        await RunGuardedAsync(async () =>
        {
            var target = await Task.Run(() => _skills.CreateTemplate(_settings, name, userScope));
            NewSkillNameBox.Clear();
            _skillsLoaded = false;
            await RefreshSkillsAsync();
            OpenExternal(Path.Combine(target, "SKILL.md"));
        });
    }

    private SkillItem? SelectedSkill(string action)
    {
        if (SkillsList.SelectedItem is SkillItem item) return item;
        ShellDialog.Show(this, "请先在列表中选择一个 Skill。", action);
        return null;
    }

    private void OpenSelectedSkillLocationButton_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedSkill("打开 Skill");
        if (item is null) return;
        OpenFolder(Directory.Exists(item.EntryPath) ? item.EntryPath : Path.GetDirectoryName(item.EntryPath)!);
    }

    private void EditSelectedSkillButton_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedSkill("编辑 Skill");
        if (item is not null) OpenExternal(item.ManifestPath);
    }

    private async void EnableSkillButton_Click(object sender, RoutedEventArgs e) => await SetSelectedSkillEnabledAsync(true);
    private async void DisableSkillButton_Click(object sender, RoutedEventArgs e) => await SetSelectedSkillEnabledAsync(false);
    private async Task SetSelectedSkillEnabledAsync(bool enabled)
    {
        var item = SelectedSkill(enabled ? "启用 Skill" : "禁用 Skill");
        if (item is null) return;
        await RunGuardedAsync(async () =>
        {
            await Task.Run(() => _skills.SetEnabled(item, enabled));
            _skillsLoaded = false;
            await RefreshSkillsAsync();
        });
    }

    private async void RemoveSkillButton_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedSkill("移除 Skill");
        if (item is null) return;
        if (ShellDialog.Show(this, $"将 {item.Name} 移到其 Skill 根目录下的 .trash 回收区，不会永久删除。继续吗？", "移除 Skill", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunGuardedAsync(async () =>
        {
            var target = await Task.Run(() => _skills.MoveToTrash(_settings, item));
            _skillsLoaded = false;
            await RefreshSkillsAsync();
            ShellDialog.Show(this, "已移到可恢复位置：\n" + target, "Skill 已移除");
        });
    }

    private void OpenUserSkillRootButton_Click(object sender, RoutedEventArgs e) => OpenFolder(_skills.UserRoot(_settings));
    private void OpenProjectSkillRootButton_Click(object sender, RoutedEventArgs e) => OpenFolder(_skills.ProjectRoot(_settings));

    private async Task UpdateDataUsageAsync()
    {
        _dataUsageCts?.Cancel();
        _dataUsageCts?.Dispose();
        var cancellation = _dataUsageCts = new CancellationTokenSource();
        DataUsageText.Text = $"DSH_HOME：{_settings.DshHome}\n正在后台统计占用…";
        try
        {
            var dshHome = _settings.DshHome;
            var bytes = await Task.Run(() =>
            {
                if (!Directory.Exists(dshHome)) return 0L;
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, ReturnSpecialDirectories = false };
                long total = 0;
                foreach (var file in Directory.EnumerateFiles(dshHome, "*", options))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    try { total += new FileInfo(file).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                return total;
            }, cancellation.Token);
            if (cancellation != _dataUsageCts || cancellation.IsCancellationRequested) return;
            DataUsageText.Text = $"DSH_HOME：{_settings.DshHome}\n当前占用：{bytes / 1024d / 1024d:N1} MiB；备份默认排除凭据、.env 与 node_modules。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (cancellation == _dataUsageCts) DataUsageText.Text = "无法统计：" + ex.Message; }
    }
    private async void CreateBackupButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "DeepSeek Harness 加密备份 (*.dshbackup)|*.dshbackup", DefaultExt = ".dshbackup", FileName = $"DeepSeek-Harness-{DateTime.Now:yyyyMMdd-HHmmss}.dshbackup", InitialDirectory = _paths.Backups };
        if (dialog.ShowDialog(this) != true) return;
        await RunGuardedAsync(async () => { var path = await _backup.CreateAsync(_settings, BackupPasswordBox.Password, IncludeSecretsCheck.IsChecked == true, dialog.FileName, new Progress<string>(SetBackupStatus)); BackupPasswordBox.Clear(); SetBackupStatus("备份已创建：" + path); });
    }
    private async void ValidateBackupButton_Click(object sender, RoutedEventArgs e)
    {
        var file = ChooseBackup(); if (file is null) return;
        await RunGuardedAsync(async () => { SetBackupStatus(await _backup.ValidateAsync(file, BackupPasswordBox.Password)); BackupPasswordBox.Clear(); });
    }
    private async void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
    {
        var file = ChooseBackup(); if (file is null) return;
        if (ShellDialog.Show(this, "恢复会整体替换当前 DSH_HOME，原目录将保留为回滚副本。继续吗？", "恢复备份", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunGuardedAsync(async () =>
        {
            var wasRunning = _server.State == ServerState.Running; if (wasRunning) await _server.StopAsync();
            try { var rollback = await _backup.RestoreAsync(_settings, file, BackupPasswordBox.Password, new Progress<string>(SetBackupStatus)); SetBackupStatus("恢复完成；旧数据位于：" + rollback); }
            finally { BackupPasswordBox.Clear(); if (wasRunning) await _server.StartAsync(_settings); }
        });
    }
    private void SetBackupStatus(string text) { BackupStatusPanel.Visibility = Visibility.Visible; BackupStatusText.Text = text; }
    private string? ChooseBackup() { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "DeepSeek Harness 加密备份 (*.dshbackup)|*.dshbackup", InitialDirectory = _paths.Backups }; return dialog.ShowDialog(this) == true ? dialog.FileName : null; }
    private void OpenDataButton_Click(object sender, RoutedEventArgs e) => OpenFolder(_settings.DshHome);

    private void AppendLog(string line)
    {
        if (!_showingSettings || SettingsTabs.SelectedIndex != 5) return;
        if (!LogLineMatches(line)) return;
        LogsBox.AppendText(line + Environment.NewLine);
        LogsBox.ScrollToEnd();
    }
    private bool LogLineMatches(string line)
    {
        if (OnlyWarningsCheck?.IsChecked == true && !line.Contains("[WARN]", StringComparison.Ordinal) && !line.Contains("[ERROR]", StringComparison.Ordinal)) return false;
        var search = LogSearchBox?.Text?.Trim();
        return string.IsNullOrWhiteSpace(search) || line.Contains(search, StringComparison.OrdinalIgnoreCase);
    }
    private void RefreshLogs() { LogsBox.Text = string.Join(Environment.NewLine, _log.Recent.Where(LogLineMatches)); LogsBox.ScrollToEnd(); }
    private void RefreshLogsButton_Click(object sender, RoutedEventArgs e) => RefreshLogs();
    private void LogFilterChanged(object sender, RoutedEventArgs e) { if (LogsBox is not null) RefreshLogs(); }
    private void ExportLogsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt", DefaultExt = ".log", FileName = $"DeepSeek-Harness-{DateTime.Now:yyyyMMdd-HHmmss}.log", InitialDirectory = _paths.Logs };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, LogsBox.Text);
        ShellDialog.Show(this, "当前筛选结果已导出。", "导出日志");
    }

    private async void RunDiagnosticsButton_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(async () => { DiagnosticsBox.Text = await _diagnostics.RunAsync(_settings); DiagnosticsEmptyState.Visibility = Visibility.Collapsed; });
    private void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DiagnosticsBox.Text)) { ShellDialog.Show(this, "请先运行完整诊断。", "导出诊断"); return; }
        var path = _diagnostics.Export(DiagnosticsBox.Text + Environment.NewLine + string.Join(Environment.NewLine, _log.Recent.TakeLast(300)));
        ShellDialog.Show(this, "诊断包已导出：\n" + path, "导出完成");
    }
    private async void RepairPluginsButton_Click(object sender, RoutedEventArgs e) => await RunGuardedAsync(async () => { await WithServerStoppedAsync(async () => { var exit = await _plugins.ReinstallAsync(_settings); if (exit != 0) throw new InvalidOperationException($"重建插件依赖失败，退出代码 {exit}。"); }); await RefreshPluginAndSkillListsAsync(); ShellDialog.Show(this, "插件依赖与随包 Skill 已重建。", "修复完成"); });
    private async void ResetPatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (ShellDialog.Show(this, "这会清除启动器记录的插件禁用项，不会删除插件。继续吗？", "撤销覆盖", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunGuardedAsync(async () =>
        {
            await WithServerStoppedAsync(async () =>
            {
                if (File.Exists(_paths.LauncherPatch)) File.Copy(_paths.LauncherPatch, _paths.LauncherPatch + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                await File.WriteAllTextAsync(_paths.LauncherPatch, "[]\n");
                var exit = await _server.RunCliAsync(_settings, $"--profile web --patch \"{_paths.LauncherPatch}\" --dump-config");
                if (exit != 0) throw new InvalidOperationException("清理后的配置未通过验证。 ");
            });
            await RefreshPluginsAsync();
        });
    }

    private static void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        OpenExternal(path);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
