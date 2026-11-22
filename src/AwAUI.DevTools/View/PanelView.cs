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
                        new Div
                        {
                            Class = "dt-left",
                            Children =
                            [
                                new SearchBar(_m),
                                new TreeView(_m, _refs),
                            ],
                        },
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
/// 元素面板顶部的搜索框：输入去抖 / 回车调用 <c>Tree.search</c>，结果按「命中原因 + 短标签」列出；
/// 选中一条会展开祖先、选中节点并滚动到应用里的元素。空查询清空结果。
/// </summary>
internal sealed class SearchBar : Component
{
    readonly InspectorModel _m;

    public SearchBar(InspectorModel m) => _m = m;

    protected override Node Build()
    {
        _ = _m.SearchRevision.Value;
        var children = new NodeList
        {
            new Input
            {
                Id = "dt-search",
                Class = "dt-search-input",
                Value = _m.SearchQuery,
                Placeholder = "Search tag, id, class, text or selector",
                OnInput = e => _m.SetSearchQuery(e.Value),
                OnSubmit = e => _m.SubmitSearch(e.Value),
                OnKeyDown = e =>
                {
                    if (e.PhysicalKey != PhysicalKey.Escape) return;
                    e.PreventDefault();
                    _m.ClearSearch();
                },
            },
        };
        if (_m.SearchQuery.Length > 0 || _m.SearchResults is not null)
            children.Add(new Button
            {
                Id = "dt-search-clear",
                Class = "dt-search-clear",
                OnClick = _ => _m.ClearSearch(),
                Children = ["×"],
            });
        if (Results() is { } results) children.Add(results);
        return new Div { Class = "dt-search", Children = children };
    }

    Node? Results()
    {
        if (_m.SearchError is null && !_m.Searching && _m.SearchResults is null) return null;
        var list = new NodeList();
        if (_m.SearchError is { } error)
        {
            list.Add(new UIText { Class = "dt-error", Id = "dt-search-error", Children = [error] });
        }
        else if (_m.Searching)
        {
            list.Add(new UIText { Class = "dt-search-note", Children = ["Searching…"] });
        }
        else if (_m.SearchResults is { Count: 0 } && !_m.SearchTruncated)
        {
            list.Add(new UIText { Class = "dt-search-note", Id = "dt-search-empty", Children = ["No results for \"" + TreeView.Clip(_m.SearchQuery, 60) + "\"."] });
        }
        else if (_m.SearchResults is { } found)
        {
            foreach (var result in found) list.Add(ResultRow(result));
            if (_m.SearchTruncated)
                list.Add(new UIText { Class = "dt-search-note", Children = ["More results are not shown."] });
        }
        return new Div { Class = "dt-search-results", Id = "dt-search-results", Children = list };
    }

    Node ResultRow(SearchResult result)
    {
        var button = new Button
        {
            Key = result.NodeId,
            Class = "dt-search-result",
            OnClick = _ => _m.SelectSearchResult(result),
            Children =
            [
                new Span { Class = "dt-search-match", Children = [result.Match] },
                new Span { Class = "dt-search-label", Children = [TreeView.Clip(result.Label, 120)] },
            ],
        };
        button.Id = "dt-search-result-" + result.NodeId.ToString(CultureInfo.InvariantCulture);
        button.Attributes["data-search-node"] = result.NodeId.ToString(CultureInfo.InvariantCulture);
        return button;
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
            SideTab.Stylesheets => new Button { Class = "dt-mini", Id = "dt-sheets-reload", OnClick = _ => _m.ReloadStyleSheets(), Children = ["Reload"] },
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
                    Children =
                    [
                        Tab("Styles", SideTab.Styles), Tab("Computed", SideTab.Computed), Tab("Attributes", SideTab.Attributes),
                        Tab("Events", SideTab.Events), Tab("Stylesheets", SideTab.Stylesheets), Tab("Components", SideTab.Components),
                        new Div { Class = "dt-tab-spacer" }, action,
                    ],
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
                            SideTab.Attributes => new AttributesView(_m, _refs),
                            SideTab.Events => new EventsView(_m, _refs),
                            SideTab.Stylesheets => new StylesheetsView(_m, _refs),
                            SideTab.Components => new ComponentsView(_m, _refs),
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
        if (_m.PseudoError is { } pseudoError) content.Add(new UIText { Class = "dt-error", Id = "dt-pseudo-error", Children = [pseudoError] });
        if (_m.Selected is not null) content.Add(PseudoBar());
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

    /// <summary>强制伪状态的开关（状态列表取自 <c>StyleInspector.ForceablePseudoStates</c>）；全部关闭时发空数组。</summary>
    Node PseudoBar()
    {
        var toggles = new NodeList();
        foreach (var state in InspectorModel.ForceablePseudoStates)
        {
            var button = new Button
            {
                Key = state,
                Class = _m.IsPseudoStateForced(state) ? "dt-pseudo on" : "dt-pseudo",
                OnClick = _ => _m.TogglePseudoState(state),
                Children = [":" + state],
            };
            button.Id = "dt-pseudo-" + state;
            button.Attributes["data-pseudo"] = state;
            toggles.Add(button);
        }
        return new Div
        {
            Class = "dt-pseudo-bar",
            Children =
            [
                new Div
                {
                    Class = "dt-pseudo-head",
                    Children =
                    [
                        new UIText { Class = "dt-pseudo-title", Children = ["Force state"] },
                        new Div { Class = "dt-spacer" },
                        new UIText { Class = "dt-pseudo-clear", Children = ["clear"], OnClick = _ => _m.ClearPseudoStates() },
                    ],
                },
                new Div { Class = "dt-pseudo-list", Children = toggles },
            ],
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

/// <summary>
/// 属性页：id、class 与元素的其它属性，点值即编辑（回车提交、Esc 取消、失焦提交），用
/// <c>Tree.setAttribute</c> / <c>Tree.removeAttribute</c> / <c>Tree.setClasses</c> 写回。只读属性的错误（-32004）直接显示。
/// </summary>
internal sealed class AttributesView : Component
{
    readonly InspectorModel _m;
    readonly PanelRefs _refs;

    public AttributesView(InspectorModel m, PanelRefs refs)
    {
        _m = m;
        _refs = refs;
    }

    protected override Node Build()
    {
        _ = _m.StylesRevision.Value;
        var content = new NodeList();
        if (_m.AttributesError is { } error)
            content.Add(new UIText { Class = "dt-error", Id = "dt-attributes-error", Children = [error] });
        if (_m.Selected is null)
        {
            content.Add(new UIText { Class = "dt-note", Children = ["Select an element in the tree, or click Select and pick one in the app."] });
        }
        else if (_m.Selected.IsText)
        {
            content.Add(new UIText { Class = "dt-note", Children = ["Text nodes have no attributes."] });
        }
        else
        {
            var node = _m.Selected;
            content.Add(new UIText { Class = "dt-section-title", Children = ["Attributes"] });
            content.Add(Row(AttributeEditKind.Id, "id", node.ElementId ?? ""));
            content.Add(Row(AttributeEditKind.Class, "class", node.Classes is { Length: > 0 } classes ? string.Join(" ", classes) : ""));
            if (node.Attributes is { } attrs)
                foreach (var kv in attrs)
                    content.Add(Row(AttributeEditKind.Attribute, kv.Key, kv.Value));
            if (_m.AttributeEditing is { Kind: AttributeEditKind.NewAttribute })
                content.Add(new Div { Class = "dt-attr-row", Children = [new AttrEditBox(_m, "name=value") { Key = "new" }] });
            else
                content.Add(new Div
                {
                    Class = "dt-attr-add",
                    Id = "dt-attr-add",
                    OnClick = _ => _m.BeginAddAttribute(),
                    Children = [new UIText { Children = ["+ add attribute"] }],
                });
        }
        return new Div
        {
            Class = "dt-scroll-content",
            Style = "top:" + TreeView.Px(-_m.SideScrollTop),
            Children = content,
        };
    }

    Node Row(AttributeEditKind kind, string name, string value)
    {
        bool editing = _m.AttributeEditing is { } edit && edit.Matches(kind, name);
        Node shown;
        if (editing)
        {
            shown = new AttrEditBox(_m, kind == AttributeEditKind.Attribute ? "value" : name) { Key = "edit" };
        }
        else
        {
            var text = new UIText
            {
                Class = "dt-attr-value",
                Children = [value.Length == 0 ? "\"\"" : TreeView.Clip(value, 160)],
            };
            text.OnClick = _ => Begin(kind, name, value);
            text.Attributes["data-attr-value"] = name;
            shown = text;
        }
        var row = new Div
        {
            Key = name,
            Class = "dt-attr-row",
            Children =
            [
                new UIText { Class = "dt-attr-name", Children = [name] },
                new UIText { Class = "dt-attr-eq", Children = ["="] },
                shown,
                new Button
                {
                    Class = "dt-attr-remove",
                    OnClick = _ => _m.RemoveAttribute(name),
                    Children = ["×"],
                },
            ],
        };
        row.Attributes["data-attr"] = name;
        return row;
    }

    void Begin(AttributeEditKind kind, string name, string value)
    {
        switch (kind)
        {
            case AttributeEditKind.Id: _m.BeginIdEdit(); break;
            case AttributeEditKind.Class: _m.BeginClassEdit(); break;
            default: _m.BeginAttributeEdit(name, value); break;
        }
    }
}

/// <summary>属性编辑框：挂载时获得焦点；回车提交，Esc 取消，失去焦点时提交。</summary>
internal sealed class AttrEditBox : Component
{
    readonly InspectorModel _m;
    readonly string _placeholder;

    public AttrEditBox(InspectorModel m, string placeholder)
    {
        _m = m;
        _placeholder = placeholder;
    }

    protected override Node Build() => new Input
    {
        Class = "dt-attr-edit",
        Id = "dt-attr-edit",
        Value = _m.AttributeDraft,
        Placeholder = _placeholder,
        OnInput = e => _m.AttributeDraft = e.Value,
        OnSubmit = e => _m.CommitAttributeEdit(e.Value),
        OnKeyDown = e =>
        {
            if (e.PhysicalKey != PhysicalKey.Escape) return;
            e.PreventDefault();
            _m.CancelAttributeEdit();
        },
        OnBlur = _ => _m.CommitAttributeEdit(_m.AttributeDraft),
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

/// <summary>
/// 样式表页：列出 <c>Css.getStyleSheets</c> 的样式表（来源 / 标题、origin、disabled、editable），
/// 选中后经 <c>Css.getStyleSheetText</c> 载入文本，在等宽多行编辑器里修改，Apply 调用 <c>Css.setStyleSheetText</c> 并显示诊断。
/// 只读样式表的编辑器不可编辑。外部修改（<c>Css.styleSheetChanged</c>）会刷新文本，但不会覆盖未保存的草稿。
/// </summary>
internal sealed class StylesheetsView : Component
{
    readonly InspectorModel _m;
    readonly PanelRefs _refs;

    public StylesheetsView(InspectorModel m, PanelRefs refs)
    {
        _m = m;
        _refs = refs;
    }

    protected override Node Build()
    {
        _ = _m.SheetsRevision.Value;
        var content = new NodeList();
        if (_m.StyleSheetsError is { } error)
            content.Add(new UIText { Class = "dt-error", Id = "dt-sheets-error", Children = [error] });
        if (_m.StyleSheets is not { } sheets)
        {
            content.Add(new UIText { Class = "dt-note", Id = "dt-sheets-loading", Children = ["Loading style sheets…"] });
        }
        else if (sheets.Count == 0)
        {
            content.Add(new UIText { Class = "dt-note", Id = "dt-sheets-empty", Children = ["No style sheets."] });
        }
        else
        {
            content.Add(new UIText { Class = "dt-section-title", Id = "dt-sheets-count", Children = [sheets.Count + (sheets.Count == 1 ? " style sheet" : " style sheets")] });
            var list = new NodeList();
            for (int i = 0; i < sheets.Count; i++) list.Add(SheetRow(sheets[i], i));
            content.Add(new Div { Class = "dt-sheet-list", Id = "dt-sheet-list", Children = list });
        }
        if (_m.SelectedSheetId is { } selected)
            content.Add(EditorPane(selected, _m.StyleSheets?.Find(e => e.SheetId == selected)));
        else if (_m.StyleSheets is { Count: > 0 })
            content.Add(new UIText { Class = "dt-note", Children = ["Select a style sheet to edit its text."] });
        return new Div
        {
            Class = "dt-scroll-content",
            Style = "top:" + TreeView.Px(-_m.SideScrollTop),
            Children = content,
        };
    }

    Node SheetRow(StyleSheetEntry sheet, int index)
    {
        var parts = new NodeList
        {
            new UIText { Class = "dt-sheet-name", Children = [TreeView.Clip(sheet.DisplayName, 100)] },
            new UIText { Class = "dt-sheet-origin", Children = [sheet.Origin] },
        };
        if (sheet.Disabled) parts.Add(new UIText { Class = "dt-sheet-badge", Children = ["disabled"] });
        if (!sheet.Editable) parts.Add(new UIText { Class = "dt-sheet-badge", Children = ["read-only"] });
        var row = new Button
        {
            Key = sheet.SheetId,
            Id = "dt-sheet-" + index.ToString(CultureInfo.InvariantCulture),
            Class = sheet.SheetId == _m.SelectedSheetId ? "dt-sheet-row on" : "dt-sheet-row",
            OnClick = _ => _m.SelectStyleSheet(sheet.SheetId),
            Children = parts,
        };
        row.Attributes["data-sheet"] = sheet.SheetId;
        return row;
    }

    Node EditorPane(string sheetId, StyleSheetEntry? sheet)
    {
        var content = new NodeList();
        if (sheet is null)
        {
            content.Add(new UIText { Class = "dt-note", Children = ["Style sheet " + TreeView.Clip(sheetId, 60) + " is not in the list any more."] });
            return new Div { Class = "dt-sheet-editor-pane", Children = content };
        }
        content.Add(new UIText
        {
            Class = "dt-sheet-editor-title",
            Id = "dt-sheet-editor-title",
            Children = [TreeView.Clip(sheet.DisplayName, 100) + (sheet.Editable ? "" : "  (read-only)")],
        });
        content.Add(new TextArea
        {
            Id = "dt-sheet-editor",
            Class = "dt-sheet-editor",
            Value = _m.SheetDraft,
            ReadOnly = !sheet.Editable,
            OnInput = e => _m.SetSheetDraft(e.Value),
        });
        var actions = new NodeList
        {
            new Button
            {
                Id = "dt-sheet-apply",
                Class = "dt-sheet-apply",
                Disabled = _m.SheetApplying || !sheet.Editable,
                OnClick = _ => _m.ApplyStyleSheet(),
                Children = [_m.SheetApplying ? "Applying…" : "Apply"],
            },
        };
        if (_m.SheetTextLoading) actions.Add(new UIText { Class = "dt-sheet-note", Id = "dt-sheet-text-loading", Children = ["Loading text…"] });
        else if (_m.SheetDirty) actions.Add(new UIText { Class = "dt-sheet-dirty", Id = "dt-sheet-dirty", Children = ["Unsaved changes"] });
        else if (_m.SheetExternalChange) actions.Add(new UIText { Class = "dt-sheet-note", Id = "dt-sheet-external", Children = ["Changed on the page; Apply keeps your text."] });
        content.Add(new Div { Class = "dt-sheet-actions", Children = actions });

        if (_m.SheetTextError is { } error)
            content.Add(new UIText { Class = "dt-error", Id = "dt-sheet-text-error", Children = [error] });
        if (_m.SheetDiagnostics is { } diagnostics)
        {
            var rows = new NodeList();
            if (diagnostics.Count == 0)
                rows.Add(new UIText { Class = "dt-sheet-note", Id = "dt-sheet-no-diagnostics", Children = ["Applied, no diagnostics."] });
            else
                foreach (var d in diagnostics) rows.Add(DiagnosticRow(d));
            content.Add(new Div { Class = "dt-sheet-diagnostics", Id = "dt-sheet-diagnostics", Children = rows });
        }
        return new Div { Class = "dt-sheet-editor-pane", Id = "dt-sheet-editor-pane", Children = content };
    }

    static Node DiagnosticRow(StyleSheetDiagnostic d)
    {
        var parts = new NodeList
        {
            new UIText { Class = "dt-sheet-diag-severity", Children = [d.Severity] },
        };
        if (d.Code is { Length: > 0 } code)
            parts.Add(new UIText { Class = "dt-sheet-diag-code", Children = [code] });
        if (d.Line > 0)
            parts.Add(new UIText
            {
                Class = "dt-sheet-diag-pos",
                Children = [d.Line.ToString(CultureInfo.InvariantCulture) + ":" + d.Column.ToString(CultureInfo.InvariantCulture)],
            });
        parts.Add(new UIText { Class = "dt-sheet-diag-message", Children = [TreeView.Clip(d.Message, 200)] });
        return new Div
        {
            Key = d.Code ?? d.Message,
            Class = "dt-sheet-diag " + d.Severity,
            Children = parts,
        };
    }
}

/// <summary>
/// 组件页：把镜像里带 <c>ComponentName</c> 的元素折叠成组件实例树（最近的组件祖先为父实例），
/// 顶部显示实例总数（超过上限时标注截断）；点击一项在元素树里选中它的代表节点。
/// </summary>
internal sealed class ComponentsView : Component
{
    readonly InspectorModel _m;
    readonly PanelRefs _refs;

    public ComponentsView(InspectorModel m, PanelRefs refs)
    {
        _m = m;
        _refs = refs;
    }

    protected override Node Build()
    {
        _ = _m.TreeRevision.Value;
        var content = new NodeList();
        int count = _m.ComponentCount;
        string summary = count + (count == 1 ? " component instance" : " component instances") + (_m.ComponentsTruncated ? " (truncated)" : "");
        content.Add(new UIText { Class = "dt-section-title", Id = "dt-components-count", Children = [summary] });
        if (_m.SelectedComponentNodeId != 0)
        {
            var node = _m.Tree.Get(_m.SelectedComponentNodeId);
            string label = node is null ? "node " + _m.SelectedComponentNodeId.ToString(CultureInfo.InvariantCulture) : "<" + (node.Tag ?? "?") + (node.ElementId is { } eid ? "#" + eid : "") + ">";
            content.Add(new UIText
            {
                Class = "dt-components-detail",
                Id = "dt-components-detail",
                Children = [(_m.SelectedComponentName ?? "?") + "  ·  node " + _m.SelectedComponentNodeId.ToString(CultureInfo.InvariantCulture) + "  ·  " + label],
            });
        }
        else
        {
            content.Add(new UIText { Class = "dt-note", Children = ["Select a component instance to reveal it in the element tree."] });
        }
        var roots = _m.Components;
        if (roots.Count == 0)
        {
            content.Add(new UIText { Class = "dt-note", Id = "dt-components-empty", Children = ["No component instances."] });
        }
        else
        {
            var rows = new NodeList();
            // 实例树已展开为列表（迭代，避免很深的组件嵌套耗尽栈）
            var stack = new Stack<ComponentInstance>();
            for (int i = roots.Count - 1; i >= 0; i--) stack.Push(roots[i]);
            while (stack.Count > 0)
            {
                var instance = stack.Pop();
                rows.Add(Row(instance));
                for (int i = instance.Children.Count - 1; i >= 0; i--) stack.Push(instance.Children[i]);
            }
            content.Add(new Div { Class = "dt-component-list", Id = "dt-component-list", Children = rows });
        }
        return new Div
        {
            Class = "dt-scroll-content",
            Style = "top:" + TreeView.Px(-_m.SideScrollTop),
            Children = content,
        };
    }

    Node Row(ComponentInstance instance)
    {
        var node = instance.Node;
        string label = "<" + (node.Tag ?? "?") + (node.ElementId is { } eid ? "#" + eid : "") + ">";
        var row = new Button
        {
            Key = node.Id,
            Id = "dt-component-" + node.Id.ToString(CultureInfo.InvariantCulture),
            Class = instance.Node.Id == _m.SelectedComponentNodeId ? "dt-component-row on" : "dt-component-row",
            Style = "padding-left:" + (6 + Math.Min(instance.Depth, 40) * 12).ToString(CultureInfo.InvariantCulture) + "px",
            OnClick = _ => _m.SelectComponent(instance),
            Children =
            [
                new Span { Class = "dt-component-name", Children = [TreeView.Clip(instance.Name, 80)] },
                new Span { Class = "dt-component-node", Children = ["  " + TreeView.Clip(label, 80)] },
            ],
        };
        row.Attributes["data-component-node"] = node.Id.ToString(CultureInfo.InvariantCulture);
        return row;
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
