using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using AwAUI.UI;

namespace AwAUI.Mvvm;

/// <summary>
/// Bridges an <see cref="INotifyPropertyChanged"/> property path to an AwAUI <see cref="State{T}"/>.
/// Source changes are mirrored into <see cref="State"/>; assigning <see cref="Value"/> writes the source.
/// Dispose the adapter to stop observing.
/// </summary>
/// <typeparam name="T">The property value type.</typeparam>
public sealed class ObservableStateAdapter<T> : IDisposable
{
    readonly INotifyPropertyChanged _source;
    readonly PropertyPath _path;
    readonly PropertyPathObserver _observer;
    readonly State<T> _state;
    bool _disposed;

    /// <summary>Creates an adapter and initializes the state from the source.</summary>
    /// <param name="source">The source object.</param>
    /// <param name="propertyPath">The source property path.</param>
    public ObservableStateAdapter(INotifyPropertyChanged source, string propertyPath)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _path = PropertyPath.Parse(propertyPath);
        _state = new State<T>(ReadValue());
        _observer = new PropertyPathObserver(source, _path, OnSourceChanged);
    }

    /// <summary>The state mirrored from the source.</summary>
    public State<T> State => _state;

    /// <summary>
    /// Gets or sets the value. Reading returns the mirrored state; assigning updates the state and writes the
    /// source property.
    /// </summary>
    public T Value
    {
        get => _state.Value;
        set
        {
            _state.Value = value;
            _path.TrySetValue(_source, value);
        }
    }

    /// <summary>Writes a value to both the state and the source.</summary>
    /// <param name="value">The new value.</param>
    public void SetValue(T value) => Value = value;

    /// <summary>Re-reads the source and updates the state.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        _state.Value = ReadValue();
    }

    T ReadValue()
    {
        if (!_path.TryGetValue(_source, out object? value) || value is null) return default!;
        if (value is T typed) return typed;
        try
        {
            return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return default!;
        }
    }

    void OnSourceChanged()
    {
        if (!_disposed) _state.Value = ReadValue();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _observer.Dispose();
    }
}

/// <summary>Creates <see cref="ObservableStateAdapter{T}"/> and bridges to <see cref="State{T}"/> / <see cref="Computed{T}"/>.</summary>
public static class ObservableStateAdapter
{
    /// <summary>Creates a disposable adapter for a source property path.</summary>
    /// <typeparam name="T">The property value type.</typeparam>
    /// <param name="source">The source object.</param>
    /// <param name="propertyPath">The source property path.</param>
    /// <returns>The adapter; dispose it to stop observing.</returns>
    public static ObservableStateAdapter<T> For<T>(INotifyPropertyChanged source, string propertyPath)
        => new(source, propertyPath);

    /// <summary>
    /// Creates a <see cref="State{T}"/> mirrored from a source property. The subscription lives as long as the
    /// source object; use <see cref="For{T}"/> when explicit disposal is required.
    /// </summary>
    /// <typeparam name="T">The property value type.</typeparam>
    /// <param name="source">The source object.</param>
    /// <param name="propertyPath">The source property path.</param>
    /// <returns>The mirrored state.</returns>
    public static State<T> ToState<T>(INotifyPropertyChanged source, string propertyPath)
    {
        var adapter = new ObservableStateAdapter<T>(source, propertyPath);
        StateKeepAlive<T>.Table.Add(adapter.State, adapter);
        return adapter.State;
    }

    /// <summary>
    /// Creates a <see cref="Computed{T}"/> that re-evaluates a source property whenever the source raises
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/>. The subscription lives as long as the computed value.
    /// </summary>
    /// <typeparam name="T">The property value type.</typeparam>
    /// <param name="source">The source object.</param>
    /// <param name="propertyPath">The source property path.</param>
    /// <returns>The computed value.</returns>
    public static Computed<T> ToComputed<T>(INotifyPropertyChanged source, string propertyPath)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        var path = PropertyPath.Parse(propertyPath);
        var version = new State<int>(0);
        var observer = new PropertyPathObserver(source, path, () => version.Value++);
        var computed = new Computed<T>(() =>
        {
            _ = version.Value;
            return ReadPath<T>(source, path);
        });
        ComputedKeepAlive<T>.Table.Add(computed, observer);
        return computed;
    }

    static T ReadPath<T>(object source, PropertyPath path)
    {
        if (!path.TryGetValue(source, out object? value) || value is null) return default!;
        if (value is T typed) return typed;
        try
        {
            return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return default!;
        }
    }

    static class StateKeepAlive<T>
    {
        public static readonly ConditionalWeakTable<State<T>, ObservableStateAdapter<T>> Table = new();
    }

    static class ComputedKeepAlive<T>
    {
        public static readonly ConditionalWeakTable<Computed<T>, PropertyPathObserver> Table = new();
    }
}
