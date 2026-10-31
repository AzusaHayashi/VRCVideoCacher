using System.Windows;
using System.Windows.Threading;
using CookieBridge.Core;

namespace CookieBridge.Manager;

/// <summary>
/// 应用入口。管理器本身是纯 UI 外壳：所有真实动作都在 <see cref="Services.ManagerService"/> 里，
/// 而那里只调用 CookieBridge.Core 已经写好的公共 API。
///
/// 容错原则（用户验收要求）：UI 永不因异常崩溃。这里有一道全局兜底，
/// 任何漏到 Dispatcher 的异常都会被记进状态栏并写进桥接日志。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 启动期的致命错误（例如桥接根目录不可写）。不静默吞掉：主窗口加载后会显示在状态栏，
    /// 否则用户只会看到“一切正常”而实际所有路径都不可用。
    /// </summary>
    public static string? StartupError { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            BridgePaths.EnsureDirectories();
            BridgeLog.Info("manager", $"Manager started (pid {Environment.ProcessId}).");
        }
        catch (Exception ex)
        {
            // 目录不可写也仍然打开 UI，但必须把原因带到界面上。
            StartupError =
                $"桥接根目录不可用：{BridgePaths.Root} —— {ex.GetType().Name}: {ex.Message}" +
                "（安装/备份/日志等功能将不可用）" +
                (ex is UnauthorizedAccessException
                    ? " 提示：当前进程被安全软件/沙箱限制了写入，请从资源管理器直接运行本程序。"
                    : string.Empty);
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            BridgeLog.Error("manager", "Unhandled UI exception", e.Exception);
        }
        catch
        {
            // 记录失败无妨，下面仍然要保证 UI 存活。
        }

        if (MainWindow is MainWindow window)
        {
            window.ShowError($"界面异常已被拦截：{e.Exception.GetType().Name} - {e.Exception.Message}");
        }

        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            if (e.ExceptionObject is Exception ex)
                BridgeLog.Error("manager", "Unhandled application exception", ex);
        }
        catch
        {
            // ignore
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            BridgeLog.Error("manager", "Unobserved task exception", e.Exception);
        }
        catch
        {
            // ignore
        }

        e.SetObserved();
    }
}
