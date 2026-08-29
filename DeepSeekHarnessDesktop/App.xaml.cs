using System.Windows;
using DeepSeekHarnessDesktop.Services;

namespace DeepSeekHarnessDesktop;

public partial class App : System.Windows.Application
{
    private SingleInstanceCoordinator? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var instanceSuffix = Environment.GetEnvironmentVariable("DSH_DESKTOP_INSTANCE");
        _instance = new SingleInstanceCoordinator(instanceSuffix);
        if (!_instance.IsPrimary)
        {
            var notified = _instance.NotifyPrimaryAsync().GetAwaiter().GetResult();
            _instance.Dispose();
            _instance = null;
            Environment.Exit(notified ? 0 : 1);
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        _instance.StartListening(() => Dispatcher.BeginInvoke(window.ActivateFromSecondInstance));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
