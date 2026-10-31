using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;
using CookieBridge.Manager.Services;
using CookieBridge.Manager.ViewModels;

namespace CookieBridge.Manager;

/// <summary>
/// 主窗口。code-behind 只做三件事：装配 ViewModel、定时静默刷新、以及少量窗口级杂事
/// （打开链接、外部异常提示）。业务动作全部在 ViewModel / Services 里。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _autoRefresh;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(new WindowDialogService(this));
        DataContext = _viewModel;

        _autoRefresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            // 插件心跳 30 秒一次、Steam 状态随时会变，5 秒静默刷新一次足够，且不会打断用户操作。
            Interval = TimeSpan.FromSeconds(5),
        };
        _autoRefresh.Tick += OnAutoRefreshTick;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
            _autoRefresh.Start();

            // 启动期的致命问题（例如根目录不可写）必须显式告知，不能静默。
            if (!string.IsNullOrEmpty(App.StartupError))
                ShowError(App.StartupError!);
        }
        catch (Exception ex)
        {
            ShowError($"初始化失败：{ex.GetType().Name} - {ex.Message}");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            _autoRefresh.Stop();
        }
        catch
        {
            // ignore
        }
    }

    private async void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        try
        {
            // 静默刷新：不覆盖状态栏里的操作结果。
            await _viewModel.RefreshAsync(true);
        }
        catch
        {
            // RefreshAsync 内部已经处理异常，这里只防御 DispatcherTimer 的边界情况。
        }
    }

    /// <summary>App 的全局异常兜底会调这里，把错误显示在状态栏而不是崩窗口。</summary>
    public void ShowError(string message)
    {
        try
        {
            _viewModel.SetStatus(message, true);
        }
        catch
        {
            // 连状态栏都设置失败时，退化成标题提示，绝不重抛。
            try { Title = "VRCVideoCacher CookieBridge 管理器 — " + message; } catch { /* ignore */ }
        }
    }

    private void OnHyperlinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowError($"打开链接失败：{ex.GetType().Name}");
        }

        e.Handled = true;
    }
}
