using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CookieBridge.Manager.ViewModels;

/// <summary>
/// 手写的最小 MVVM 基类（不引第三方框架）。只做一件事：属性变更通知。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>赋值并在值变化时通知，返回是否真的变了（用于决定是否刷新其它派生字段）。</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void Raise(params string[] propertyNames)
    {
        foreach (var name in propertyNames) OnPropertyChanged(name);
    }
}

/// <summary>同步命令。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 异步命令：执行期间自动禁用按钮，异常一律吞进 UI 状态栏（绝不让 UI 线程炸掉）。
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _running;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 命令体内部通常已经 try/catch；这里是最后一道保险。
            ReportFailure?.Invoke($"操作失败：{ex.GetType().Name} - {ex.Message}");
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>由宿主注入的错误回调（状态栏）。</summary>
    public static Action<string>? ReportFailure { get; set; }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
