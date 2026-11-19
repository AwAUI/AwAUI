using System;

namespace AwAUI.Graphics.Text;

/// <summary>一个可变字体轴的取值。</summary>
public readonly struct VariationSetting
{
    public VariationSetting(uint tag, float value)
    {
        Tag = tag;
        Value = value;
    }

    public uint Tag { get; }
    public float Value { get; }
}

/// <summary>可变字体的轴取值集合。default 表示默认实例。可变字体在 P4 实现，此前所有取值都被忽略。</summary>
public readonly struct FontVariation : IEquatable<FontVariation>
{
    readonly VariationSetting[]? _settings;

    public FontVariation(params VariationSetting[] settings) => _settings = settings is { Length: > 0 } ? (VariationSetting[])settings.Clone() : null;

    public static FontVariation Default => default;

    public bool IsDefault => _settings is null;

    public ReadOnlySpan<VariationSetting> Settings => _settings;

    /// <summary>按轴取值逐项比较（顺序敏感）。</summary>
    public bool Equals(FontVariation other)
    {
        if (ReferenceEquals(_settings, other._settings)) return true;
        var a = Settings;
        var b = other.Settings;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].Tag != b[i].Tag || !a[i].Value.Equals(b[i].Value)) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is FontVariation v && Equals(v);

    public override int GetHashCode()
    {
        int h = 0;
        foreach (var s in Settings) h = HashCodeCompat.Combine(h, (int)s.Tag, s.Value.GetHashCode());
        return h;
    }

    public static bool operator ==(FontVariation a, FontVariation b) => a.Equals(b);
    public static bool operator !=(FontVariation a, FontVariation b) => !a.Equals(b);
}

/// <summary>可变字体的一个设计轴（'fvar'）。</summary>
public sealed class VariationAxis
{
    public VariationAxis(uint tag, string name, float minValue, float defaultValue, float maxValue, bool isHidden = false)
    {
        Tag = tag;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        MinValue = minValue;
        DefaultValue = defaultValue;
        MaxValue = maxValue;
        IsHidden = isHidden;
    }

    public uint Tag { get; }
    public string Name { get; }
    public float MinValue { get; }
    public float DefaultValue { get; }
    public float MaxValue { get; }
    public bool IsHidden { get; }
}
