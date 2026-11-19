using System;
using System.Collections.Generic;
using AwAUI.Graphics.Text;

namespace AwAUI.Text;

/// <summary>字体匹配请求：CSS 的 <c>font-family</c> 列表与 <c>font-weight / font-style / font-stretch</c>。</summary>
public readonly struct FontQuery
{
    readonly IReadOnlyList<string>? _families;
    readonly FontStretch _stretch;

    public FontQuery(string family, FontWeight weight = default, FontStyle style = FontStyle.Normal, FontStretch stretch = FontStretch.Normal)
        : this(new[] { family ?? throw new ArgumentNullException(nameof(family)) }, weight, style, stretch) { }

    public FontQuery(IReadOnlyList<string> families, FontWeight weight = default, FontStyle style = FontStyle.Normal, FontStretch stretch = FontStretch.Normal)
    {
        _families = families ?? throw new ArgumentNullException(nameof(families));
        Weight = weight;
        Style = style;
        _stretch = stretch;
    }

    /// <summary>按优先级排列的族名（可以包含通用族名，如 sans-serif，由 <see cref="FontCollection.SetGenericFamily"/> 解析）。</summary>
    public IReadOnlyList<string> Families => _families ?? Array.Empty<string>();
    public FontWeight Weight { get; }
    public FontStyle Style { get; }
    public FontStretch Stretch => _stretch == 0 ? FontStretch.Normal : _stretch;
}
