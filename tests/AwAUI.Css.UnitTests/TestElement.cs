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

    public El Attr(string name, string value) { _attrs[name] = value; return this; }

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

    public override string ToString() => TagName + (IdValue is null ? "" : "#" + IdValue) + string.Concat(_classes.Select(c => "." + c));
}
