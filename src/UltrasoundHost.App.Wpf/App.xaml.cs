using System.Windows;
using UltrasoundHost.Application;
using UltrasoundHost.Contracts;
using UltrasoundHost.Plugins.Device.Simulator;
using UltrasoundHost.Plugins.Processing.Basic;
using UltrasoundHost.Plugins.View.Basic;

namespace UltrasoundHost.App.Wpf;

/// <summary>
/// Composition root of the WPF shell: the only place that knows the host and every plugin.
/// <c>--selftest [--report path]</c> runs the headless verification path instead of showing a window.
/// </summary>
public partial class App : System.Windows.Application
{
    private ShellSession? _session;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string[] args = e.Args ?? Array.Empty<string>();

        if (args.Any(argument => string.Equals(argument, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            string reportPath = ValueAfter(args, "--report") ?? string.Empty;
            int exitCode = SelfTest.Run(reportPath);
            Environment.ExitCode = exitCode;
            Shutdown(exitCode);
            return;
        }

        _session = new ShellSession(ComposePlugins());
        try
        {
            _session.InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "插件宿主启动失败：" + Environment.NewLine + Environment.NewLine + ex,
                "超声检测上位机",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Environment.ExitCode = 1;
            Shutdown(1);
            return;
        }

        var window = new MainWindow(_session);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_session is not null)
        {
            try
            {
                _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Nothing sensible left to report during shutdown.
            }

            _session = null;
        }

        base.OnExit(e);
    }

    private static IPlugin[] ComposePlugins() => new IPlugin[]
    {
        new SimulatorPlugin(),
        new GatePeakPlugin(),
        new BasicViewPlugin(),
    };

    private static string? ValueAfter(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
