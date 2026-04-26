using System.Windows;

namespace PanelRuntime;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var minimized = e.Args.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));
        var autoStart = e.Args.Any(arg => string.Equals(arg, "--start", StringComparison.OrdinalIgnoreCase));
        MainWindow = new MainWindow(minimized, autoStart);
        MainWindow.Show();
    }
}
