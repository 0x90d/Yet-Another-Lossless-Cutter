using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace YetAnotherLosslessCutter;

/// <summary>
/// Minimal INPC base. Avalonia bindings just need PropertyChanged; the WPF version's
/// IDataErrorInfo plumbing isn't ported — Avalonia uses DataValidationErrors instead.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
