using System;
using System.Collections.Generic;

namespace AwAUI.Graphics;

/// <summary>混合模式：CSS 的 mix-blend-mode 全集 + 常用的 Porter-Duff 模式。</summary>
public enum BlendMode : byte
{
    SrcOver, Src, Clear, DstIn, DstOut, Plus,
    Multiply, Screen, Overlay, Darken, Lighten, ColorDodge, ColorBurn, HardLight, SoftLight,
    Difference, Exclusion, Hue, Saturation, Color, Luminosity,
}

/// <summary>画刷 + 混合模式 + 抗锯齿。不可变。</summary>
public sealed class Paint
{
    public Paint(Brush brush, BlendMode blendMode = BlendMode.SrcOver, bool antiAlias = true)
    {
        Brush = brush ?? throw new ArgumentNullException(nameof(brush));
        BlendMode = blendMode;
        AntiAlias = antiAlias;
    }

    public Paint(Color color, BlendMode blendMode = BlendMode.SrcOver, bool antiAlias = true)
        : this(new SolidColorBrush(color), blendMode, antiAlias) { }

    public Brush Brush { get; }
    public BlendMode BlendMode { get; }
    public bool AntiAlias { get; }

    public static implicit operator Paint(Color color) => new(color);
    public static implicit operator Paint(Brush brush) => new(brush);
}

public enum StrokeCap : byte { Butt, Round, Square }
public enum StrokeJoin : byte { Miter, Round, Bevel }

/// <summary>描边参数。不可变。</summary>
public sealed class Stroke
{
    public Stroke(float width, StrokeCap cap = StrokeCap.Butt, StrokeJoin join = StrokeJoin.Miter,
        float miterLimit = 4f, IReadOnlyList<float>? dashes = null, float dashOffset = 0)
    {
        Width = width;
        Cap = cap;
        Join = join;
        MiterLimit = Math.Max(1f, miterLimit);
        if (dashes is { Count: > 0 })
        {
            // SVG 规则：奇数个时重复一遍；全为 0 或含负数时视为无虚线
            var list = new List<float>(dashes);
            if (list.Count % 2 == 1) list.AddRange(dashes);
            float sum = 0;
            bool valid = true;
            foreach (var d in list) { if (d < 0) valid = false; sum += d; }
            if (valid && sum > 0) Dashes = list.ToArray();
        }
        DashOffset = dashOffset;
    }

    public float Width { get; }
    public StrokeCap Cap { get; }
    public StrokeJoin Join { get; }
    public float MiterLimit { get; }
    public IReadOnlyList<float>? Dashes { get; }
    public float DashOffset { get; }

    /// <summary>描边相对于几何的最大外扩距离（保守），用于包围盒。</summary>
    public float Outset
    {
        get
        {
            float hw = Width * 0.5f;
            float f = 1f;
            if (Join == StrokeJoin.Miter) f = Math.Max(f, MiterLimit);
            if (Cap == StrokeCap.Square) f = Math.Max(f, 1.4142136f);
            return hw * f;
        }
    }
}

/// <summary>盒阴影（CSS box-shadow）。</summary>
public readonly struct BoxShadow
{
    public BoxShadow(float offsetX, float offsetY, float blur, float spread, Color color, bool inset = false)
    {
        OffsetX = offsetX; OffsetY = offsetY; Blur = Math.Max(0, blur); Spread = spread; Color = color; Inset = inset;
    }

    public float OffsetX { get; }
    public float OffsetY { get; }

    /// <summary>CSS 模糊半径；对应高斯标准差 σ = Blur / 2。</summary>
    public float Blur { get; }
    public float Spread { get; }
    public Color Color { get; }
    public bool Inset { get; }
}

/// <summary>离屏图层参数（组透明度、混合模式）。</summary>
public readonly struct LayerOptions
{
    public LayerOptions(float opacity, BlendMode blendMode = BlendMode.SrcOver, Rect? bounds = null)
    {
        Opacity = opacity; BlendMode = blendMode; Bounds = bounds;
    }

    public float Opacity { get; }
    public BlendMode BlendMode { get; }

    /// <summary>图层内容的范围提示（局部坐标）。为空表示不限制。</summary>
    public Rect? Bounds { get; }
}
