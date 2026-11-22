using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AwAUI.Mvvm;

/// <summary>
/// Base class for view models that implement <see cref="INotifyPropertyChanged"/>.
/// </summary>
public class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Assigns <paramref name="value"/> to <paramref name="field"/> and raises <see cref="PropertyChanged"/>
    /// when the value differs from the current field value.
    /// </summary>
    /// <typeparam name="T">The property type.</typeparam>
    /// <param name="field">The backing field.</param>
    /// <param name="value">The new value.</param>
    /// <param name="propertyName">The property name; supplied automatically by the compiler.</param>
    /// <returns><c>true</c> when the value changed and the notification was raised.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        => SetProperty(ref field, value, EqualityComparer<T>.Default, propertyName);

    /// <summary>
    /// Assigns <paramref name="value"/> to <paramref name="field"/> using a custom comparer and raises
    /// <see cref="PropertyChanged"/> when the value differs from the current field value.
    /// </summary>
    /// <typeparam name="T">The property type.</typeparam>
    /// <param name="field">The backing field.</param>
    /// <param name="value">The new value.</param>
    /// <param name="comparer">The comparer used to detect changes; <c>null</c> means <see cref="EqualityComparer{T}.Default"/>.</param>
    /// <param name="propertyName">The property name; supplied automatically by the compiler.</param>
    /// <returns><c>true</c> when the value changed and the notification was raised.</returns>
    protected bool SetProperty<T>(ref T field, T value, IEqualityComparer<T>? comparer, [CallerMemberName] string? propertyName = null)
    {
        if ((comparer ?? EqualityComparer<T>.Default).Equals(field, value)) return false;
        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for the given property.</summary>
    /// <param name="propertyName">The property name; supplied automatically by the compiler.</param>
    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
        => OnPropertyChanged(new PropertyChangedEventArgs(propertyName));

    /// <summary>Raises <see cref="PropertyChanged"/> for several properties in one call.</summary>
    /// <param name="propertyNames">The property names to notify.</param>
    protected void RaisePropertyChanged(params string[] propertyNames)
    {
        if (propertyNames is null) throw new ArgumentNullException(nameof(propertyNames));
        foreach (var name in propertyNames) RaisePropertyChanged(name);
    }

    /// <summary>Raises <see cref="PropertyChanged"/> with a pre-built event argument.</summary>
    /// <param name="e">The event argument.</param>
    protected virtual void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e is null) throw new ArgumentNullException(nameof(e));
        PropertyChanged?.Invoke(this, e);
    }
}
