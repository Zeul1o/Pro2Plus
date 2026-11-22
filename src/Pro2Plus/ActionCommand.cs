using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Pro2Plus;

/// <summary>
/// WPF 命令：异步执行期间保持 CanExecute 为 false，并在开始与结束时各刷新一次，
/// 防止用户重复点击造成并发操作。同步回调包装成已完成的 Task。
/// </summary>
public sealed class ActionCommand : ICommand
{
    private readonly Func<object?, Task> execute;
    private readonly Predicate<object?>? canExecute;
    private bool running;

    public ActionCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public ActionCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        : this(parameter =>
        {
            execute(parameter);
            return Task.CompletedTask;
        }, canExecute)
    {
    }

    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        try
        {
            running = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            await execute(parameter);
        }
        finally
        {
            running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CanExecuteChanged;
    public void RefreshCanExecute() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
