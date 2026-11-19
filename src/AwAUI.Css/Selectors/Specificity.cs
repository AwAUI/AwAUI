using System;

namespace AwAUI.Css.Selectors;

/// <summary>选择器特异性 (A, B, C)：A = ID，B = 类/属性/伪类，C = 类型/伪元素（Selectors 4 §17）。</summary>
public readonly struct Specificity : IEquatable<Specificity>, IComparable<Specificity>
{
    public Specificity(int ids, int classes, int types)
    {
        Ids = ids;
        Classes = classes;
        Types = types;
    }

    public static Specificity Zero => default;

    /// <summary>A：ID 选择器个数。</summary>
    public int Ids { get; }

    /// <summary>B：类、属性选择器与伪类个数。</summary>
    public int Classes { get; }

    /// <summary>C：类型选择器与伪元素个数。</summary>
    public int Types { get; }

    public static Specificity operator +(Specificity a, Specificity b) => new(a.Ids + b.Ids, a.Classes + b.Classes, a.Types + b.Types);

    public static Specificity Max(Specificity a, Specificity b) => a.CompareTo(b) >= 0 ? a : b;

    public int CompareTo(Specificity other)
    {
        if (Ids != other.Ids) return Ids.CompareTo(other.Ids);
        if (Classes != other.Classes) return Classes.CompareTo(other.Classes);
        return Types.CompareTo(other.Types);
    }

    public static bool operator <(Specificity a, Specificity b) => a.CompareTo(b) < 0;
    public static bool operator >(Specificity a, Specificity b) => a.CompareTo(b) > 0;
    public static bool operator <=(Specificity a, Specificity b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Specificity a, Specificity b) => a.CompareTo(b) >= 0;
    public static bool operator ==(Specificity a, Specificity b) => a.Equals(b);
    public static bool operator !=(Specificity a, Specificity b) => !a.Equals(b);

    public bool Equals(Specificity other) => Ids == other.Ids && Classes == other.Classes && Types == other.Types;
    public override bool Equals(object? obj) => obj is Specificity s && Equals(s);
    public override int GetHashCode() => HashCodeCompat.Combine(Ids, Classes, Types);
    public override string ToString() => FormattableString.Invariant($"({Ids},{Classes},{Types})");
}
