using System;
using System.Collections.Generic;
using AwAUI.Graphics.Text;
using AwAUI.Unicode;

namespace AwAUI.Text;

/// <summary>字体管理：CSS 字体匹配、按码点回退、注册应用自带的字体。系统字体由平台层枚举后注册进来。</summary>
public abstract class FontManager
{
    /// <summary>按 CSS 字体匹配算法选择字型：族名列表 → 字宽 → 字形风格 → 字重。没有任何族名可用时返回 null。</summary>
    public abstract FontFace? Match(in FontQuery query);

    /// <summary>为某个码点（见 <see cref="CodePoint"/>）在指定语言环境下找到能显示它的字型；找不到时返回 null。</summary>
    public abstract FontFace? Fallback(int codePoint, in FontQuery baseQuery, string? locale);

    /// <summary>
    /// 没有任何字型能显示某个码点（且回退链没有系统字体）时触发，供应用按需下载并注册字体（design/19「Web 平台没有系统字体」）。
    /// 每个（码点、语言环境、样式）组合只触发一次，直到再次注册字体或清空缓存。
    /// </summary>
    public event EventHandler<MissingGlyphEventArgs>? MissingGlyphs;

    /// <summary>触发 <see cref="MissingGlyphs"/>。</summary>
    protected void RaiseMissingGlyph(int codePoint, string? locale) =>
        MissingGlyphs?.Invoke(this, new MissingGlyphEventArgs(codePoint, locale));

    /// <summary>注册应用自带或 <c>@font-face</c> 声明的字体。<paramref name="overrideDescriptor"/> 覆盖字体自身的描述符。</summary>
    public abstract FontRegistration Register(FontSource source, in FontDescriptor? overrideDescriptor = null);

    /// <summary>已注册的族名（按首次注册的顺序）。</summary>
    public abstract IReadOnlyList<string> FamilyNames { get; }
}

/// <summary><see cref="FontManager.MissingGlyphs"/> 的事件参数。</summary>
public sealed class MissingGlyphEventArgs : EventArgs
{
    public MissingGlyphEventArgs(int codePoint, string? locale)
    {
        CodePoint = codePoint;
        Locale = locale;
    }

    /// <summary>没有任何字型能显示的码点。</summary>
    public int CodePoint { get; }

    /// <summary>查询时的语言环境（BCP 47，可能为 null）。</summary>
    public string? Locale { get; }
}
