using AwAUI.Css.Cascade;
using AwAUI.Css.Selectors;
using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Cascade;

/// <summary>级联测试的辅助：建引擎、解析整棵树。</summary>
internal static class CascadeTestHelper
{
    public static StyleEngine Engine(string authorCss, string? uaCss = null, CssEnvironment? env = null)
    {
        var e = new StyleEngine(env);
        if (uaCss is not null) e.AddStyleSheet(StyleSheet.Parse(uaCss), CascadeOrigin.UserAgent);
        e.AddStyleSheet(StyleSheet.Parse(authorCss), CascadeOrigin.Author);
        return e;
    }

    /// <summary>自顶向下解析整棵树。</summary>
    public static Dictionary<El, ComputedStyle> ResolveTree(StyleEngine engine, El root)
    {
        var map = new Dictionary<El, ComputedStyle>();
        Walk(root, null);
        return map;

        void Walk(El el, ComputedStyle? parent)
        {
            var s = engine.Resolve(el, parent);
            map[el] = s;
            foreach (var c in el.Children) Walk(c, s);
        }
    }

    /// <summary>单元素：解析 root 下的一个子元素（root 无样式表命中时为初始值）。</summary>
    public static ComputedStyle One(string css, El element, string? uaCss = null)
    {
        var engine = Engine(css, uaCss);
        var chain = new List<El>();
        for (StyleElement? e = element; e is not null; e = e.Parent) chain.Add((El)e);
        chain.Reverse();
        ComputedStyle? s = null;
        foreach (var e in chain) s = engine.Resolve(e, s);
        return s!;
    }

    public static string Text(this ComputedStyle s, string property) => s.GetPropertyValueText(property)!;
}
