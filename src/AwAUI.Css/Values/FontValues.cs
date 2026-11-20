using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>通用字体族。</summary>
public enum GenericFontFamily : byte
{
    /// <summary>不是通用族，而是具体字体名。</summary>
    None,
    Serif,
    SansSerif,
    Monospace,
    Cursive,
    Fantasy,
    SystemUI,
    UISerif,
    UISansSerif,
    UIMonospace,
    UIRounded,
    Emoji,
    Math,
}

/// <summary>字体族列表中的一项。</summary>
public readonly struct FontFamilyName : IEquatable<FontFamilyName>
{
    public FontFamilyName(string name, GenericFontFamily generic)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Generic = generic;
    }

    /// <summary>字体名；通用族为其关键字（如 <c>sans-serif</c>）。</summary>
    public string Name { get; }

    public GenericFontFamily Generic { get; }

    public bool IsGeneric => Generic != GenericFontFamily.None;

    public bool Equals(FontFamilyName other) => Generic == other.Generic && string.Equals(Name, other.Name, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is FontFamilyName n && Equals(n);
    public override int GetHashCode() => HashCodeCompat.Combine(Name.GetHashCode(), (int)Generic);

    public override string ToString() => IsGeneric ? Name : CssSerializer.SerializeString(Name);

    internal static GenericFontFamily ParseGeneric(string lower) => lower switch
    {
        "serif" => GenericFontFamily.Serif,
        "sans-serif" => GenericFontFamily.SansSerif,
        "monospace" => GenericFontFamily.Monospace,
        "cursive" => GenericFontFamily.Cursive,
        "fantasy" => GenericFontFamily.Fantasy,
        "system-ui" => GenericFontFamily.SystemUI,
        "ui-serif" => GenericFontFamily.UISerif,
        "ui-sans-serif" => GenericFontFamily.UISansSerif,
        "ui-monospace" => GenericFontFamily.UIMonospace,
        "ui-rounded" => GenericFontFamily.UIRounded,
        "emoji" => GenericFontFamily.Emoji,
        "math" => GenericFontFamily.Math,
        _ => GenericFontFamily.None,
    };
}

/// <summary><c>font-family</c> 计算值：按优先级排列的字体族。</summary>
public sealed class FontFamilyList : IEquatable<FontFamilyList>
{
    public FontFamilyList(IReadOnlyList<FontFamilyName> families)
    {
        if (families is null || families.Count == 0) throw new ArgumentException("至少需要一个字体族。", nameof(families));
        Families = families;
    }

    public IReadOnlyList<FontFamilyName> Families { get; }

    public bool Equals(FontFamilyList? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || other.Families.Count != Families.Count) return false;
        for (int i = 0; i < Families.Count; i++)
            if (!Families[i].Equals(other.Families[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is FontFamilyList l && Equals(l);

    public override int GetHashCode()
    {
        int h = Families.Count;
        foreach (var f in Families) h = HashCodeCompat.Combine(h, f.GetHashCode());
        return h;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Families.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Families[i].ToString());
        }
        return sb.ToString();
    }
}

/// <summary><see cref="LineHeight"/> 的种类。</summary>
public enum LineHeightKind : byte
{
    Normal,
    /// <summary>无单位数：子元素继承的是数本身，按各自字体大小相乘。</summary>
    Number,
    /// <summary>长度（px；百分比在计算值阶段已乘以字体大小）。</summary>
    Length,
}

/// <summary><c>line-height</c> 计算值：<c>normal</c>、数（保留为数）或长度（px）。</summary>
public readonly struct LineHeight : IEquatable<LineHeight>
{
    LineHeight(LineHeightKind kind, float value)
    {
        Kind = kind;
        Value = value;
    }

    public static LineHeight Normal => default;

    public static LineHeight FromNumber(float number) => new(LineHeightKind.Number, number);

    public static LineHeight FromPx(float px) => new(LineHeightKind.Length, px);

    public LineHeightKind Kind { get; }

    /// <summary>数或 px。</summary>
    public float Value { get; }

    /// <summary>按字体大小得到行高（px）；<c>normal</c> 返回 <paramref name="normalFactor"/> × 字体大小（实际应取字体度量）。</summary>
    public float Resolve(float fontSize, float normalFactor = 1.2f) => Kind switch
    {
        LineHeightKind.Number => Value * fontSize,
        LineHeightKind.Length => Value,
        _ => normalFactor * fontSize,
    };

    public bool Equals(LineHeight other) => Kind == other.Kind && Value.Equals(other.Value);
    public override bool Equals(object? obj) => obj is LineHeight l && Equals(l);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Value.GetHashCode());
    public static bool operator ==(LineHeight a, LineHeight b) => a.Equals(b);
    public static bool operator !=(LineHeight a, LineHeight b) => !a.Equals(b);

    public override string ToString() => Kind switch
    {
        LineHeightKind.Number => ValueText.Number(Value),
        LineHeightKind.Length => ValueText.Px(Value),
        _ => "normal",
    };
}

/// <summary>计算值的文本格式化（DevTools 的 getComputedStyle）。</summary>
public static class ValueText
{
    /// <summary>把 float 以最多 4 位小数格式化。</summary>
    public static string Number(float v)
    {
        double d = Math.Round((double)v, 4);
        return CssSerializer.FormatNumber(d == 0 ? 0 : d);
    }

    public static string Px(float v) => Number(v) + "px";

    public static string Percent(float v) => Number(v) + "%";

    public static string Integer(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>时间（毫秒）按秒序列化（Chrome 的 getComputedStyle 写法，如 <c>0.15s</c>）；-1 为 <c>auto</c>。</summary>
    public static string Time(float ms) => ms == -1 ? "auto" : Number(ms / 1000) + "s";
}
