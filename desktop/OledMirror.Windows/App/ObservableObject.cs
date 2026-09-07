using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace OledMirror.Windows.App;

/// <summary>Base de notificacao de mudanca de propriedade.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>Comando simples para binding de botoes.</summary>
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
        // Um erro num handler de botao nunca pode derrubar a aplicacao.
        try { _execute(); }
        catch (Exception ex) { UnhandledError?.Invoke(ex); }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Erros vindos de comandos, para a ViewModel poder registrar no log.</summary>
    public static event Action<Exception>? UnhandledError;
}