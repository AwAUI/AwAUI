using System.Collections.Generic;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>
/// 只做语法校验、计算值保留为规范化文本的属性（<c>mask-repeat</c>、<c>mask-position</c>、<c>mask-size</c>）：
/// 渲染尚未实现，但声明有效、参与级联、可在 DevTools 中查看。
/// </summary>
internal static class RawParsers
{
    /// <summary>逗号分隔的列表，每项由 <paramref name="item"/> 校验；返回规范化文本。</summary>
    delegate bool ItemParser(ref ValueReader r);

    static CssValue? List(ref ValueReader r, ItemParser item)
    {
        var sb = new StringBuilder();
        int items = 0;
        while (true)
        {
            int start = r.Position;
            if (!item(ref r) || ++items > 1024) return null;
            for (int i = start; i < r.Position; i++) sb.Append(r.PeekAt(i)!.ToCss());
            if (r.AtEnd) break;
            if (!r.TryComma()) return null;
            sb.Append(", ");
        }
        return new PrecomputedValue<string>(System.Text.RegularExpressions.Regex.Replace(sb.ToString().Trim(), @"\s+", " "));
    }

    /// <summary><c>&lt;repeat-style&gt;# = repeat-x | repeat-y | [ repeat | space | round | no-repeat ]{1,2}</c></summary>
    public static CssValue? MaskRepeat(ref ValueReader r) => List(ref r, static (ref ValueReader x) =>
    {
        var k = x.PeekIdentLower();
        if (k is "repeat-x" or "repeat-y") { x.Advance(); return true; }
        int n = 0;
        while (n < 2 && x.PeekIdentLower() is "repeat" or "space" or "round" or "no-repeat") { x.Advance(); n++; }
        return n > 0;
    });

    /// <summary><c>&lt;position&gt;#</c></summary>
    public static CssValue? MaskPosition(ref ValueReader r) => List(ref r, static (ref ValueReader x) => ValueParsers.Position(ref x, out _, out _));

    /// <summary><c>&lt;bg-size&gt;# = [ &lt;length-percentage [0,∞]&gt; | auto ]{1,2} | cover | contain</c></summary>
    public static CssValue? MaskSize(ref ValueReader r) => List(ref r, static (ref ValueReader x) =>
    {
        if (x.TryIdent("cover") || x.TryIdent("contain")) return true;
        int n = 0;
        while (n < 2 && !x.AtEnd)
        {
            int save = x.Position;
            var v = x.Next();
            if (v is CssToken t && t.IsIdent("auto") || ValueParsers.LengthPercentageNode(v, nonNegative: true) is not null) { n++; continue; }
            x.Position = save;
            break;
        }
        return n > 0;
    });
}
