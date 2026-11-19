namespace AwAUI.Graphics.Text;

/// <summary>一组垂直度量（设计单位，y 向上：上升为正，下降通常为负）。</summary>
public readonly struct VerticalMetrics
{
    public VerticalMetrics(int ascender, int descender, int lineGap)
    {
        Ascender = ascender;
        Descender = descender;
        LineGap = lineGap;
    }

    public int Ascender { get; }
    public int Descender { get; }
    public int LineGap { get; }

    public override string ToString() => $"{Ascender} {Descender} {LineGap}";
}

/// <summary>
/// 字型的全局度量，全部为设计单位（除以 <see cref="UnitsPerEm"/> 再乘以字号得到逻辑像素）。
/// y 向上：<see cref="Ascender"/> 为正，<see cref="Descender"/> 通常为负；下划线位置在基线以下时为负。
/// </summary>
public readonly struct FontMetrics
{
    public int UnitsPerEm { get; init; }

    /// <summary>排版使用的上升、下降与行间距（已按 USE_TYPO_METRICS / hhea / typo / win 的优先级选择）。</summary>
    public int Ascender { get; init; }
    public int Descender { get; init; }
    public int LineGap { get; init; }

    /// <summary>'hhea' 表中的原始值。</summary>
    public VerticalMetrics Hhea { get; init; }
    /// <summary>'OS/2' sTypo* 的原始值（没有 OS/2 表时为 0）。</summary>
    public VerticalMetrics Typo { get; init; }
    /// <summary>'OS/2' usWinAscent / -usWinDescent（行间距恒为 0）。</summary>
    public VerticalMetrics Win { get; init; }

    public int XHeight { get; init; }
    public int CapHeight { get; init; }

    public int UnderlinePosition { get; init; }
    public int UnderlineThickness { get; init; }
    public int StrikeoutPosition { get; init; }
    public int StrikeoutThickness { get; init; }

    /// <summary>斜体角度（度，逆时针为正，常见的右倾斜体为负值）。</summary>
    public float ItalicAngle { get; init; }
    public bool IsFixedPitch { get; init; }

    /// <summary>全部字形的边界框（'head' 中的 xMin/yMin/xMax/yMax）。</summary>
    public int XMin { get; init; }
    public int YMin { get; init; }
    public int XMax { get; init; }
    public int YMax { get; init; }

    public int AdvanceWidthMax { get; init; }

    /// <summary>默认行高：上升 − 下降 + 行间距。</summary>
    public int LineHeight => Ascender - Descender + LineGap;

    /// <summary>设计单位到指定字号（逻辑像素）的缩放比例。</summary>
    public float GetScale(float size) => UnitsPerEm == 0 ? 0 : size / UnitsPerEm;
}
