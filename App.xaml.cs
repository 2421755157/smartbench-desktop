using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SmartBench;

public partial class App : Application
{
    private const string CrashLogFileName = "crash.log";
    private const long MaximumCrashLogLength = 2 * 1024 * 1024;
    private static readonly object CrashLogGate = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("UI 线程未处理异常", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            $"程序遇到未处理的错误，已记录到崩溃日志。\n\n{e.Exception.Message}\n\n当前操作已中止，可继续使用其他功能。",
            "智控工作台", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) WriteCrashLog("后台线程未处理异常", exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("未观察到的任务异常", e.Exception);
        e.SetObserved();
    }

    private static void WriteCrashLog(string source, Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartBench");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, CrashLogFileName);

            var builder = new StringBuilder();
            builder.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}");
            builder.AppendLine(exception.ToString());
            builder.AppendLine(new string('-', 72));

            lock (CrashLogGate)
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaximumCrashLogLength) File.Delete(path);
                File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 崩溃日志写入失败不能再抛出异常。
        }
    }
}
