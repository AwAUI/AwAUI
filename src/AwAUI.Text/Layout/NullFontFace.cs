using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Graphics.Text;

namespace AwAUI.Text;

/// <summary>
/// 字体管理器中没有任何可用字型时的占位字型：没有字形（全部映射为不可见的 .notdef），前进量 0.5 em，度量与方块字体相同。
/// 使排版在没有字体时仍能给出有限、确定的结果而不是抛出异常。
/// </summary>
sealed class NullFontFace : FontFace
{
    public static readonly NullFontFace Instance = new();

    NullFontFace() { }

    public override FontDescriptor Descriptor { get; } = new("");
    public override FontMetrics Metrics { get; } = new()
    {
        UnitsPerEm = 1000,
        Ascender = 800,
        Descender = -200,
        XHeight = 500,
        CapHeight = 700,
        UnderlinePosition = -100,
        UnderlineThickness = 50,
        StrikeoutPosition = 300,
        StrikeoutThickness = 50,
        XMax = 500,
        YMin = -200,
        YMax = 800,
        AdvanceWidthMax = 500,
    };
    public override int GlyphCount => 1;
    public override OutlineFormat OutlineFormat => OutlineFormat.None;
    public override ReadOnlyMemory<byte> GetTable(uint tag) => ReadOnlyMemory<byte>.Empty;

    public override bool TryGetGlyph(int codePoint, out ushort glyph)
    {
        glyph = 0;
        return false;
    }

    public override int GetAdvance(ushort glyph) => 500;
    public override bool GetOutline(ushort glyph, in FontVariation variation, PathBuilder sink, in Matrix3x2 transform) => glyph == 0;
    public override GlyphImage? GetColorGlyph(ushort glyph, float size) => null;
    public override IReadOnlyList<VariationAxis> Axes => Array.Empty<VariationAxis>();
}

/// <summary>可按引用访问元素的增长数组（避免 List 的结构体副本）。</summary>
sealed class ValueList<T>
{
    T[] _items = new T[8];

    public int Count { get; private set; }

    public ref T this[int index] => ref _items[index];

    public void Add(in T item)
    {
        if (Count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
        _items[Count++] = item;
    }

    public void Clear() => Count = 0;

    public Span<T> AsSpan(int start, int length) => _items.AsSpan(start, length);
}
