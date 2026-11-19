using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.Css.Values;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.DevTools;

/// <summary>视图需要在布局之后量取的元素（滚动区域的视口与内容）。</summary>
internal sealed class PanelRefs
{
    public readonly Ref<Element> TreeViewport = new();
    public readonly Ref<Element> SideViewport = new();
    public readonly Ref<Element> BoxPane = new();

    /// <summary>面板根元素（可聚焦，面板快捷键挂在它的 keydown 上）。</summary>
    public readonly Ref<Element> Root = new();
}

/// <summary>面板根组件：工具栏、元素树 | 右侧页签（样式 / 计算值 / 事件）+ 盒模型、状态栏。</summary>
internal sealed class PanelView : Component
{
    readonly InspectorModel _m;
    readonly PanelRefs _refs;
    readonly Action<KeyEvent>? _onKey;

    public PanelView(InspectorModel model, PanelRefs refs, Action<KeyEvent>? onKey = null)
    {
        _m = model;
        _refs = refs;
        _onKey = onKey;
    }

    protected override Node Build() =>
        new Div
        {
            Class = "dt",
            // 可程序聚焦（不在 Tab 顺序中）：点击面板的任何地方都让焦点落在面板内，按键冒泡到这里
            TabIndex = -1,
            Ref = _refs.Root,
            OnKeyDown = _onKey,
            Children =
            [
                new Toolbar(_m),
                new Div
                {
                    Class = "dt-main",
                    Children =
                    [
                        new TreeView(_m, _refs),
                        new SidePane(_m, _refs),
                    ],
                },
                new StatusBar(_m),
            ],
        };
}

/// <summary>工具栏：拾取按钮、标题、连接状态。</summary>
internal sealed class Toolbar : Component
{
    readonly InspectorModel _m;

    public Toolbar(InspectorModel m) => _m = m;

    protected override Node Build()
    {
        _ = _m.ChromeRevision.Value;
        return new Div
        {
            Class = "dt-toolbar",
            Children =
            [
                new Button
                {
                    Id = "dt-pick",
                    Class = _m.Picking ? "dt-tool on" : "dt-tool",
                    OnClick = _ => _m.TogglePick(),
                    Children = [new Div { Class = "dt-pick-icon" }, "Select"],
                },
                new Div { Class = "dt-sep" },
                new UIText { Class = "dt-title", Children = ["Elements"] },
                new Div { Class = "dt-spacer" },
                new Div
                {
                    Class = "dt-conn",
                    Children =
                    [
                        new Div { Class = _m.Connected ? "dt-dot ok" : "dt-dot" },
                        new UIText
                        {
                            Id = "dt-connection",
                            Children = [_m.Connected ? (_m.AppName ?? "app") + "  ·  " + (_m.TargetId ?? "") : _m.Status],
                        },
                    ],
                },
            ],
        };
    }
}

/// <summary>
/// 元素树（虚拟化）：只为视口内的行（加一行余量）创建元素，滚动只改变哪些行被描述，元素数量与树的大小无关。
/// 行按节点 id 作 Key，滚动时复用存活元素。
/// </summary>
internal sealed class TreeView : Component
{
    readonly InspectorModel _m;
    readonly PanelRefs _refs;
    float _dragStartY, _dragStartScroll;
    bool _dragging;

    public TreeView(InspectorModel m, PanelRefs refs)
    {
        _m = m;
        _refs = refs;
    }

    /// <summary>缩进封顶的层数（更深的节点不再右移，避免极深的树把内容推出视口）。</summary>
    const int MaxIndentLevels = 48;

    /// <summary>属性值在行内显示的最大长度。</summary>
    const int MaxShownValue = 60;

    protected override Node Build()
    {
        _ = _m.TreeRevision.Value;
        _m.ApplyPendingTreeClamp();
        var rows = _m.Tree.Rows;
        const float rh = InspectorModel.RowHeight;
        var children = new NodeList();
        if (!_m.Loaded)
        {
            children.Add(new UIText { Class = "dt-empty", Children = [_m.Status] });
        }
        else
        {
            int first = Math.Max(0, (int)(_m.TreeScrollTop / rh));
            int count = (int)Math.Ceiling(_m.TreeViewportHeight / rh) + 1;
            int last = Math.Min(rows.Count, first + count);
            var list = new NodeList();
            for (int i = first; i < last; i++) list.Add(Row(rows[i]));
            float offset = first * rh - _m.TreeScrollTop;
            children.Add(new Div
            {
                Class = "dt-rows",
                Style = "top:" + Px(offset),
                Children = list,
            });
            if (Scrollbar(rows.Count * rh) is { } bar) children.Add(bar);
        }
        return new Div
        {
            Class = "dt-tree",
            Id = "dt-tree",
            Ref = _refs.TreeViewport,
            OnPointerLeave = _ => _m.Hover(0),
            // 虚拟化的树由模型自己滚动（只描述可见的行）：接管滚轮，阻止滚动系统的默认滚动
            OnWheel = e =>
            {
                _m.ScrollTreeBy(e.DeltaY);
                e.PreventDefault();
            },
            Children = children,
        };
    }

    Node Row(in TreeRow row)
    {
        var n = row.Node;
        bool twisty = n.HasChildren && n.InlineText is null;
        var arrow = twisty
            ? new Div
            {
                Class = "dt-twisty",
                OnClick = e =>
                {
                    e.StopPropagation();
                    _m.Toggle(n);
                },
                Children = [new Div { Class = n.Expanded ? "dt-arrow-down" : "dt-arrow-right" }],
            }
            : new Div { Class = "dt-twisty" };
        int indent = 4 + Math.Min(row.Depth, MaxIndentLevels) * 12;
        var div = new Div
        {
            Key = n.Id,
            Class = ReferenceEquals(n, _m.Selected) ? "dt-row selected" : "dt-row",
            Style = "padding-left:" + indent.ToString(CultureInfo.InvariantCulture) + "px",
            OnClick = _ => _m.Select(n),
            OnPointerEnter = _ => _m.Hover(n.Id),
            Children = [arrow, Label(n)],
        };
        div.Attributes["data-node"] = n.Id.ToString(CultureInfo.InvariantCulture);
        return div;
    }

    static Node Label(DomNode n)
    {
        var t = new UIText { Class = "dt-label" };
        if (n.IsText)
        {
            t.Children.Add(new Span { Class = "t-text", Children = ["\"" + Clip(n.Text ?? "", 120) + (n.TextTruncated ? "…" : "") + "\""] });
            return t;
        }
        string tag = n.Tag ?? "?";
        t.Children.Add(new Span { Class = "t-tag", Children = ["<" + tag] });
        if (n.ElementId is { } id) Attr(t, "id", id);
        if (n.Classes is { Length: > 0 } cls) Attr(t, "class", string.Join(" ", cls));
        if (n.Attributes is { } attrs)
            foreach (var kv in attrs) Attr(t, kv.Key, kv.Value);
        t.Children.Add(new Span { Class = "t-tag", Children = [">"] });
        if (n.InlineText is { } text)
        {
            t.Children.Add(new Span { Class = "t-text", Children = [Clip(text.Text ?? "", TreeMirror.MaxInlineTextLength)] });
            t.Children.Add(new Span { Class = "t-tag", Children = ["</" + tag + ">"] });
        }
        else if (!n.HasChildren)
        {
            t.Children.Add(new Span { Class = "t-tag", Children = ["</" + tag + ">"] });
        }
        else if (!n.Expanded)
        {
            t.Children.Add(new Span { Class = "t-dim", Children = ["…"] });
            t.Children.Add(new Span { Class = "t-tag", Children = ["</" + tag + ">"] });
        }
        // 组件的第一个元素旁标出组件名（元素属于哪个组件）
        if (n.ComponentName is { } comp && n.Parent?.ComponentName != comp)
            t.Children.Add(new Span { Class = "t-comp", Children = ["  " + Clip(comp, 60)] });
        return t;
    }

    static void Attr(UIText t, string name, string value)
    {
        t.Children.Add(new Span { Class = "t-attr", Children = [" " + Clip(name, MaxShownValue)] });
        t.Children.Add(new Span { Class = "t-tag", Children = ["=\""] });
        t.Children.Add(new Span { Class = "t-val", Children = [Clip(value, MaxShownValue)] });
        t.Children.Add(new Span { Class = "t-tag", Children = ["\""] });
    }

    internal static string Clip(string s, int max)
    {
        // 控制字符（换行等）显示成空格，避免一行变多行
        var sb = new StringBuilder(Math.Min(s.Length, max + 1));
        foreach (char c in s)
        {
            if (sb.Length >= max)
            {
                sb.Append('…');
                break;
            }
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        if (sb.Length > 0 && char.IsHighSurrogate(sb[sb.Length - 1])) sb.Length--;
        return sb.ToString();
    }

    Node? Scrollbar(float contentHeight)
    {
        float vh = _m.TreeViewportHeight;
        if (contentHeight <= vh + 0.5f) return null;
        float thumb = Math.Max(24, vh * vh / contentHeight);
        float max = _m.MaxTreeScroll;
        float top = max <= 0 ? 0 : _m.TreeScrollTop / max * (vh - thumb);
        return new Div
        {
            Key = "scrollbar",
            Class = "dt-scrollbar",
            OnPointerDown = e =>
            {
                // 点在滑块外：翻一页
                if (e.Target is { } t && t.HasClass("dt-thumb")) return;
                var box = _refs.TreeViewport.Current?.BoundingRect ?? default;
                float y = e.Position.Y - box.Y;
                _m.ScrollTreeBy(y < top ? -vh : vh);
            },
            Children =
            [
                new Div
                {
                    Key = "thumb",
                    Class = "dt-thumb",
                    Style = "top:" + Px(top) + ";height:" + Px(thumb),
                    OnPointerDown = e =>
                    {
                        _dragging = true;
                        _dragStartY = e.Position.Y;
                        _dragStartScroll = _m.TreeScrollTop;
                        e.CurrentTarget?.SetPointerCapture(e.PointerId);
                    },
                    OnPointerMove = e =>
                    {
                        if (!_dragging) return;
                        float track = Math.Max(1, _m.TreeViewportHeight - thumb);
                        _m.ScrollTreeTo(_dragStartScroll + (e.Position.Y - _dragStartY) / track * _m.MaxTreeScroll);
                    },
                    OnPointerUp = e =>
                    {
                        _dragging = false;
                        e.CurrentTarget?.ReleasePointerCapture(e.PointerId);
                    },
                },
            ],
        };
    }

    internal static string Px(float v) =>
        (float.IsNaN(v) || float.IsInfinity(v) ? 0 : v).ToString("0.##", CultureInfo.InvariantCulture) + "px";
}

/// <summary>右侧：页签、可滚动的内容区、盒模型图。</summary>
internal sealed class SidePane : Component
{
    readonly InspectorModel _m;
    readonly PanelRefs _refs;

    public SidePane(InspectorModel m, PanelRefs refs)
    {
        _m = m;
        _refs = refs;
    }

    protected override Node Build()
    {
        _ = _m.ChromeRevision.Value;
        var tab = _m.Tab;
        Node Tab(string label, SideTab t) => new Div
        {
            Key = label,
            Id = "dt-tab-" + label.ToLowerInvariant(),
            Class = tab == t ? "dt-tab on" : "dt-tab",
            OnClick = _ => _m.SetTab(t),
            Children = [new UIText { Children = [label] }],
        };
        Node? action = tab switch
        {
            SideTab.Styles => new Button { Class = "dt-mini", Id = "dt-reset", OnClick = _ => _m.ClearOverrides(), Children = ["Reset edits"] },
            SideTab.Computed => new Button { Class = "dt-mini", OnClick = _ => _m.ToggleShowAllComputed(), Children = [_m.ShowAllComputed ? "Hide initial" : "Show all"] },
            _ => null,
        };
        return new Div
        {
            Class = "dt-side",
            Children =
            [
                new Div
                {
                    Class = "dt-tabs",
                    Children = [Tab("Styles", SideTab.Styles), Tab("Computed", SideTab.Computed), Tab("Events", SideTab.Events), new Div { Class = "dt-tab-spacer" }, action],
                },
                new Div
                {
                    Class = "dt-scroll",
                    Id = "dt-side",
                    Ref = _refs.SideViewport,
                    OnWheel = e =>
                    {
                        _m.ScrollSideBy(e.DeltaY);
                        e.PreventDefault();
                    },
                    Children =
                    [
                        tab switch
                        {
                            SideTab.Computed => new ComputedView(_m, _refs),
                            SideTab.Events => new EventsView(_m, _refs),
                            _ => new StylesView(_m, _refs),
                        },
                    ],
                },
                new BoxModelView(_m, _refs),
            ],
        };
    }
}

/// <summary>样式页：<c>element.style</c>、匹配的规则（按级联优先级从高到低）与继承的规则；值可点击编辑，勾选框开关声明。</summary>
internal sealed class StylesView : Component
{
    readonly InspectorModel _m;
    readonly PanelRefs _refs;

    public StylesView(InspectorModel m, PanelRefs refs)
    {
        _m = m;
        _refs = refs;
    }

    protected override Node Build()
    {
        _ = _m.StylesRevision.Value;
        var content = new NodeList();
        if (_m.StylesError is { } err) content.Add(new UIText { Class = "dt-error", Id = "dt-styles-error", Children = [err] });
        if (_m.Selected is null)
        {
            content.Add(new UIText { Class = "dt-note", Children = ["Select an element in the tree, or click Select and pick one in the app."] });
        }
        else if (_m.Styles is { } styles)
        {
            if (_m.Selected.IsText) content.Add(new UIText { Class = "dt-note", Children = ["Text node: showing the styles of its parent element."] });
            foreach (var section in styles.Sections)
            {
                if (section.Title is { } title) content.Add(new UIText { Class = "dt-section-title", Children = [title] });
                for (int i = 0; i < section.Blocks.Count; i++) content.Add(Block(section.Blocks[i], i));
            }
        }
        return new Div
        {
            Class = "dt-scroll-content",
            Style = "top:" + TreeView.Px(-_m.SideScrollTop),
            Children = content,
        };
    }

    Node Block(StyleBlock b, int index)
    {
        var lines = new NodeList();
        var head = new Div
        {
            Class = "dt-rule-head",
            Children =
            [
                new UIText
                {
                    Class = "dt-selector",
                    Children = [TreeView.Clip(b.Selector, 200) + (b.Media is { } media ? "  @media " + TreeView.Clip(media, 60) : "") + " {"],
                },
                b.Source is { } src ? new UIText { Class = "dt-source", Children = [TreeView.Clip(src, 60)] } : null,
            ],
        };
        lines.Add(head);
        for (int i = 0; i < b.Decls.Count; i++)
        {
            var d = b.Decls[i];
            if (b.Inherited && !StylesSnapshot.IsInheritedProperty(d.Name)) continue;
            lines.Add(Decl(b, d, i));
        }
        if (_m.Editing is { } edit && edit.Matches(b, -1))
            lines.Add(new Div { Key = "new", Class = "dt-decl", Children = [new EditBox(_m, "name: value") { Key = "edit" }] });
        else if (b.Editable && !b.Inherited)
            lines.Add(new Div
            {
                Key = "add",
                Class = "dt-add",
                OnClick = _ => _m.BeginEdit(b, -1),
                Children = [new UIText { Children = ["+ add property"] }],
            });
        lines.Add(new UIText { Class = "dt-brace", Children = ["}"] });
        var div = new Div
        {
            Key = (b.RuleId ?? "inline") + "@" + index.ToString(CultureInfo.InvariantCulture),
            Class = "dt-rule",
            Children = lines,
        };
        div.Attributes["data-rule"] = b.RuleId ?? "inline";
        return div;
    }

    Node Decl(StyleBlock b, StyleDecl d, int i)
    {
        bool editing = _m.Editing is { } edit && edit.Matches(b, i);
        bool overridden = d.Status == "overridden" && !d.Disabled;
        bool bad = d.Status is "invalid" or "unsupported";
        var cls = "dt-decl" + (d.Disabled || d.Status == "inactive" ? " off" : "") + (bad ? " bad" : "");
        var parts = new NodeList();
        if (b.Editable && !b.Inherited)
            parts.Add(new Div
            {
                Class = d.Disabled ? "dt-check" : "dt-check on",
                OnClick = _ => _m.ToggleDeclaration(b, i),
            });
        else parts.Add(new Div { Style = "width:16px;flex-shrink:0" });

        var text = new NodeList
        {
            new UIText { Class = "dt-prop", Children = [TreeView.Clip(d.Name, 80)] },
            new UIText { Class = "dt-colon", Children = [": "] },
        };
        if (Swatch(d) is { } sw) text.Add(sw);
        if (editing) text.Add(new EditBox(_m, d.Value) { Key = "edit" });
        else
        {
            var value = new UIText
            {
                Class = "dt-value",
                Children = [TreeView.Clip(d.Value, 200) + (d.Important ? " !important" : "")],
            };
            if (b.Editable && !b.Inherited) value.OnClick = _ => _m.BeginEdit(b, i);
            value.Attributes["data-prop"] = d.Name;
            text.Add(value);
        }
        if (!editing) text.Add(new UIText { Class = "dt-colon", Children = [";"] });
        if (overridden) text.Add(new Div { Class = "dt-strike", Style = "left:0;right:0" });
        parts.Add(new Div { Style = "position:relative;display:flex;align-items:center", Children = text });
        if (bad) parts.Add(new UIText { Class = "dt-warn", Children = [d.Status + (d.Reason is { } r ? ": " + TreeView.Clip(r, 80) : "")] });
        var div = new Div { Key = d.Name + "#" + i.ToString(CultureInfo.InvariantCulture), Class = cls, Children = parts };
        div.Attributes["data-decl"] = d.Name;
        return div;
    }

    /// <summary>颜色值旁的色块（值先经 CSS 颜色解析器规范化，不会把对端的文本拼进面板的内联样式）。</summary>
    static Node? Swatch(StyleDecl d)
    {
        if (d.Value.Length > 64 || !CssColorParser.TryParse(d.Value.Trim(), out var color) || color.IsCurrentColor) return null;
        return new Div { Class = "dt-swatch", Style = "background-color:" + color };
    }
}

/// <summary>声明的编辑框：挂载时获得焦点；回车提交，Esc 取消，失去焦点时提交（同浏览器）。</summary>
internal sealed class EditBox : Component
{
    readonly InspectorModel _m;
    readonly string _placeholder;

    public EditBox(InspectorModel m, string placeholder)
    {
        _m = m;
        _placeholder = placeholder;
    }

    protected override Node Build() => new Input
    {
        Class = "dt-edit",
        Id = "dt-edit",
        Value = _m.EditDraft,
        Placeholder = _placeholder,
        OnInput = e => _m.EditDraft = e.Value,
        OnSubmit = e => _m.CommitEdit(e.Value),
        OnKeyDown = e =>
        {
            if (e.PhysicalKey != PhysicalKey.Escape) return;
            e.PreventDefault();
            _m.CancelEdit();
        },
        OnBlur = _ => _m.CommitEdit(_m.EditDraft),
    };

    protected override void OnMount()
    {
        foreach (var e in GetRootElements()) e.Focus();
    }
}

/// <summary>计算值页：默认只列出有声明设置的属性，「Show all」列出全部。</summary>
internal sealed class ComputedView : Component
{
    readonly InspectorModel _m;
    readonly PanelRefs _refs;

    public ComputedView(InspectorModel m, PanelRefs refs)
    {
        _m = m;
        _refs = refs;
    }

    protected override Node Build()
    {
        _ = _m.StylesRevision.Value;
        var list = new NodeList();
        if (_m.Selected is null) list.Add(new UIText { Class = "dt-note", Children = ["No element selected."] });
        else if (_m.Computed is { } computed)
            foreach (var p in computed)
            {
                if (!_m.ShowAllComputed && p.Origin is "initial" or "default") continue;
                list.Add(new Div
                {
                    Key = p.Name,
                    Class = "dt-computed-row",
                    Children =
                    [
                        new UIText { Class = "dt-computed-name", Children = [TreeView.Clip(p.Name, 60)] },
                        new UIText { Class = "dt-computed-value", Children = [TreeView.Clip(p.Value, 200)] },
                        new UIText { Class = "dt-computed-origin", Children = [p.Origin] },
                    ],
                });
            }
        return new Div
        {
            Class = "dt-scroll-content",
            Style = "top:" + TreeView.Px(-_m.SideScrollTop),
            Children = list,
        };
    }
}

/// <summary>事件页：最近的协议事件（方法名 + 截断的参数），新的在上。</summary>
internal sealed class EventsView : Component
{
    readonly InspectorModel _m;
    readonly PanelRefs _refs;

    public EventsView(InspectorModel m, PanelRefs refs)
    {
        _m = m;
        _refs = refs;
    }

    protected override Node Build()
    {
        _ = _m.LogRevision.Value;
        _ = _m.StylesRevision.Value;
        var entries = new List<LogEntry>(_m.Log);
        var list = new NodeList();
        if (entries.Count == 0) list.Add(new UIText { Class = "dt-note", Children = ["No protocol events yet."] });
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            list.Add(new Div
            {
                Key = e.Seq,
                Class = "dt-log-row",
                Children =
                [
                    new UIText { Class = "dt-log-method", Children = [e.Method] },
                    new UIText { Class = "dt-log-detail", Children = [TreeView.Clip(e.Detail, 160)] },
                ],
            });
        }
        return new Div
        {
            Class = "dt-scroll-content",
            Style = "top:" + TreeView.Px(-_m.SideScrollTop),
            Children = list,
        };
    }
}

/// <summary>盒模型图：margin / border / padding / content 四层，各边数值取自 <c>Layout.getBoxModel</c>。</summary>
internal sealed class BoxModelView : Component
{
    readonly InspectorModel _m;
    readonly PanelRefs _refs;

    public BoxModelView(InspectorModel m, PanelRefs refs)
    {
        _m = m;
        _refs = refs;
    }

    protected override Node Build()
    {
        _ = _m.StylesRevision.Value;
        Node body;
        if (_m.Box is { } b)
        {
            var content = new Div
            {
                Class = "dt-box-content",
                Id = "dt-box-content",
                Children = [new UIText { Children = [BoxSnapshot.Format(b.ContentWidth) + " × " + BoxSnapshot.Format(b.ContentHeight)] }],
            };
            var padding = Layer("padding", "dt-box-padding", b.Padding, content);
            var border = Layer("border", "dt-box-border", b.Border, padding);
            body = Layer("margin", "dt-box-margin", b.Margin, border);
        }
        else
        {
            body = new UIText { Class = "dt-note", Children = [_m.Selected is null ? "Box model" : "No layout box for this node."] };
        }
        return new Div { Class = "dt-boxpane", Ref = _refs.BoxPane, Children = [body] };
    }

    static Node Layer(string label, string cls, BoxSnapshot.Thickness t, Node inner) =>
        new Div
        {
            Class = "dt-box " + cls,
            Children =
            [
                new UIText { Class = "dt-box-label", Children = [label] },
                new Div { Class = "dt-box-row", Children = [new UIText { Children = [BoxSnapshot.Format(t.Top)] }] },
                new Div
                {
                    Class = "dt-box-mid",
                    Children =
                    [
                        new UIText { Class = "dt-box-side", Children = [BoxSnapshot.Format(t.Left)] },
                        inner,
                        new UIText { Class = "dt-box-side", Children = [BoxSnapshot.Format(t.Right)] },
                    ],
                },
                new Div { Class = "dt-box-row", Children = [new UIText { Children = [BoxSnapshot.Format(t.Bottom)] }] },
            ],
        };
}

/// <summary>状态栏：选中元素的祖先路径与镜像中的节点数。</summary>
internal sealed class StatusBar : Component
{
    readonly InspectorModel _m;

    public StatusBar(InspectorModel m) => _m = m;

    protected override Node Build()
    {
        _ = _m.TreeRevision.Value;
        _ = _m.ChromeRevision.Value;
        var crumbs = new List<string>();
        for (var n = _m.Selected; n is not null && crumbs.Count < 6; n = n.Parent) crumbs.Add(_m.Describe(n.Id));
        crumbs.Reverse();
        string path = crumbs.Count == 0 ? "" : string.Join("  ›  ", crumbs);
        string count = _m.Tree.Count.ToString(CultureInfo.InvariantCulture) + " nodes" + (_m.Tree.Truncated ? " (truncated)" : "");
        return new Div
        {
            Class = "dt-status",
            Children =
            [
                new UIText { Class = "dt-crumb", Id = "dt-crumbs", Children = [TreeView.Clip(path, 200)] },
                new Div { Class = "dt-spacer" },
                new UIText { Id = "dt-count", Children = [count] },
            ],
        };
    }
}
