using System.Windows.Input;

namespace RainExplorer.Helpers;

/// <summary>Minimal ICommand for binding buttons/shortcuts to ViewModel methods.</summary>
/// <remarks>
/// Avalonia has no WPF-style CommandManager, so commands re-query their CanExecute
/// state when <see cref="InvalidateRequerySuggested"/> is called (after navigation,
/// selection and undo changes).
/// </remarks>
public sealed class RelayCommand : ICommand
{
    private static event EventHandler? RequerySuggested;

    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => RequerySuggested += value;
        remove => RequerySuggested -= value;
    }

    /// <summary>Ask every bound command to re-evaluate whether it can execute.</summary>
    public static void InvalidateRequerySuggested() => RequerySuggested?.Invoke(null, EventArgs.Empty);
}
