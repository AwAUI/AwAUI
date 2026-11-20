using System;
using System.Globalization;

namespace AwAUI.Controls.Primitives;

readonly struct SliderScale
{
    readonly double _length;
    readonly int _decimalPlaces;

    public SliderScale(double min, double max, double step)
    {
        if (!IsFinite(min)) throw new ArgumentOutOfRangeException(nameof(Min), "Min must be finite.");
        _length = max - min;
        if (!IsFinite(_length) || _length < 0)
            throw new ArgumentOutOfRangeException(nameof(Max), "Max must be at least Min, with a finite range.");
        if (!IsFinite(step) || step <= 0) throw new ArgumentOutOfRangeException(nameof(Step), "Step must be finite and positive.");

        Min = min;
        Max = max;
        Step = step;
        _decimalPlaces = Math.Max(DecimalPlaces(min), DecimalPlaces(step));
    }

    public double Min { get; }
    public double Max { get; }
    double Step { get; }

    public double Clamp(double value) => double.IsNaN(value) ? Min : Math.Max(Min, Math.Min(Max, value));

    public double Fraction(double value) => _length == 0 ? 0 : (Clamp(value) - Min) / _length;

    public double AtFraction(double fraction)
    {
        if (fraction <= 0 || _length == 0) return Min;
        if (fraction >= 1) return Max;

        double offset = fraction * _length;
        double value = Clamp(Min + offset);
        double index = offset / Step;
        if (!IsFinite(index)) return value;
        double tick = Clamp(Tick(Math.Round(index, MidpointRounding.AwayFromZero)));
        // Max may lie between two ticks; include it as a reachable endpoint.
        return Max - value <= Math.Abs(tick - value) ? Max : tick;
    }

    public double StepFrom(double value, int direction)
    {
        value = Clamp(value);
        if (direction < 0 ? value == Min : value == Max) return value;

        double index = (value - Min) / Step;
        if (!IsFinite(index)) return Clamp(value + direction * Step);
        double nearest = Math.Round(index);
        double tick = Tick(nearest);
        if (direction < 0 ? tick >= value : tick <= value) nearest += direction;
        return Clamp(Tick(nearest));
    }

    double Tick(double index)
    {
        if (index <= 0) return Min;
        double value = Min + index * Step;
        // Keep common decimal steps stable (e.g. 3 × 0.1 is reported as 0.3).
        return _decimalPlaces <= 15 ? Math.Round(value, _decimalPlaces) : value;
    }

    static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    static int DecimalPlaces(double value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        int exponentStart = text.IndexOf('E');
        int end = exponentStart < 0 ? text.Length : exponentStart;
        int point = text.IndexOf('.');
        int places = point < 0 ? 0 : end - point - 1;
        return exponentStart < 0 ? places : places - int.Parse(text.Substring(exponentStart + 1), CultureInfo.InvariantCulture);
    }
}
