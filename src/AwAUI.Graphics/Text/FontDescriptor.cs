using System;

namespace AwAUI.Graphics.Text;

/// <summary>参与字体匹配的属性：族名、字重、风格、字宽（对应 CSS <c>@font-face</c> 的描述符）。</summary>
public readonly struct FontDescriptor : IEquatable<FontDescriptor>
{
    public FontDescriptor(string familyName, FontWeight weight = default, FontStyle style = FontStyle.Normal, FontStretch stretch = FontStretch.Normal)
    {
        _family = familyName ?? throw new ArgumentNullException(nameof(familyName));
        Weight = weight;
        Style = style;
        _stretch = stretch;
    }

    readonly string? _family;
    readonly FontStretch _stretch;

    public string FamilyName => _family ?? string.Empty;
    public FontWeight Weight { get; }
    public FontStyle Style { get; }
    /// <summary>字宽；default 结构体视为 Normal。</summary>
    public FontStretch Stretch => _stretch == 0 ? FontStretch.Normal : _stretch;

    public bool Equals(FontDescriptor other) =>
        string.Equals(FamilyName, other.FamilyName, StringComparison.OrdinalIgnoreCase)
        && Weight == other.Weight && Style == other.Style && Stretch == other.Stretch;

    public override bool Equals(object? obj) => obj is FontDescriptor d && Equals(d);

    public override int GetHashCode() => HashCodeCompat.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(FamilyName), Weight.Value, (int)Style, (int)Stretch);

    public static bool operator ==(FontDescriptor a, FontDescriptor b) => a.Equals(b);
    public static bool operator !=(FontDescriptor a, FontDescriptor b) => !a.Equals(b);

    public override string ToString() => $"{FamilyName} {Weight} {Style} {Stretch}";
}
