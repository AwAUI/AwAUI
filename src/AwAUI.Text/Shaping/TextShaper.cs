using System;
using AwAUI.Graphics.Text;
using AwAUI.Unicode;

namespace AwAUI.Text;

/// <summary>整形请求。<see cref="Text"/> 含上下文，实际整形范围由 <see cref="Range"/> 指定。</summary>
public readonly ref struct ShapeRequest
{
    public ShapeRequest(ReadOnlySpan<char> text, FontFace face, float size)
        : this(text, new TextRange(0, text.Length), face, size) { }

    public ShapeRequest(ReadOnlySpan<char> text, TextRange range, FontFace face, float size)
    {
        range = range.Normalize();
        if (range.Min < 0 || range.Max > text.Length) throw new ArgumentOutOfRangeException(nameof(range));
        Text = text;
        Range = range;
        Face = face ?? throw new ArgumentNullException(nameof(face));
        Size = size;
        Script = Script.Unknown;
        Language = null;
        RightToLeft = false;
        Features = default;
        Variation = default;
    }

    public ReadOnlySpan<char> Text { get; }
    public TextRange Range { get; }
    public FontFace Face { get; }
    /// <summary>字号（逻辑像素，即 1 em 的长度）。</summary>
    public float Size { get; }
    /// <summary>文字（UAX #24 Script 属性，见 <see cref="UnicodeData.GetScript(int)"/>）。</summary>
    public Script Script { get; init; }
    public string? Language { get; init; }
    public bool RightToLeft { get; init; }
    public ReadOnlySpan<FontFeature> Features { get; init; }
    public FontVariation Variation { get; init; }
}

/// <summary>整形器：把一段同字体、同方向、同文字的文本转换为定位好的字形。</summary>
public abstract class TextShaper
{
    public abstract ShapedRun Shape(in ShapeRequest request);

    /// <summary>
    /// 两段分别整形的文本（例如段落布局中按断行机会切开、各自经缓存整形的词）在边界处相邻时，应在它们之间插入的额外间距（逻辑像素，负值收紧）。
    /// <paramref name="first"/> 是前一段逻辑顺序的最后一个可见字形，<paramref name="second"/> 是后一段的第一个可见字形；
    /// <paramref name="request"/> 只用其字型、字号、文字、语言、方向与特性（文本被忽略）。默认没有调整。
    /// </summary>
    public virtual float GetBoundaryKerning(in ShapeRequest request, ushort first, ushort second) => 0;
}
