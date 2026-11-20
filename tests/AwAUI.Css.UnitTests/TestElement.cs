using AwAUI.Css.Selectors;

namespace AwAUI.Css.UnitTests;

/// <summary>测试用的内存元素树。</summary>
internal sealed class El : StyleElement
{
    readonly List<string> _classes = new();
    readonly Dictionary<string, string> _attrs = new();
    readonly List<El> _children = new();
    readonly List<string> _parts = new();
    El? _parent;

    public El(string tag, string? id = null, string? classes = null)
    {
        TagName = tag;
        IdValue = id;
        if (classes is not null) _classes.AddRange(classes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
    }

    public string TagName { get; }
    public string? IdValue { get; }
    public PseudoStates State { get; set; }
    public bool Disableable { get; set; }
    public string? Text { get; set; }
    public El? Host { get; set; }

    public CssInlineStyle? Inline { get; set; }

    /// <summary>表现提示（<see cref="StyleElement.PresentationalHints"/>）。</summary>
    public CssInlineStyle? Hints { get; set; }

    /// <summary>为真的表单 / 文档状态伪类。</summary>
    public HashSet<PseudoClass> StatePseudoClasses { get; } = new();

    public StyleDirection Dir { get; set; }

    public string? Lang { get; set; }

    /// <summary>上一次计算的样式（容器查询读取）。</summary>
    public ComputedStyle? Style0 { get; set; }

    /// <summary>作为查询容器的内容盒尺寸；null 表示尚未布局。</summary>
    public (float W, float H)? ContainerSize { get; set; }

    public El Attr(string name, string value) { _attrs[name] = value; return this; }

    /// <summary>替换 class 列表（动画测试中模拟样式变化）。</summary>
    public El SetClasses(string classes)
    {
        _classes.Clear();
        _classes.AddRange(classes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        return this;
    }

    public El Style(string inlineStyle) { Inline = CssInlineStyle.Parse(inlineStyle); return this; }
    public El Part(string name) { _parts.Add(name); return this; }
    public El With(PseudoStates states) { State = states; return this; }

    public El Add(params El[] children)
    {
        foreach (var c in children)
        {
            c._parent = this;
            _children.Add(c);
        }
        return this;
    }

    public IReadOnlyList<El> Children => _children;
    public El this[int i] => _children[i];

    public override string Tag => TagName;
    public override string? Id => IdValue;
    public override IReadOnlyList<string> Classes => _classes;
    public override string? GetAttribute(string name) => _attrs.TryGetValue(name, out var v) ? v : null;
    public override StyleElement? Parent => _parent;

    public override StyleElement? PreviousSibling
    {
        get
        {
            if (_parent is null) return null;
            int i = _parent._children.IndexOf(this);
            return i > 0 ? _parent._children[i - 1] : null;
        }
    }

    public override StyleElement? NextSibling
    {
        get
        {
            if (_parent is null) return null;
            int i = _parent._children.IndexOf(this);
            return i + 1 < _parent._children.Count ? _parent._children[i + 1] : null;
        }
    }

    public override PseudoStates States => State;
    public override bool IsEmpty => _children.Count == 0 && string.IsNullOrEmpty(Text);
    public override bool CanBeDisabled => Disableable;
    public override IReadOnlyList<string> Parts => _parts;
    public override StyleElement? PartHost => Host;
    public override CssInlineStyle? InlineStyle => Inline;
    public override CssInlineStyle? PresentationalHints => Hints;
    public override StyleElement? FirstChild => _children.Count > 0 ? _children[0] : null;
    public override StyleDirection Direction => Dir;
    public override string? Language => Lang;
    public override ComputedStyle? CurrentStyle => Style0;

    public override bool MatchesStatePseudoClass(PseudoClass pseudoClass)
        => StatePseudoClasses.Contains(pseudoClass) || (pseudoClass == PseudoClass.ReadOnly && !StatePseudoClasses.Contains(PseudoClass.ReadWrite))
           || pseudoClass == PseudoClass.Defined;

    public override bool TryGetContainerSize(out float width, out float height)
    {
        width = ContainerSize?.W ?? 0;
        height = ContainerSize?.H ?? 0;
        return ContainerSize is not null;
    }

    public El Remove(El child)
    {
        _children.Remove(child);
        child._parent = null;
        return this;
    }

    public override string ToString() => TagName + (IdValue is null ? "" : "#" + IdValue) + string.Concat(_classes.Select(c => "." + c));
}
