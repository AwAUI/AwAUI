using System;
using System.Globalization;

namespace AwAUI.Mvvm;

/// <summary>
/// Converts values between a binding source and its target. The shape mirrors the WPF converter contract
/// without depending on WPF.
/// </summary>
public interface IValueConverter
{
    /// <summary>Converts a source value to a target value.</summary>
    /// <param name="value">The source value.</param>
    /// <param name="targetType">The target type, when known.</param>
    /// <param name="parameter">The converter parameter.</param>
    /// <param name="culture">The culture to use.</param>
    /// <returns>The converted value.</returns>
    object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture);

    /// <summary>Converts a target value back to a source value.</summary>
    /// <param name="value">The target value.</param>
    /// <param name="targetType">The source type, when known.</param>
    /// <param name="parameter">The converter parameter.</param>
    /// <param name="culture">The culture to use.</param>
    /// <returns>The converted value, or <see cref="BindingValue.Unset"/> to leave the source unchanged.</returns>
    object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture);
}

/// <summary>
/// A converter built from two delegates. Missing delegates mean "pass the value through" in that direction.
/// </summary>
/// <typeparam name="TIn">The source value type.</typeparam>
/// <typeparam name="TOut">The target value type.</typeparam>
public sealed class FuncValueConverter<TIn, TOut> : IValueConverter
{
    readonly Func<TIn, TOut>? _convert;
    readonly Func<TOut, TIn>? _convertBack;

    /// <summary>Creates a converter from a forward delegate.</summary>
    /// <param name="convert">Converts a source value to a target value.</param>
    public FuncValueConverter(Func<TIn, TOut> convert) => _convert = convert ?? throw new ArgumentNullException(nameof(convert));

    /// <summary>Creates a converter from forward and backward delegates.</summary>
    /// <param name="convert">Converts a source value to a target value.</param>
    /// <param name="convertBack">Converts a target value back to a source value.</param>
    public FuncValueConverter(Func<TIn, TOut> convert, Func<TOut, TIn> convertBack)
    {
        _convert = convert ?? throw new ArgumentNullException(nameof(convert));
        _convertBack = convertBack ?? throw new ArgumentNullException(nameof(convertBack));
    }

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (_convert is null) return value;
        if (value is null) return _convert(default!);
        if (value is TIn input) return _convert(input);
        return value;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (_convertBack is null) return BindingValue.Unset;
        if (value is null) return _convertBack(default!);
        if (value is TOut output) return _convertBack(output);
        return BindingValue.Unset;
    }
}

/// <summary>Sentinel values shared by bindings.</summary>
public static class BindingValue
{
    /// <summary>
    /// Marks an unavailable value (a failed property path, a missing conversion or a one-way converter).
    /// Bindings substitute <see cref="Binding.FallbackValue"/> for it.
    /// </summary>
    public static readonly object Unset = new UnsetValue();

    sealed class UnsetValue
    {
        public override string ToString() => "(unset)";
    }
}
