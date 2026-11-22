using System;
using System.Collections.Generic;
using System.Globalization;

namespace AwAUI.Controls.DataGrid;

/// <summary>How a <see cref="DataGridLength"/> is interpreted.</summary>
public enum DataGridLengthUnitType
{
    /// <summary>The value is derived from the widest header or cell content.</summary>
    Auto = 0,

    /// <summary>The value is a fixed number of logical pixels.</summary>
    Pixel = 1,

    /// <summary>The value is a weight that shares the space left after fixed and auto columns.</summary>
    Star = 2,
}

/// <summary>
/// A column width: fixed pixels, a share of the remaining space (star), or content-sized.
/// Implicitly convertible from a pixel <see cref="double"/>.
/// </summary>
public readonly struct DataGridLength : IEquatable<DataGridLength>
{
    /// <summary>Creates a fixed pixel length.</summary>
    public DataGridLength(double value) : this(value, DataGridLengthUnitType.Pixel)
    {
    }

    /// <summary>Creates a length with the given unit.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is NaN, infinite, or negative.</exception>
    public DataGridLength(double value, DataGridLengthUnitType unitType)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "A column length must be a finite, non-negative number.");
        Value = unitType == DataGridLengthUnitType.Auto ? 0 : value;
        UnitType = unitType;
    }

    /// <summary>The pixel width or the star weight; zero for <see cref="DataGridLengthUnitType.Auto"/>.</summary>
    public double Value { get; }

    /// <summary>The unit of <see cref="Value"/>.</summary>
    public DataGridLengthUnitType UnitType { get; }

    /// <summary>The width is derived from content.</summary>
    public bool IsAuto => UnitType == DataGridLengthUnitType.Auto;

    /// <summary>The width is a fixed pixel value.</summary>
    public bool IsAbsolute => UnitType == DataGridLengthUnitType.Pixel;

    /// <summary>The width is a share of the remaining space.</summary>
    public bool IsStar => UnitType == DataGridLengthUnitType.Star;

    /// <summary>A content-sized length.</summary>
    public static DataGridLength Auto => new DataGridLength(0, DataGridLengthUnitType.Auto);

    /// <summary>A star length with the given weight (1 by default).</summary>
    public static DataGridLength Star(double value = 1) => new DataGridLength(value, DataGridLengthUnitType.Star);

    /// <summary>A fixed pixel length.</summary>
    public static DataGridLength Pixel(double value) => new DataGridLength(value, DataGridLengthUnitType.Pixel);

    /// <summary>A fixed pixel length.</summary>
    public static implicit operator DataGridLength(double value) => new DataGridLength(value);

    /// <inheritdoc />
    public bool Equals(DataGridLength other) => UnitType == other.UnitType && Value.Equals(other.Value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DataGridLength other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (int)UnitType * 397 ^ Value.GetHashCode();

    /// <summary>Operators compare both the unit and the value.</summary>
    public static bool operator ==(DataGridLength left, DataGridLength right) => left.Equals(right);

    /// <summary>Operators compare both the unit and the value.</summary>
    public static bool operator !=(DataGridLength left, DataGridLength right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
    {
        switch (UnitType)
        {
            case DataGridLengthUnitType.Auto: return "Auto";
            case DataGridLengthUnitType.Star: return Value.ToString("0.#########", CultureInfo.InvariantCulture) + "*";
            default: return Value.ToString("0.#########", CultureInfo.InvariantCulture) + "px";
        }
    }
}

/// <summary>Pure column-width allocation (exported to tests through InternalsVisibleTo).</summary>
internal static class DataGridColumnWidths
{
    /// <summary>Width used for a one-star column before the viewport has been measured.</summary>
    internal const double UnmeasuredStarWidth = 120;

    /// <summary>
    /// Allocates widths for <paramref name="lengths"/> given the natural widths of auto columns and the available
    /// viewport width. Fixed columns keep their value, auto columns use <paramref name="autoWidths"/>, and star
    /// columns split whatever remains in proportion to their weights. A non-positive available width means the
    /// viewport has not been measured yet; star columns then fall back to <see cref="UnmeasuredStarWidth"/>.
    /// </summary>
    internal static double[] Allocate(IReadOnlyList<DataGridLength> lengths, IReadOnlyList<double> autoWidths, double availableWidth)
    {
        int count = lengths.Count;
        var widths = new double[count];
        double fixedSum = 0;
        double autoSum = 0;
        double starWeight = 0;
        for (int i = 0; i < count; i++)
        {
            var length = lengths[i];
            if (length.IsStar)
            {
                starWeight += length.Value;
            }
            else if (length.IsAuto)
            {
                double auto = i < autoWidths.Count ? autoWidths[i] : 0;
                if (double.IsNaN(auto) || double.IsInfinity(auto) || auto < 0) auto = 0;
                widths[i] = auto;
                autoSum += auto;
            }
            else
            {
                widths[i] = length.Value;
                fixedSum += length.Value;
            }
        }

        if (starWeight <= 0) return widths;
        if (availableWidth <= 0)
        {
            for (int i = 0; i < count; i++)
                if (lengths[i].IsStar) widths[i] = UnmeasuredStarWidth * lengths[i].Value;
            return widths;
        }

        double remaining = availableWidth - fixedSum - autoSum;
        if (remaining < 0) remaining = 0;
        for (int i = 0; i < count; i++)
            if (lengths[i].IsStar) widths[i] = remaining * lengths[i].Value / starWeight;
        return widths;
    }
}
