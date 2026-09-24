using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Cmux.Spike.Terminal;

public partial class App : System.Windows.Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux", "logs", "app.log");

    public App()
    {
        // M0 取证: 任何未处理异常都必须落到文件, 不允许静默崩溃。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log($"AppDomain.UnhandledException: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Log($"UnobservedTaskException: {e.Exception}");
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不允许二次崩溃。
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log($"DispatcherUnhandledException: {e.Exception}");
        e.Handled = true; // M0 spike: 显示错误但不退出, 便于取证。
    }
}
