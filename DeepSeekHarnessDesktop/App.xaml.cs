using System.Threading;
using System.Windows;

namespace DeepSeekHarnessDesktop;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var instanceSuffix = Environment.GetEnvironmentVariable("DSH_DESKTOP_INSTANCE");
        var mutexName = string.IsNullOrWhiteSpace(instanceSuffix)
            ? "DeepSeekHarnessDesktop.SingleInstance"
            : $"DeepSeekHarnessDesktop.SingleInstance.{instanceSuffix}";
        _mutex = new Mutex(true, mutexName, out var first);
        if (!first)
        {
            ShellDialog.Show("DeepSeek Harness Desktop 已经在运行，请从系统托盘打开。", "DeepSeek Harness Desktop");
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
