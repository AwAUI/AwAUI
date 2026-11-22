using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.Diagnostics.Inspection;
using AwAUI.Graphics;
using AwAUI.Platform;
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
    readonly InputManager _input;
    readonly Action _changed;
    readonly List<ActivePointerInfo> _activePointers = new();
    Element? _highlight;
    HighlightConfig _config = new();
    Element? _pickHover;
    InspectMode _mode;
    OverlayShowOptions _showOptions = new();

    public ElementOverlayHost(ElementInspectableTree tree, RenderPipeline pipeline, InputManager input, Action changed)
    {
        _tree = tree;
        _pipeline = pipeline;
        _input = input;
        _changed = changed;
        _input.ActivePointersChanged += OnActivePointersChanged;
    }

    /// <summary>停止跟踪输入管理器（DevTools 释放时调用）。</summary>
    internal void Dispose() => _input.ActivePointersChanged -= OnActivePointersChanged;

    void OnActivePointersChanged()
    {
        // 只有显示触摸点时才需要重新合成；打开选项时 SetShowOptions 自己会触发一次。
        if (_showOptions.Touches) Changed();
    }

    /// <summary>叠加层内容的版本：每次高亮、拾取悬停或模式变化加一（宿主据此决定是否重新合成）。</summary>
    public int Version { get; private set; }

    public InspectMode Mode => _mode;

    /// <summary>当前高亮的元素（拾取悬停优先）。</summary>
    public Element? Highlighted => _mode == InspectMode.Pick && _pickHover is not null ? _pickHover : _highlight;

    public bool HasContent => (Highlighted is { IsAttached: true }) || HasShowOptions;

    /// <summary>是否有需要持续绘制（与高亮无关）的显示选项：标尺、命中目标、触摸点、布局网格。</summary>
    bool HasShowOptions => _showOptions.Rulers || _showOptions.HitTargets || _showOptions.Touches || _showOptions.LayoutGrids.Count > 0;

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

    /// <summary>当前的持续显示选项（标尺、命中目标、触摸点、布局网格）。</summary>
    public OverlayShowOptions ShowOptions => _showOptions;

    /// <summary>
    /// 保存持续显示选项，并触发重新合成：标尺、命中目标、触摸点与布局网格在 <see cref="Paint"/> 中按最新布局 / 输入状态绘制。
    /// </summary>
    public override void SetShowOptions(OverlayShowOptions options)
    {
        _showOptions = options ?? throw new ArgumentNullException(nameof(options));
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

    /// <summary>把叠加层录制到 <paramref name="dc"/>（逻辑像素，与应用帧同一坐标系）；整体裁剪到视口内。</summary>
    public void Paint(DrawingContext dc)
    {
        var vp = _pipeline.Viewport;
        dc.Save();
        dc.ClipRect(new Rect(0, 0, vp.Width, vp.Height));
        try
        {
            PaintShowOptions(dc, vp);
            PaintHighlight(dc);
        }
        finally
        {
            dc.Restore();
        }
    }

    /// <summary>高亮四层着色、延长线与信息提示。</summary>
    void PaintHighlight(DrawingContext dc)
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

    // ------------------------------------------------------------ 持续显示选项

    const float RulerThickness = 16, RulerStep = 50;
    static readonly Color s_rulerBackground = Color.FromRgba(32, 33, 36, 200);
    static readonly Color s_rulerTick = Color.FromRgba(230, 232, 236, 220);
    static readonly Color s_hitTarget = Color.FromRgba(255, 159, 10, 230);
    static readonly Color s_gridFill = Color.FromRgba(111, 168, 220, 48);
    static readonly Color s_gridStroke = Color.FromRgba(111, 168, 220, 200);
    const float TouchRadius = 12;
    static readonly Color s_touchFill = Color.FromRgba(255, 87, 34, 72);
    static readonly Color s_touchStroke = Color.FromRgba(255, 87, 34, 230);
    readonly Dictionary<string, TextLayout> _rulerLabels = new(StringComparer.Ordinal);

    void PaintShowOptions(DrawingContext dc, Size vp)
    {
        if (_showOptions.Rulers) PaintRulers(dc, vp);
        if (_showOptions.HitTargets) PaintHitTarget(dc);
        if (_showOptions.Touches) PaintTouches(dc);
        if (_showOptions.LayoutGrids.Count > 0) PaintLayoutGrids(dc);
    }

    /// <summary>
    /// 活动触摸 / 笔指针：圆 + 十字准星（客户区逻辑像素）。鼠标也由输入管理器记录，但不绘制。
    /// 位置来自 <see cref="InputManager.GetActivePointers"/>，选项关闭后不再绘制。
    /// </summary>
    void PaintTouches(DrawingContext dc)
    {
        _input.GetActivePointers(_activePointers);
        var paint = new Paint(s_touchStroke);
        var stroke = new Stroke(2);
        for (int i = 0; i < _activePointers.Count; i++)
        {
            var p = _activePointers[i];
            if (p.Type == PointerType.Mouse) continue;
            var center = p.Position;
            dc.FillCircle(center, TouchRadius, s_touchFill);
            dc.DrawLine(new Point(center.X - TouchRadius, center.Y), new Point(center.X + TouchRadius, center.Y), paint, stroke);
            dc.DrawLine(new Point(center.X, center.Y - TouchRadius), new Point(center.X, center.Y + TouchRadius), paint, stroke);
        }
    }

    /// <summary>视口顶边与左边的刻度条：每 <see cref="RulerStep"/> 逻辑像素一根刻度，偶数位置为主刻度并标注坐标。</summary>
    void PaintRulers(DrawingContext dc, Size vp)
    {
        if (!(vp.Width > 0) || !(vp.Height > 0)) return;
        dc.FillRect(new Rect(0, 0, vp.Width, RulerThickness), s_rulerBackground);
        dc.FillRect(new Rect(0, 0, RulerThickness, vp.Height), s_rulerBackground);
        var paint = new Paint(s_rulerTick);
        var stroke = new Stroke(1);
        for (float x = 0; x <= vp.Width; x += RulerStep)
        {
            bool major = (int)Math.Round(x / RulerStep) % 2 == 0;
            dc.DrawLine(new Point(x + 0.5f, 0), new Point(x + 0.5f, major ? 8 : 4), paint, stroke);
            if (major) DrawRulerLabel(dc, ((int)Math.Round(x)).ToString(CultureInfo.InvariantCulture), new Point(x + 2, 1));
        }
        for (float y = RulerStep; y <= vp.Height; y += RulerStep)
        {
            bool major = (int)Math.Round(y / RulerStep) % 2 == 0;
            dc.DrawLine(new Point(0, y + 0.5f), new Point(major ? 8 : 4, y + 0.5f), paint, stroke);
            if (major) DrawRulerLabel(dc, ((int)Math.Round(y)).ToString(CultureInfo.InvariantCulture), new Point(1, y + 1));
        }
    }

    void DrawRulerLabel(DrawingContext dc, string text, Point at)
    {
        // 视口尺寸变化会产生新的坐标标签；限制缓存条目，避免长时间运行后无限增长
        if (_rulerLabels.Count > 128) _rulerLabels.Clear();
        if (!_rulerLabels.TryGetValue(text, out var layout))
        {
            var style = new TextStyle { FontFamilies = s_fonts, FontSize = 9, Color = s_rulerTick };
            var builder = _pipeline.TextEngine.CreateParagraphBuilder(new ParagraphStyle { TextStyle = style, MaxLines = 1 });
            builder.AddText(text);
            layout = builder.Build();
            layout.Layout(float.PositiveInfinity);
            _rulerLabels[text] = layout;
        }
        layout.Paint(dc, at);
    }

    /// <summary>当前高亮（拾取悬停优先）元素的命中矩形描边。没有全局命中表，因此只标出当前元素。</summary>
    void PaintHitTarget(DrawingContext dc)
    {
        var e = Highlighted;
        if (e is null || !e.IsAttached) return;
        var rect = e.BoundingRect;
        if (!(rect.Width > 0) || !(rect.Height > 0)) return;
        dc.StrokeRect(rect, new Paint(s_hitTarget), new Stroke(1));
    }

    /// <summary>为 <see cref="OverlayShowOptions.LayoutGrids"/> 指定的 flex / grid 容器绘制列 / 行轨道带。</summary>
    void PaintLayoutGrids(DrawingContext dc)
    {
        var grids = _showOptions.LayoutGrids;
        for (int i = 0; i < grids.Count; i++)
        {
            var e = _tree.Find(grids[i]);
            if (e is null || e.IsTextNode || e.Render is not { HasBox: true, Layout: { } node }) continue;
            var box = ElementInspectableTree.BoxModelOf(e);
            if (box is null) continue;
            var content = ToRect(box.Content);
            if (!(content.Width > 0) || !(content.Height > 0)) continue;
            // X 轴轨道带沿内容盒高度延伸，Y 轴沿宽度延伸
            PaintTrackBands(dc, content, node.UsedTracksX, node.UsedTrackPositionsX, true);
            PaintTrackBands(dc, content, node.UsedTracksY, node.UsedTrackPositionsY, false);
        }
    }

    static void PaintTrackBands(DrawingContext dc, Rect content, IReadOnlyList<float>? sizes, IReadOnlyList<float>? positions, bool horizontal)
    {
        if (sizes is null || positions is null) return;
        int n = Math.Min(sizes.Count, positions.Count);
        var fill = new Paint(s_gridFill);
        var stroke = new Stroke(1);
        var outline = new Paint(s_gridStroke);
        for (int i = 0; i < n; i++)
        {
            float size = sizes[i], pos = positions[i];
            if (!(size > 0) || float.IsNaN(pos) || float.IsInfinity(pos)) continue;
            var band = horizontal
                ? new Rect(content.X + pos, content.Y, size, content.Height)
                : new Rect(content.X, content.Y + pos, content.Width, size);
            dc.FillRect(band, fill);
            dc.StrokeRect(band, outline, stroke);
        }
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
