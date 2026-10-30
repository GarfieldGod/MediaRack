using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MediaRack.Client.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>无参同步命令（播放控制这类"按一下立刻有反应"的）。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>
    /// 必须是**真事件**：挂了 canExecute 的命令如果不通知，按钮会永远停在初始判定上
    /// （连接按钮"点不动"就是这么来的）。带条件的命令改完状态后调用 <see cref="RaiseCanExecuteChanged"/>。
    /// </summary>
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>带参数的同步命令（列表项上的"连接 / 删除"这类）。</summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;

    public RelayCommand(Action<T> execute) => _execute = execute;

    /// <summary>条件恒定（只看参数类型），不需要订阅。</summary>
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => parameter is T;

    public void Execute(object? parameter)
    {
        if (parameter is T value) _execute(value);
    }
}

/// <summary>
/// 异步命令。执行期间 CanExecute=false（按钮自动禁用），避免连点把探活打成一团。
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

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    /// <summary>给 ViewModel 内部直接 await 用（<see cref="Execute"/> 是 async void，拿不到完成信号）。</summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        await RunAsync().ConfigureAwait(true);
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        await RunAsync().ConfigureAwait(true);
    }

    private async Task RunAsync()
    {
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (Exception e)
        {
            // 命令里的异常不能冒泡到 async void（会直接崩进程），但也不能只写 Trace：
            // GUI 没有控制台，静默吞掉的表现是"按钮按下去什么都不发生"，只能靠翻日志。
            Tools.ClientLog.Write($"[命令异常] {e}");
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
