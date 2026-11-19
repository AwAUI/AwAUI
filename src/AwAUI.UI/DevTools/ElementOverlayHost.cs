using System;
using System.Globalization;
using System.Text;
using AwAUI.Diagnostics.Inspection;
using AwAUI.Graphics;
using AwAUI.Text;

namespace AwAUI.UI;

/// <summary>
/// DevTools <c>Overlay</c> 域的执行者（design/06「与被调试 UI 的隔离」）：高亮与拾取画在应用帧之上的独立叠加层里，
/// 不参与应用的布局与命中测试。高亮像浏览器一样分四层着色（外边距 / 边框 / 内边距 / 内容），并带「标签 + 尺寸」的信息提示；
/// 跟随节点的布局变化（每次合成时按最新布局重画）。
/// <para>拾取模式下宿主（<see cref="UIHost"/>）在输入路由进应用之前截获指针：悬停高亮命中的元素（忽略 <c>pointer-events</c>），
/// 按下即选中（<see cref="OverlayHost.NotifyNodePicked"/>）。只在 UI 线程上使用。</para>
/// </summary>
internal sealed class ElementOverlayHost : OverlayHost
{
    readonly ElementInspectableTree _tree;
    readonly RenderPipeline _pipeline;
    readonly Action _changed;
    Element? _highlight;
    HighlightConfig _config = new();
    Element? _pickHover;
    InspectMode _mode;

    public ElementOverlayHost(ElementInspectableTree tree, RenderPipeline pipeline, Action changed)
    {
        _tree = tree;
        _pipeline = pipeline;
        _changed = changed;
    }

    /// <summary>叠加层内容的版本：每次高亮、拾取悬停或模式变化加一（宿主据此决定是否重新合成）。</summary>
    public int Version { get; private set; }

    public InspectMode Mode => _mode;

    /// <summary>当前高亮的元素（拾取悬停优先）。</summary>
    public Element? Highlighted => _mode == InspectMode.Pick && _pickHover is not null ? _pickHover : _highlight;

    public bool HasContent => Highlighted is { IsAttached: true };

    void Changed()
    {
        Version++;
        _changed();
    }

    public override bool HighlightNode(long nodeId, HighlightConfig config)
    {
        var e = _tree.Find(nodeId);
        if (e is null) return false;
        Highlight(e, config);
        return true;
    }

    /// <summary>高亮一个元素（null 隐藏）。</summary>
    public void Highlight(Element? e, HighlightConfig? config = null)
    {
        _highlight = e;
        _config = config ?? new HighlightConfig();
        Changed();
    }

    public override void HideHighlight()
    {
        if (_highlight is null) return;
        _highlight = null;
        Changed();
    }

    public override void SetInspectMode(InspectMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        _pickHover = null;
        Changed();
    }

    /// <summary>拾取模式下指针悬停在 <paramref name="e"/> 上。</summary>
    public void SetPickHover(Element? e)
    {
        if (ReferenceEquals(_pickHover, e)) return;
        _pickHover = e;
        Changed();
    }

    // ------------------------------------------------------------ 绘制

    /// <summary>把叠加层录制到 <paramref name="dc"/>（逻辑像素，与应用帧同一坐标系）。</summary>
    public void Paint(DrawingContext dc)
    {
        var e = Highlighted;
        if (e is null || !e.IsAttached) return;
        var config = _mode == InspectMode.Pick && _pickHover is not null ? s_pickConfig : _config;
        var box = ElementInspectableTree.BoxModelOf(e);
        Rect border;
        if (box is null)
        {
            // 没有自己的盒（段落内的 span、文本节点）：用元素报告的矩形画内容色
            border = e.BoundingRect;
            if (border.Width <= 0 && border.Height <= 0) return;
            dc.FillRect(border, config.ContentColor);
        }
        else
        {
            var margin = ToRect(box.Margin);
            border = ToRect(box.Border);
            var padding = ToRect(box.Padding);
            var content = ToRect(box.Content);
            FillRing(dc, margin, border, config.MarginColor);
            FillRing(dc, border, padding, config.BorderColor);
            FillRing(dc, padding, content, config.PaddingColor);
            dc.FillRect(content, config.ContentColor);
        }
        if (config.ShowExtensionLines)
        {
            var line = new Paint(Color.FromRgba(111, 168, 220, 200));
            var stroke = new Stroke(1, StrokeCap.Butt, StrokeJoin.Miter, 10, new float[] { 3, 3 });
            var vp = _pipeline.Viewport;
            dc.DrawLine(new Point(0, border.Top), new Point(vp.Width, border.Top), line, stroke);
            dc.DrawLine(new Point(0, border.Bottom), new Point(vp.Width, border.Bottom), line, stroke);
            dc.DrawLine(new Point(border.Left, 0), new Point(border.Left, vp.Height), line, stroke);
            dc.DrawLine(new Point(border.Right, 0), new Point(border.Right, vp.Height), line, stroke);
        }
        if (config.ShowInfo) PaintInfo(dc, e, border);
    }

    static readonly HighlightConfig s_pickConfig = new() { ShowInfo = true };

    static Rect ToRect(Quad q) => new(q.P1.X, q.P1.Y, q.P3.X - q.P1.X, q.P3.Y - q.P1.Y);

    /// <summary>填充 <paramref name="outer"/> 减去 <paramref name="inner"/> 的环（上下左右四条，互不重叠）。</summary>
    static void FillRing(DrawingContext dc, Rect outer, Rect inner, Color color)
    {
        if (color.A == 0) return;
        float top = Math.Max(0, inner.Top - outer.Top), bottom = Math.Max(0, outer.Bottom - inner.Bottom);
        if (top > 0) dc.FillRect(new Rect(outer.Left, outer.Top, outer.Width, top), color);
        if (bottom > 0) dc.FillRect(new Rect(outer.Left, outer.Bottom - bottom, outer.Width, bottom), color);
        float midTop = outer.Top + top, midHeight = outer.Height - top - bottom;
        if (midHeight <= 0) return;
        float left = Math.Max(0, inner.Left - outer.Left), right = Math.Max(0, outer.Right - inner.Right);
        if (left > 0) dc.FillRect(new Rect(outer.Left, midTop, left, midHeight), color);
        if (right > 0) dc.FillRect(new Rect(outer.Right - right, midTop, right, midHeight), color);
    }

    TextLayout? _label;
    string? _labelText;

    /// <summary>信息提示：「标签#id.class  宽 × 高」，画在边框盒上方（放不下时在下方），保持在视口内。</summary>
    void PaintInfo(DrawingContext dc, Element e, Rect border)
    {
        var sb = new StringBuilder(e.IsTextNode ? "#text" : e.Tag);
        if (e.Id is { Length: > 0 } id) sb.Append('#').Append(id);
        foreach (var c in e.Classes)
        {
            if (sb.Length > 60) break;
            sb.Append('.').Append(c);
        }
        sb.Append("  ").Append(Format(border.Width)).Append(" × ").Append(Format(border.Height));
        string text = sb.ToString();
        if (!string.Equals(text, _labelText, StringComparison.Ordinal) || _label is null)
        {
            var style = new TextStyle { FontFamilies = s_fonts, FontSize = 11, Color = Color.FromRgba(32, 33, 36, 255) };
            var builder = _pipeline.TextEngine.CreateParagraphBuilder(new ParagraphStyle { TextStyle = style, MaxLines = 1 });
            builder.AddText(text);
            _label = builder.Build();
            _label.Layout(float.PositiveInfinity);
            _labelText = text;
        }
        var label = _label;
        float w = label.MaxIntrinsicWidth + 12, h = label.Height + 6;
        var vp = _pipeline.Viewport;
        float x = Math.Max(0, Math.Min(border.Left, vp.Width - w));
        float y = border.Top - h - 4;
        if (y < 0) y = Math.Min(border.Bottom + 4, Math.Max(0, vp.Height - h));
        var bg = new RoundRect(new Rect(x, y, w, h), 3);
        dc.DrawBoxShadow(bg, new BoxShadow(0, 1, 4, 0, Color.FromRgba(0, 0, 0, 60)));
        dc.FillRoundRect(bg, Color.FromRgba(255, 255, 255, 245));
        label.Paint(dc, new Point(x + 6, y + 3));
    }

    static readonly string[] s_fonts = { "system-ui", "sans-serif" };

    static string Format(float v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
