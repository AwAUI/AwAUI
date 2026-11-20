using System;
using System.Collections.Generic;
using AwAUI.Css;
using AwAUI.Text;

namespace AwAUI.UI;

// 输入框的段落与绘制：预编辑文本带下划线、选区高亮、插入点、内容超宽时的水平滚动。
public sealed partial class RenderPipeline
{
    /// <summary>选区高亮色（与默认样式表的焦点环同一色系，半透明叠在文字下面）。</summary>
    static readonly Color SelectionColor = new(0.31f, 0.55f, 0.97f, 0.32f);

    readonly List<Rect> _inputRects = new();

    /// <summary>输入框的段落内容：显示文本（值 + 预编辑文本，密码框为圆点）、占位文本，或撑起一行高度的零宽空格。</summary>
    void AddInputText(InputElement input, ComputedStyle style, TextStyle ts, ParagraphBuilder builder)
    {
        string doc = input.DisplayText;
        if (doc.Length > 0)
        {
            if (input.IsPassword)
            {
                builder.AddText(TextEditing.Mask(doc));
                return;
            }
            if (!input.IsComposing)
            {
                builder.AddText(doc);
                return;
            }
            int cs = input.CompositionStart, ce = cs + input.CompositionText.Length;
            if (cs > 0) builder.AddText(doc.AsSpan(0, cs));
            builder.PushStyle(Underlined(ts));
            builder.AddText(input.CompositionText);
            builder.Pop();
            if (ce < doc.Length) builder.AddText(doc.AsSpan(ce));
        }
        else if (!string.IsNullOrEmpty(input.Placeholder))
        {
            builder.PushStyle(PlaceholderTextStyle(input, style)); // ::placeholder（没有规则时半透明）
            builder.AddText(input.Placeholder!);
            builder.Pop();
        }
        else
        {
            // 空输入框仍有一行高度（光标停靠）
            builder.AddText("​");
        }
    }

    /// <summary>预编辑文本的样式：在原样式上加实线下划线（输入法组合的通行显示方式）。</summary>
    static TextStyle Underlined(TextStyle s) => new()
    {
        FontFamilies = s.FontFamilies,
        FontSize = s.FontSize,
        FontWeight = s.FontWeight,
        FontStyle = s.FontStyle,
        FontStretch = s.FontStretch,
        Color = s.Color,
        Foreground = s.Foreground,
        Decoration = s.Decoration | TextDecorationLine.Underline,
        DecorationColor = s.DecorationColor,
        DecorationStyle = TextDecorationStyle.Solid,
        DecorationThickness = s.DecorationThickness,
        LetterSpacing = s.LetterSpacing,
        WordSpacing = s.WordSpacing,
        LineHeight = s.LineHeight,
        TextTransform = s.TextTransform,
        Locale = s.Locale,
        Features = s.Features,
        Variation = s.Variation,
    };

    /// <summary>
    /// 绘制输入框的段落：文本在内容盒内垂直居中；获得焦点时画选区高亮与插入点（选区非空时不画插入点）；
    /// 输入法选中的文节加粗下划线。文本比内容盒宽时水平滚动使插入点可见，并裁剪到内边距盒。
    /// 记录段落原点与插入点（相对边框盒），供命中测试与候选窗定位。
    /// </summary>
    void PaintInput(RenderBox b, ComputedStyle style, TextParagraph para, InputElement input)
    {
        var dc = _dc!;
        var tl = para.Layout;
        tl.Layout(b.ContentWidth + TextParagraph.WidthSlack);
        string doc = input.DisplayText;
        bool pw = input.IsPassword;
        bool hasText = doc.Length > 0;
        bool focused = input.IsFocused;

        // 插入点与选区（显示文本下标）
        int caretDoc, selStartDoc, selEndDoc;
        if (input.IsComposing)
        {
            caretDoc = input.CompositionStart + input.CompositionSelectionEnd;
            selStartDoc = selEndDoc = caretDoc;
        }
        else
        {
            caretDoc = input.SelectionFocus;
            selStartDoc = input.SelectionStart;
            selEndDoc = input.SelectionEnd;
        }
        int caret = hasText ? TextEditing.ToDisplay(doc, caretDoc, pw) : 0;
        var caretRect = tl.GetCaretRect(new TextPosition(caret, TextAffinity.Upstream));

        // 水平滚动：只在文本超出内容盒时出现，使插入点保持在可见范围内（滚动量只在需要时变化，重复计算结果不变）
        float contentW = b.ContentWidth;
        float textW = hasText ? tl.Width : 0;
        float scroll = input.ScrollX;
        if (textW <= contentW) scroll = 0;
        else
        {
            float maxScroll = textW - contentW + 1;
            if (caretRect.X - scroll > contentW - 1) scroll = caretRect.X - contentW + 1;
            if (caretRect.X - scroll < 0) scroll = caretRect.X;
            scroll = Math.Max(0, Math.Min(scroll, maxScroll));
        }
        input.ScrollX = scroll;

        // 单行输入框：文本在内容盒内垂直居中
        // 水平滚动量单独对齐：录制结果只依赖对齐后的内容盒（PaintGeometry），元素整体移动后缓存平移复用时像素不变
        float originY;
        bool clipsHeight = false;
        if (input.IsMultiline)
        {
            float scrollY = input.ScrollY;
            float fullHeight = Math.Max(tl.Height, caretRect.Bottom);
            if (focused)
            {
                if (caretRect.Top < scrollY) scrollY = caretRect.Top;
                if (caretRect.Bottom > scrollY + b.ContentHeight) scrollY = caretRect.Bottom - b.ContentHeight;
            }
            input.ScrollY = Math.Max(0, Math.Min(scrollY, fullHeight - b.ContentHeight));
            originY = Snap(b.ContentY) - Snap(input.ScrollY);
            clipsHeight = fullHeight > b.ContentHeight + 0.5f;
        }
        else originY = Snap(b.ContentY + Math.Max(0, (b.ContentHeight - tl.Height) / 2));
        var origin = new Point(Snap(b.ContentX) - Snap(scroll), originY);
        bool clip = clipsHeight || scroll > 0 || textW > contentW + 0.5f;
        if (clip)
        {
            dc.Save();
            var bw = BoxPainter.GetBorderWidths(style, _scale);
            dc.ClipRect(Rect.FromLTRB(b.Border.Left + bw.Left, b.Border.Top + bw.Top, b.Border.Right - bw.Right, b.Border.Bottom - bw.Bottom));
        }

        if (focused && hasText && selStartDoc != selEndDoc)
        {
            int ds = TextEditing.ToDisplay(doc, selStartDoc, pw), de = TextEditing.ToDisplay(doc, selEndDoc, pw);
            _inputRects.Clear();
            tl.GetSelectionRects(new TextRange(ds, de), _inputRects);
            var highlight = SelectionHighlight(input) ?? SelectionColor; // ::selection 的背景色
            foreach (var r in _inputRects) dc.FillRect(new Rect(origin.X + r.X, origin.Y + r.Y, r.Width, r.Height), highlight);
        }
        if (!para.IsEmpty) tl.Paint(dc, origin);

        var color = style.ResolveColor(style.Color);
        float line = 1f / _scale * Math.Max(1, (float)Math.Round(_scale));
        if (input.IsComposing && !pw && input.CompositionSelectionStart != input.CompositionSelectionEnd)
        {
            // 输入法正在转换的文节：在普通下划线之上加粗
            int ds = input.CompositionStart + input.CompositionSelectionStart, de = input.CompositionStart + input.CompositionSelectionEnd;
            _inputRects.Clear();
            tl.GetSelectionRects(new TextRange(ds, de), _inputRects);
            foreach (var r in _inputRects) dc.FillRect(new Rect(origin.X + r.X, origin.Y + r.Bottom - 2 * line, r.Width, 2 * line), color);
        }
        if (focused && !input.ReadOnly && selStartDoc == selEndDoc)
            dc.FillRect(new Rect(Snap(origin.X + caretRect.X), origin.Y + caretRect.Y, line, caretRect.Height), color);
        if (clip) dc.Restore();

        input.TextOriginLocal = new Point(origin.X - b.Border.X, origin.Y - b.Border.Y);
        input.CaretLocal = new Rect(origin.X + caretRect.X - b.Border.X, origin.Y + caretRect.Y - b.Border.Y, line, caretRect.Height);
        input.PaintedText = hasText ? tl.Text : "";
        input.HasPaintedGeometry = true;
    }
}
