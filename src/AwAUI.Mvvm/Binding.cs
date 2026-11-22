using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using AwAUI.UI;

namespace AwAUI.Mvvm;

/// <summary>How a <see cref="Binding"/> flows between its source and target.</summary>
public enum BindingMode
{
    /// <summary>Source changes update the target; target changes are ignored.</summary>
    OneWay,

    /// <summary>Source and target stay in sync in both directions.</summary>
    TwoWay,

    /// <summary>The target is initialized once from the source and is not updated afterwards.</summary>
    OneTime,
}

/// <summary>
/// A binding from an <see cref="INotifyPropertyChanged"/> source property path to a target.
/// Create an active expression with <see cref="Attach"/>.
/// </summary>
public sealed class Binding
{
    /// <summary>Creates a binding from a property path.</summary>
    /// <param name="path">The source path, for example <c>Address.City</c> or <c>Items[0].Name</c>.</param>
    public Binding(string path) : this(PropertyPath.Parse(path)) { }

    /// <summary>Creates a binding from a parsed property path.</summary>
    /// <param name="path">The source path.</param>
    public Binding(PropertyPath path) => Path = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>The source property path.</summary>
    public PropertyPath Path { get; }

    /// <summary>The binding mode; defaults to <see cref="BindingMode.OneWay"/>.</summary>
    public BindingMode Mode { get; set; } = BindingMode.OneWay;

    /// <summary>The optional value converter.</summary>
    public IValueConverter? Converter { get; set; }

    /// <summary>The converter parameter.</summary>
    public object? ConverterParameter { get; set; }

    /// <summary>The culture used by the converter and <see cref="StringFormat"/>; defaults to the current culture.</summary>
    public CultureInfo? ConverterCulture { get; set; }

    /// <summary>An optional format applied after conversion, for example <c>N2</c> or <c>{0} items</c>.</summary>
    public string? StringFormat { get; set; }

    /// <summary>
    /// The value written to the target when the source path cannot be resolved. Defaults to <see cref="BindingValue.Unset"/>,
    /// which is treated as <c>null</c>.
    /// </summary>
    public object? FallbackValue { get; set; } = BindingValue.Unset;

    /// <summary>
    /// The value written to the target when the converted value is <c>null</c>. Defaults to <see cref="BindingValue.Unset"/>,
    /// which leaves <c>null</c> unchanged.
    /// </summary>
    public object? TargetNullValue { get; set; } = BindingValue.Unset;

    /// <summary>Creates and starts a binding expression.</summary>
    /// <param name="source">The source object.</param>
    /// <param name="target">The target adapter.</param>
    /// <returns>The active expression; dispose it to stop observing.</returns>
    public BindingExpression Attach(object source, IBindingTarget target) => new(this, source, target);

    /// <summary>Creates and starts a binding expression for an <see cref="INotifyPropertyChanged"/> target property.</summary>
    /// <param name="source">The source object.</param>
    /// <param name="target">The target object.</param>
    /// <param name="targetPath">The target property path.</param>
    /// <returns>The active expression; dispose it to stop observing.</returns>
    public BindingExpression AttachTo(object source, INotifyPropertyChanged target, string targetPath)
        => new(this, source, new PropertyBindingTarget(target, targetPath));

    /// <summary>Creates and starts a binding expression for a <see cref="State{T}"/> target.</summary>
    /// <typeparam name="T">The state value type.</typeparam>
    /// <param name="source">The source object.</param>
    /// <param name="target">The target state.</param>
    /// <returns>The active expression; dispose it to stop observing.</returns>
    public BindingExpression AttachTo<T>(object source, State<T> target)
        => new(this, source, new StateBindingTarget<T>(target));
}

/// <summary>
/// The target side of a <see cref="BindingExpression"/>: a readable and writable value that can notify changes.
/// </summary>
public interface IBindingTarget
{
    /// <summary>The target value type, when known.</summary>
    Type? TargetType { get; }

    /// <summary>Reads the current target value.</summary>
    /// <returns>The target value.</returns>
    object? GetValue();

    /// <summary>Writes the target value.</summary>
    /// <param name="value">The new value.</param>
    void SetValue(object? value);

    /// <summary>Subscribes to target changes; returns <c>null</c> when the target is read-only.</summary>
    /// <param name="onChanged">Invoked whenever the target value changes.</param>
    /// <returns>A subscription to dispose, or <c>null</c>.</returns>
    IDisposable? Subscribe(Action<object?> onChanged);
}

/// <summary>An <see cref="IBindingTarget"/> backed by an AwAUI <see cref="State{T}"/>.</summary>
/// <typeparam name="T">The state value type.</typeparam>
public sealed class StateBindingTarget<T> : IBindingTarget, IDisposable
{
    readonly State<T> _state;
    IDisposable? _subscription;

    /// <summary>Creates a target for a state.</summary>
    /// <param name="state">The target state.</param>
    public StateBindingTarget(State<T> state) => _state = state ?? throw new ArgumentNullException(nameof(state));

    /// <summary>The wrapped state.</summary>
    public State<T> State => _state;

    /// <inheritdoc />
    public Type? TargetType => typeof(T);

    /// <inheritdoc />
    public object? GetValue() => _state.Peek();

    /// <inheritdoc />
    public void SetValue(object? value)
    {
        if (value is null)
        {
            _state.Value = default!;
            return;
        }
        if (value is T typed)
        {
            _state.Value = typed;
            return;
        }
        _state.Value = (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public IDisposable? Subscribe(Action<object?> onChanged)
    {
        _subscription?.Dispose();
        _subscription = Reactive.Effect(() => onChanged(_state.Value));
        return _subscription;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}

/// <summary>An <see cref="IBindingTarget"/> backed by a property path on an <see cref="INotifyPropertyChanged"/> object.</summary>
public sealed class PropertyBindingTarget : IBindingTarget, IDisposable
{
    readonly INotifyPropertyChanged _target;
    readonly PropertyPath _path;
    PropertyPathObserver? _observer;

    /// <summary>Creates a target for a property path on an object.</summary>
    /// <param name="target">The target object.</param>
    /// <param name="propertyPath">The target property path.</param>
    public PropertyBindingTarget(INotifyPropertyChanged target, string propertyPath)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _path = PropertyPath.Parse(propertyPath);
    }

    /// <inheritdoc />
    public Type? TargetType => null;

    /// <inheritdoc />
    public object? GetValue() => _path.TryGetValue(_target, out object? value) ? value : null;

    /// <inheritdoc />
    public void SetValue(object? value)
    {
        if (!_path.TrySetValue(_target, value))
            throw new InvalidOperationException($"Cannot write target property path \"{_path.Path}\".");
    }

    /// <inheritdoc />
    public IDisposable? Subscribe(Action<object?> onChanged)
    {
        _observer?.Dispose();
        _observer = new PropertyPathObserver(_target, _path, () => onChanged(GetValue()));
        return _observer;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _observer?.Dispose();
        _observer = null;
    }
}

/// <summary>
/// A live binding between a source property path and a target. Source changes update the target;
/// in <see cref="BindingMode.TwoWay"/> target changes are written back to the source without feedback loops.
/// </summary>
public sealed class BindingExpression : IDisposable
{
    readonly object _source;
    readonly IBindingTarget _target;
    readonly PropertyPathObserver _observer;
    IDisposable? _targetSubscription;
    bool _isUpdating;
    bool _isInitializing;
    bool _disposed;

    /// <summary>Creates and starts a binding expression.</summary>
    /// <param name="binding">The binding description.</param>
    /// <param name="source">The source object.</param>
    /// <param name="target">The target adapter.</param>
    public BindingExpression(Binding binding, object source, IBindingTarget target)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _target = target ?? throw new ArgumentNullException(nameof(target));

        _observer = new PropertyPathObserver(source, binding.Path, OnSourceChanged);
        _isInitializing = true;
        try
        {
            UpdateTarget();
            if (binding.Mode == BindingMode.TwoWay)
                _targetSubscription = target.Subscribe(OnTargetChanged);
        }
        finally
        {
            _isInitializing = false;
        }
    }

    /// <summary>The binding description.</summary>
    public Binding Binding { get; }

    /// <summary>The most recent value written to the target.</summary>
    public object? Value { get; private set; }

    /// <summary>Whether an update is currently in progress (usable to ignore re-entrant notifications).</summary>
    public bool IsUpdating => _isUpdating;

    /// <summary>Re-evaluates the source and writes the result to the target.</summary>
    public void UpdateTarget()
    {
        if (_disposed) return;
        _isUpdating = true;
        try
        {
            object? value;
            if (!Binding.Path.TryGetValue(_source, out value))
                value = Binding.FallbackValue;
            else
                value = ConvertForward(value);

            if (ReferenceEquals(value, BindingValue.Unset))
                value = ReferenceEquals(Binding.FallbackValue, BindingValue.Unset) ? null : Binding.FallbackValue;

            if (value is null && !ReferenceEquals(Binding.TargetNullValue, BindingValue.Unset))
                value = Binding.TargetNullValue;

            Value = value;
            _target.SetValue(value);
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>Reads the target and, in <see cref="BindingMode.TwoWay"/>, writes it back to the source.</summary>
    public void UpdateSource()
    {
        if (_disposed || Binding.Mode != BindingMode.TwoWay) return;
        _isUpdating = true;
        try
        {
            object? value = _target.GetValue();
            if (Binding.Converter is not null)
            {
                value = Binding.Converter.ConvertBack(
                    value, typeof(object), Binding.ConverterParameter, Binding.ConverterCulture ?? CultureInfo.CurrentCulture);
                if (ReferenceEquals(value, BindingValue.Unset)) return;
            }

            if (Binding.Path.TryGetValue(_source, out object? current) && Equals(current, value)) return;
            Binding.Path.TrySetValue(_source, value);
        }
        finally
        {
            _isUpdating = false;
        }
    }

    void OnSourceChanged()
    {
        if (_disposed || _isUpdating || _isInitializing) return;
        if (Binding.Mode == BindingMode.OneTime) return;
        UpdateTarget();
    }

    void OnTargetChanged(object? value)
    {
        if (_disposed || _isUpdating || _isInitializing || Binding.Mode != BindingMode.TwoWay) return;
        UpdateSource();
    }

    object? ConvertForward(object? value)
    {
        var culture = Binding.ConverterCulture ?? CultureInfo.CurrentCulture;
        if (Binding.Converter is not null)
        {
            value = Binding.Converter.Convert(value, _target.TargetType ?? typeof(object), Binding.ConverterParameter, culture);
            if (ReferenceEquals(value, BindingValue.Unset))
                value = ReferenceEquals(Binding.FallbackValue, BindingValue.Unset) ? null : Binding.FallbackValue;
        }

        if (Binding.StringFormat is { Length: > 0 } format && value is not null)
        {
            value = format.IndexOf("{0", StringComparison.Ordinal) >= 0
                ? string.Format(culture, format, value)
                : string.Format(culture, "{0:" + format + "}", value);
        }

        return value;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _observer.Dispose();
        _targetSubscription?.Dispose();
        _targetSubscription = null;
    }
}

/// <summary>
/// Subscribes to <see cref="INotifyPropertyChanged"/> along every object of a property path and rewires the
/// subscriptions whenever an intermediate object is replaced.
/// </summary>
internal sealed class PropertyPathObserver : IDisposable
{
    readonly object _root;
    readonly PropertyPath _path;
    readonly Action _onChanged;
    readonly List<Subscription> _subscriptions = new();
    bool _disposed;
    bool _rewiring;
    bool _pending;

    public PropertyPathObserver(object root, PropertyPath path, Action onChanged)
    {
        _root = root;
        _path = path;
        _onChanged = onChanged;
        Rewire();
    }

    void Rewire()
    {
        if (_disposed || _rewiring) return;
        _rewiring = true;
        try
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();

            object? current = _root;
            foreach (var segment in _path.Segments)
            {
                if (current is null) break;
                if (segment.Name.Length > 0 && current is INotifyPropertyChanged notifier)
                {
                    string name = segment.Name;
                    PropertyChangedEventHandler handler = (sender, e) =>
                    {
                        if (string.IsNullOrEmpty(e.PropertyName) || string.Equals(e.PropertyName, name, StringComparison.Ordinal))
                            OnChanged();
                    };
                    notifier.PropertyChanged += handler;
                    _subscriptions.Add(new Subscription(notifier, handler));
                }
                if (!segment.TryGetValue(current, out object? next)) break;
                current = next;
            }
        }
        finally
        {
            _rewiring = false;
        }

        if (_pending)
        {
            _pending = false;
            OnChanged();
        }
    }

    void OnChanged()
    {
        if (_disposed) return;
        if (_rewiring)
        {
            _pending = true;
            return;
        }
        Rewire();
        _onChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }

    sealed class Subscription : IDisposable
    {
        INotifyPropertyChanged? _notifier;
        PropertyChangedEventHandler? _handler;

        public Subscription(INotifyPropertyChanged notifier, PropertyChangedEventHandler handler)
        {
            _notifier = notifier;
            _handler = handler;
        }

        public void Dispose()
        {
            var notifier = _notifier;
            var handler = _handler;
            _notifier = null;
            _handler = null;
            if (notifier is not null && handler is not null) notifier.PropertyChanged -= handler;
        }
    }
}
