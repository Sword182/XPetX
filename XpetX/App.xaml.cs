using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace XpetX;

/// <summary>
/// XpetX 应用程序入口，负责全局异常处理。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 初始化 <see cref="App"/> 实例，并注册全局异常处理。
    /// </summary>
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 透明窗口使用软件渲染，避免与游戏争夺 GPU 导致窗口合成降频。
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        base.OnStartup(e);
    }

    /// <summary>
    /// 处理 UI 线程未捕获异常：记录日志并阻止程序崩溃。
    /// </summary>
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        e.Handled = true;
    }

    /// <summary>
    /// 处理非 UI 线程未捕获异常：记录日志。
    /// </summary>
    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogException(exception);
        }
    }

    /// <summary>
    /// 将异常信息追加写入程序目录下的 error.log 文件。
    /// </summary>
    private static void LogException(Exception exception)
    {
        try
        {
            string logPath = Path.Combine(AppContext.BaseDirectory, "error.log");
            var message = new StringBuilder()
                .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]")
                .AppendLine(exception.ToString())
                .AppendLine();
            File.AppendAllText(logPath, message.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 日志写入失败时不再抛出，避免递归异常。
        }
    }
}
