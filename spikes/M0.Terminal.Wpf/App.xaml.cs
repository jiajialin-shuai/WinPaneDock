using System.Windows;
using System.Windows.Threading;
using Cmux.Core;

namespace Cmux.Spike.Terminal;

public partial class App : System.Windows.Application
{
    public static DiagnosticLog Diagnostics { get; } = new("gui");

    /// <summary>
    /// Build identity for the UI, taken from the same <c>Directory.Build.props</c> version the
    /// package manifest is stamped with, so the corner readout and the installed MSIX can never
    /// disagree.
    /// </summary>
    public static string AppVersion { get; } =
        typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";

    public App()
    {
        Diagnostics.Write(DiagnosticLevel.Info, "gui.start", $"version={typeof(App).Assembly.GetName().Version}");
        // M0 取证: 任何未处理异常都必须落到文件, 不允许静默崩溃。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log($"AppDomain.UnhandledException: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Log($"UnobservedTaskException: {e.Exception}");
    }

    public static void Log(string message) => Diagnostics.Write(DiagnosticLevel.Error, "gui.error", message);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log($"DispatcherUnhandledException: {e.Exception}");
        e.Handled = true; // M0 spike: 显示错误但不退出, 便于取证。
    }
}
