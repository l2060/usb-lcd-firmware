using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Aurora.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    readonly Action _execute;
    readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncCommand : ICommand
{
    readonly Func<Task> _execute;
    readonly Func<bool>? _canExecute;
    readonly Action<Exception>? _onError;
    bool _running;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        RaiseCanExecuteChanged();
        try { await _execute(); }
        catch (Exception ex) { _onError?.Invoke(ex); }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public enum LogLevel { Info, Good, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelText => Level switch
    {
        LogLevel.Good => "OK",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERR",
        _ => "INFO",
    };
}

public sealed class LogViewModel : ObservableObject
{
    public ObservableCollection<LogEntry> Entries { get; } = new();

    public void Add(LogLevel level, string message)
    {
        Entries.Add(new LogEntry(DateTime.Now, level, message));
        while (Entries.Count > 500) Entries.RemoveAt(0);
    }

    public void Info(string m) => Add(LogLevel.Info, m);
    public void Good(string m) => Add(LogLevel.Good, m);
    public void Warn(string m) => Add(LogLevel.Warn, m);
    public void Error(string m) => Add(LogLevel.Error, m);
    public void Clear() => Entries.Clear();
}
