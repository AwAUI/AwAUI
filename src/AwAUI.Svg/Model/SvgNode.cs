using System;
using System.Collections.Generic;
using AwAUI.Svg.Xml;

namespace AwAUI.Svg.Model;

internal enum SvgElementKind : byte
{
    Unknown,
    Svg, G, Defs, Symbol, Use, A, Switch,
    Path, Rect, Circle, Ellipse, Line, Polyline, Polygon,
    LinearGradient, RadialGradient, Stop, ClipPath, Style,
    /// <summary>image：位图经 <see cref="SvgImageResolver"/> 解码后绘制。</summary>
    Image,
    /// <summary>title、desc、metadata：静默忽略。</summary>
    Descriptive,
    /// <summary>识别但不支持的元素（text、mask、filter、pattern、marker、foreignObject 等），带诊断忽略。</summary>
    Unsupported,
}

/// <summary>SVG 命名空间中的一个元素。外来命名空间的元素（Inkscape 元数据等）在构建时被剔除。</summary>
internal sealed class SvgNode
{
    static readonly List<SvgNode> NoChildren = new(0);

    public SvgNode(XmlElement xml, SvgElementKind kind, SvgNode? parent, int index)
    {
        Xml = xml;
        Kind = kind;
        Parent = parent;
        Index = index;
        Id = xml.GetAttribute("id");
        var cls = xml.GetAttribute("class");
        Classes = string.IsNullOrEmpty(cls) ? Array.Empty<string>() : SplitClasses(cls!);
        if (Classes.Length > 8) _classSet = new HashSet<string>(Classes, StringComparer.Ordinal);
    }

    readonly HashSet<string>? _classSet;

    /// <summary>元素是否带有类名 <paramref name="name"/>。类很多时用哈希集合，保证选择器匹配的单步代价是常数。</summary>
    public bool HasClass(string name) =>
        _classSet != null ? _classSet.Contains(name) : Array.IndexOf(Classes, name) >= 0;

    public XmlElement Xml { get; }
    public SvgElementKind Kind { get; }
    public string Name => Xml.Name;
    public SvgNode? Parent { get; }
    public int Index { get; }
    public int Offset => Xml.Offset;
    public string? Id { get; }
    public string[] Classes { get; }
    public List<SvgNode> Children { get; private set; } = NoChildren;

    /// <summary>层叠后的样式声明；没有任何声明时为 null。</summary>
    public DeclaredStyle? Style { get; set; }

    public void AddChild(SvgNode child)
    {
        if (ReferenceEquals(Children, NoChildren)) Children = new List<SvgNode>();
        Children.Add(child);
    }

    public string? Attr(string name) => Xml.GetAttribute(name);

    /// <summary>href 或 xlink:href（SVG 2 中前者优先）。</summary>
    public string? Href
    {
        get
        {
            string? xlink = null;
            foreach (var a in Xml.Attributes)
            {
                if (a.Name != "href") continue;
                if (a.Prefix == null) return a.Value;
                xlink ??= a.Value;
            }
            return xlink;
        }
    }

    public string? GetStyle(SvgProperty p) => Style?[p];

    /// <summary>拆分 class 属性并去重（重复的类名否则会让同一条规则被匹配多次）。</summary>
    static string[] SplitClasses(string s)
    {
        var parts = s.Split(new[] { ' ', '\t', '\r', '\n', '\f' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return parts;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<string>(parts.Length);
        foreach (var p in parts) if (seen.Add(p)) unique.Add(p);
        return unique.Count == parts.Length ? parts : unique.ToArray();
    }

    public static SvgElementKind KindOf(string name) => name switch
    {
        "svg" => SvgElementKind.Svg,
        "g" => SvgElementKind.G,
        "defs" => SvgElementKind.Defs,
        "symbol" => SvgElementKind.Symbol,
        "use" => SvgElementKind.Use,
        "a" => SvgElementKind.A,
        "switch" => SvgElementKind.Switch,
        "path" => SvgElementKind.Path,
        "rect" => SvgElementKind.Rect,
        "circle" => SvgElementKind.Circle,
        "ellipse" => SvgElementKind.Ellipse,
        "line" => SvgElementKind.Line,
        "polyline" => SvgElementKind.Polyline,
        "polygon" => SvgElementKind.Polygon,
        "linearGradient" => SvgElementKind.LinearGradient,
        "radialGradient" => SvgElementKind.RadialGradient,
        "stop" => SvgElementKind.Stop,
        "clipPath" => SvgElementKind.ClipPath,
        "style" => SvgElementKind.Style,
        "title" or "desc" or "metadata" => SvgElementKind.Descriptive,
        "image" => SvgElementKind.Image,
        "text" or "tspan" or "textPath" or "mask" or "filter" or "pattern" or "marker" or "foreignObject" or
            "script" or "animate" or "animateTransform" or "animateMotion" or "set" or "view" or "cursor" or
            "font" or "font-face" or "glyph" or "hatch" or "mesh" or "meshgradient" or "solidcolor" or
            "feBlend" or "feColorMatrix" or "feComposite" or "feFlood" or "feGaussianBlur" or "feOffset" or "feMerge" or
            "feMergeNode" or "feImage" or "feTurbulence" or "feMorphology" or "feDropShadow" => SvgElementKind.Unsupported,
        _ => SvgElementKind.Unknown,
    };
}
