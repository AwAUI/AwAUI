using System;
using AwAUI.Text.OpenType;

namespace AwAUI.Text;

/// <summary>OpenType 特性设置（对应 CSS <c>font-feature-settings</c>），可限定在文本范围内。</summary>
public readonly struct FontFeature
{
    public FontFeature(uint tag, uint value = 1, int start = 0, int end = int.MaxValue)
    {
        Tag = tag;
        Value = value;
        Start = start;
        End = end;
    }

    public FontFeature(string tag, uint value = 1, int start = 0, int end = int.MaxValue)
        : this(OpenTypeTag.Make(tag), value, start, end) { }

    public uint Tag { get; }
    /// <summary>0 关闭，1 开启，更大的值用于选择替代字形。</summary>
    public uint Value { get; }
    /// <summary>生效范围 [Start, End)，UTF-16 偏移。</summary>
    public int Start { get; }
    public int End { get; }

    public const uint Kerning = 0x6B65726E;   // 'kern'

    public override string ToString() => $"{OpenTypeTag.ToString(Tag)}={Value}";
}
