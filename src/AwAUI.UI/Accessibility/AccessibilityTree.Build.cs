using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.Css.Values;
using AwAUI.Platform;

namespace AwAUI.UI.Accessibility;

public sealed partial class AccessibilityTree
{
    static readonly HashSet<string> s_nameFromContents = new(StringComparer.Ordinal)
    {
        "button", "link", "checkbox", "radio", "switch", "tab", "menuitem", "menuitemcheckbox",
        "menuitemradio", "option", "treeitem", "heading", "cell", "columnheader", "rowheader",
        "tooltip", "text", "statictext", "paragraph", "listitem",
    };

    static readonly char[] s_whitespace = { ' ', '\t', '\n', '\r', '\f' };

    readonly Dictionary<string, Element> _labelsByFor = new(StringComparer.Ordinal);
    readonly HashSet<string> _usedAccessKeys = new(StringComparer.OrdinalIgnoreCase);

    void Rebuild()
    {
        _buildCount++;
        _nodes.Clear();
        _byId.Clear();
        _elementsById.Clear();
        _labelsByFor.Clear();
        _usedAccessKeys.Clear();
        _rootNode = null;

        var rootElement = _root.RootElement;
        CollectElementIds(rootElement);
        Visit(rootElement, null, forceRoot: true);
        if (_rootNode is null)
        {
            // 根被完全过滤时仍然暴露一个空的根节点，便于平台桥挂接。
            var fallback = CreateNode(rootElement, explicitRoleName: null, forceRoot: true);
            _nodes.Add(fallback);
            _byId[fallback.Id] = fallback;
            _rootNode = fallback;
        }
        _selectionVersion = _root.Selection.SelectionVersion;
        _dirty = false;
        PublishDiffEvents();
    }

    void CollectElementIds(Element root)
    {
        var stack = new Stack<Element>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e.Id is { Length: > 0 } id && !_elementsById.ContainsKey(id)) _elementsById.Add(id, e);
            if (string.Equals(e.Tag, "label", StringComparison.OrdinalIgnoreCase))
            {
                // 同时接受 HTML 的 for 与 JSX 风格的 htmlFor。
                var target = Trimmed(e.GetAttribute("for")) ?? Trimmed(e.GetAttribute("htmlfor"));
                if (target is not null)
                {
                    foreach (var targetId in SplitIds(target))
                        if (!_labelsByFor.ContainsKey(targetId)) _labelsByFor.Add(targetId, e);
                }
            }
            var children = e.Children;
            for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }

    AccessibilityNode? Visit(Element element, AccessibilityNode? parent, bool forceRoot)
    {
        if (!forceRoot)
        {
            if (IsAriaHidden(element)) return null; // 整棵子树被剔除
            if (element.Render?.Style is { } style && style.Display == CssDisplay.None) return null;
        }

        var explicitRole = GetExplicitRole(element);
        bool presentation = !forceRoot && (explicitRole == "presentation" || explicitRole == "none");
        bool semantic = forceRoot || (!presentation && IsSemantic(element, explicitRole));

        AccessibilityNode? node = null;
        if (semantic)
        {
            node = CreateNode(element, explicitRole, forceRoot);
            if (parent is null) _rootNode = node;
            else parent.AddChild(node);
            _nodes.Add(node);
            if (!_byId.ContainsKey(node.Id)) _byId.Add(node.Id, node);
        }

        // 装饰性节点不产生语义节点，但它的子元素仍挂到最近的语义祖先下；文本不产生匿名节点。
        if (forceRoot && IsAriaHidden(element)) return node; // 根自身隐藏：保留（隐藏的）根，但不暴露其内容
        var effectiveParent = node ?? parent;
        var children = element.Children;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.IsTextNode) continue;
            Visit(child, effectiveParent, forceRoot: false);
        }
        return node;
    }

    AccessibilityNode CreateNode(Element element, string? explicitRoleName, bool forceRoot)
    {
        var node = new AccessibilityNode(IdFor(element), element, this);

        string role;
        if (forceRoot && element.Parent is null && explicitRoleName is null) role = "root";
        else role = explicitRoleName ?? InferRole(element) ?? (HasExplicitName(element) ? ContainerRole(element) : "generic");
        node.Role = role;

        char? mnemonic;
        node.Name = ComputeName(element, role, out mnemonic);
        node.Description = ComputeDescription(element, node.Name);
        node.Value = ComputeValue(element);
        node.State = ComputeState(element, role);
        node.Actions = ComputeActions(element, role, node.State);
        node.LabelledBy = SplitIds(element.GetAttribute("aria-labelledby"));
        node.Controls = SplitIds(element.GetAttribute("aria-controls"));
        node.DescribedBy = SplitIds(element.GetAttribute("aria-describedby"));
        node.LiveRegion = ComputeLiveRegion(element, role);
        node.HeadingLevel = ComputeHeadingLevel(element, role);
        node.PosInSet = ParseInt(element.GetAttribute("aria-posinset"));
        node.SetSize = ParseInt(element.GetAttribute("aria-setsize"));
        node.Text = element is InputElement input ? new AccessibilityTextInfo(input) : null;

        bool staticText = IsStaticTextRole(role);
        node.IsStaticText = staticText;
        node.TextPattern = staticText || role == "heading" ? ComputeTextPattern(element) : null;

        ComputeAccessKey(element, mnemonic, node);
        node.ActiveDescendant = FirstId(element.GetAttribute("aria-activedescendant"));
        node.HasPopup = ComputeHasPopup(element);
        node.Current = ComputeCurrent(element);
        node.Orientation = Token(element, "aria-orientation");
        node.Level = ParseInt(element.GetAttribute("aria-level"));
        node.ValueMin = ParseDouble(element.GetAttribute("aria-valuemin")) ?? ParseDouble(element.GetAttribute("min"));
        node.ValueMax = ParseDouble(element.GetAttribute("aria-valuemax")) ?? ParseDouble(element.GetAttribute("max"));
        node.ValueStep = ParseDouble(element.GetAttribute("step"));
        node.KeyShortcuts = Trimmed(element.GetAttribute("aria-keyshortcuts"));
        node.IsModal = Truthy(element.GetAttribute("aria-modal"));
        node.Expanded = TriState(element.GetAttribute("aria-expanded"));
        node.Pressed = TriState(element.GetAttribute("aria-pressed"));
        node.Selected = TriState(element.GetAttribute("aria-selected"));
        node.Checked = TriState(element.GetAttribute("aria-checked"));
        node.Busy = TriState(element.GetAttribute("aria-busy"));
        node.Invalid = ComputeInvalid(element);
        node.Atomic = Truthy(element.GetAttribute("aria-atomic"));
        node.Relevant = Trimmed(element.GetAttribute("aria-relevant"));
        node.Bounds = element.GetBoundingClientRect();
        return node;
    }

    // ------------------------------------------------------------ 过滤与角色

    static bool IsAriaHidden(Element element)
    {
        var value = element.GetAttribute("aria-hidden");
        return value is not null && string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    static string? GetExplicitRole(Element element)
    {
        var raw = element.GetAttribute("role");
        if (raw is null) return null;
        var trimmed = raw.Trim();
        return trimmed.Length == 0 ? null : trimmed.ToLowerInvariant();
    }

    static bool IsSemantic(Element element, string? explicitRole)
    {
        if (explicitRole is not null) return true;
        if (InferRole(element) is not null) return true;
        if (element.IsFocusable) return true;
        if (HasClickHandler(element)) return true;
        if (HasExplicitName(element)) return true;
        return HasRelationshipOrStateAttributes(element);
    }

    static bool HasClickHandler(Element element) => element.GetHandler(EventKind.Click) is not null;

    static bool HasExplicitName(Element element)
    {
        if (Trimmed(element.GetAttribute("aria-label")) is not null) return true;
        if (Trimmed(element.GetAttribute("aria-labelledby")) is not null) return true;
        if (element is ImageElement image && !string.IsNullOrEmpty(image.Alt)) return true;
        if (Trimmed(element.GetAttribute("alt")) is not null) return true;
        return Trimmed(element.GetAttribute("title")) is not null;
    }

    static bool HasRelationshipOrStateAttributes(Element element)
    {
        return element.GetAttribute("aria-expanded") is not null
            || element.GetAttribute("aria-pressed") is not null
            || element.GetAttribute("aria-checked") is not null
            || element.GetAttribute("aria-selected") is not null
            || element.GetAttribute("aria-live") is not null
            || element.GetAttribute("aria-posinset") is not null
            || element.GetAttribute("aria-setsize") is not null
            || element.GetAttribute("aria-valuenow") is not null
            || element.GetAttribute("aria-valuetext") is not null
            || element.GetAttribute("aria-valuemin") is not null
            || element.GetAttribute("aria-valuemax") is not null
            || element.GetAttribute("aria-controls") is not null
            || element.GetAttribute("aria-describedby") is not null
            || element.GetAttribute("aria-activedescendant") is not null
            || element.GetAttribute("aria-haspopup") is not null
            || element.GetAttribute("aria-current") is not null
            || element.GetAttribute("aria-orientation") is not null
            || element.GetAttribute("aria-level") is not null
            || element.GetAttribute("aria-modal") is not null
            || element.GetAttribute("aria-keyshortcuts") is not null
            || element.GetAttribute("aria-busy") is not null
            || element.GetAttribute("aria-invalid") is not null
            || element.GetAttribute("aria-atomic") is not null
            || element.GetAttribute("aria-relevant") is not null
            || element.GetAttribute("accesskey") is not null;
    }

    static string? InferRole(Element element)
    {
        if (element is TextNodeElement) return null;

        // 段落与文本块是只读静态文本；仅供布局的 Div 等容器不会命中这里。
        if (element is TextElement) return "statictext";

        if (element is ButtonElement) return "button";
        if (element is InputElement input) return InputRole(input);
        if (element is ImageElement image)
        {
            // alt="" 是显式的装饰性图片；缺少 alt 仍按 img 报告，便于审计。
            if (string.Equals(image.Alt, "", StringComparison.Ordinal) && !HasExplicitName(image)) return null;
            return "img";
        }
        if (element is SvgElement)
            return HasExplicitName(element) ? "img" : null;

        switch (element.Tag.ToLowerInvariant())
        {
            case "a": return element.GetAttribute("href") is not null ? "link" : null;
            case "img": return "img";
            case "h1": case "h2": case "h3": case "h4": case "h5": case "h6": return "heading";
            case "ul": case "ol": return "list";
            case "li": return "listitem";
            case "table": return "table";
            case "thead": case "tbody": case "tfoot": return "rowgroup";
            case "tr": return "row";
            case "td": return "cell";
            case "th": return "columnheader";
            case "select": return "combobox";
            case "option": return "option";
            case "dialog": return "dialog";
            case "nav": return "navigation";
            case "main": return "main";
            case "article": return "article";
            case "aside": return "complementary";
            case "header": return "banner";
            case "footer": return "contentinfo";
            case "progress": return "progressbar";
            case "hr": return "separator";
            case "summary": return "button";
            case "menu": return "menu";
            case "textarea": return "textbox";
            case "section": return HasExplicitName(element) ? "region" : null;
            case "form": return HasExplicitName(element) ? "form" : null;
        }

        if (HasClickHandler(element)) return "button";
        if (HasExplicitName(element)) return ContainerRole(element);
        return null;
    }

    static string InputRole(InputElement input)
    {
        if (input.IsMultiline) return "textbox";
        var type = input.InputType?.ToLowerInvariant();
        switch (type)
        {
            case "checkbox": return "checkbox";
            case "radio": return "radio";
            case "range": return "slider";
            case "number": return "spinbutton";
            case "search": return "searchbox";
            default: return "textbox";
        }
    }

    static string ContainerRole(Element element)
    {
        switch (element.Tag.ToLowerInvariant())
        {
            case "section": return "region";
            case "form": return "form";
            case "article": return "article";
            case "nav": return "navigation";
            case "main": return "main";
            default: return "group";
        }
    }

    // ------------------------------------------------------------ 名称、描述、值

    string? ComputeName(Element element, string role)
    {
        char? ignored;
        return ComputeName(element, role, out ignored);
    }

    /// <summary>
    /// 按 ARIA 的名称计算顺序得出可访问名称：
    /// <c>aria-labelledby</c> → 关联的 <c>label</c>（for 或包裹）→ <c>aria-label</c> → <c>alt</c> → 文本内容 → <c>title</c>。
    /// 内容中的 Windows 风格 <c>&amp;X</c> 助记符会被去掉，并通过 <paramref name="mnemonic"/> 返回访问键。
    /// </summary>
    string? ComputeName(Element element, string role, out char? mnemonic)
    {
        mnemonic = null;

        // 1. aria-labelledby：按 id 解析；全部断链时继续走后续规则。
        var labelledBy = element.GetAttribute("aria-labelledby");
        if (labelledBy is not null)
        {
            var parts = new List<string>();
            var visited = new HashSet<Element>();
            foreach (var id in SplitIds(labelledBy))
            {
                if (!_elementsById.TryGetValue(id, out var referenced)) continue;
                var name = ReferenceName(referenced, visited);
                if (name is not null) parts.Add(name);
            }
            if (parts.Count > 0) return string.Join(" ", parts.ToArray());
        }

        // 2. 关联标签：for 指向本元素，或包裹本元素的 label。
        var labelElement = FindLabelElement(element);
        if (labelElement is not null)
        {
            var labelText = Normalize(CollectVisibleText(labelElement));
            if (labelText.Length > 0) return StripMnemonic(labelText, out mnemonic);
        }

        // 3. aria-label。
        var ariaLabel = Trimmed(element.GetAttribute("aria-label"));
        if (ariaLabel is not null) return ariaLabel;

        // 4. 图片替代文本。
        if (element is ImageElement image && !string.IsNullOrEmpty(image.Alt))
        {
            var alt = Trimmed(image.Alt);
            if (alt is not null) return alt;
        }

        var altAttribute = Trimmed(element.GetAttribute("alt"));
        if (altAttribute is not null) return altAttribute;

        // 5. 文本内容（按钮、链接、标题等，或无独立语义后代的容器）。
        if (s_nameFromContents.Contains(role) || (role != "root" && !HasSemanticDescendant(element)))
        {
            var text = Normalize(CollectVisibleText(element));
            if (text.Length > 0) return StripMnemonic(text, out mnemonic);
        }

        // 6. title 回退。
        return Trimmed(element.GetAttribute("title"));
    }

    /// <summary>
    /// 找出与本元素关联的 <c>label</c>：先按 <c>for</c> 匹配，再沿祖先寻找包裹式 label。
    /// 标签自身、或没有文本的标签不会贡献名称。
    /// </summary>
    Element? FindLabelElement(Element element)
    {
        if (element.Id is { Length: > 0 } id && _labelsByFor.TryGetValue(id, out var byFor)) return byFor;
        for (var parent = element.Parent; parent is not null; parent = parent.Parent)
        {
            if (string.Equals(parent.Tag, "label", StringComparison.OrdinalIgnoreCase)) return parent;
        }
        return null;
    }

    /// <summary>
    /// 去掉 Windows 风格的 <c>&amp;X</c> 助记符标记并返回访问键字符。<c>&amp;&amp;</c> 表示字面量 <c>&amp;</c>。
    /// 只取第一个有效助记符作为访问键，后续标记仍会被去掉。
    /// </summary>
    static string StripMnemonic(string text, out char? key)
    {
        key = null;
        int amp = text.IndexOf('&');
        if (amp < 0) return text;

        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c != '&')
            {
                sb.Append(c);
                i++;
                continue;
            }
            if (i + 1 < text.Length && text[i + 1] == '&')
            {
                sb.Append('&');
                i += 2;
                continue;
            }
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && !key.HasValue)
            {
                char candidate = text[i + 1];
                key = char.ToUpperInvariant(candidate);
                sb.Append(candidate);
                i += 2;
                continue;
            }
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                sb.Append(text[i + 1]);
                i += 2;
                continue;
            }
            sb.Append('&');
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 子树中是否有会单独成为语义节点的后代。没有时（纯文本/装饰性盒子）文本可以直接作为本节点的名称，
    /// 这样实时区域与只含文本的容器也向辅助技术暴露可读内容；列表、表格等有语义子项时则不会把全部文本当作名称。
    /// </summary>
    static bool HasSemanticDescendant(Element element)
    {
        var stack = new Stack<Element>();
        var children = element.Children;
        for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        while (stack.Count > 0)
        {
            var child = stack.Pop();
            if (child.IsTextNode || IsAriaHidden(child)) continue;
            var role = GetExplicitRole(child);
            bool presentation = role == "presentation" || role == "none";
            if (!presentation && IsSemantic(child, role)) return true;
            var kids = child.Children;
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
        }
        return false;
    }

    string? ReferenceName(Element element, HashSet<Element> visited)
    {
        if (!visited.Add(element)) return null;

        var label = Trimmed(element.GetAttribute("aria-label"));
        if (label is not null) return label;

        var labelledBy = element.GetAttribute("aria-labelledby");
        if (labelledBy is not null)
        {
            var parts = new List<string>();
            foreach (var id in SplitIds(labelledBy))
            {
                if (!_elementsById.TryGetValue(id, out var referenced)) continue;
                var name = ReferenceName(referenced, visited);
                if (name is not null) parts.Add(name);
            }
            if (parts.Count > 0) return string.Join(" ", parts.ToArray());
        }

        if (element is ImageElement image && !string.IsNullOrEmpty(image.Alt))
        {
            var alt = Trimmed(image.Alt);
            if (alt is not null) return alt;
        }

        var altAttribute = Trimmed(element.GetAttribute("alt"));
        if (altAttribute is not null) return altAttribute;

        var text = Normalize(CollectVisibleText(element));
        if (text.Length > 0)
        {
            char? ignored;
            return StripMnemonic(text, out ignored);
        }

        return Trimmed(element.GetAttribute("title"));
    }

    string? ComputeDescription(Element element, string? name)
    {
        var description = Trimmed(element.GetAttribute("aria-description"));
        if (description is not null) return description;

        var describedBy = element.GetAttribute("aria-describedby");
        if (describedBy is not null)
        {
            var parts = new List<string>();
            var visited = new HashSet<Element>();
            foreach (var id in SplitIds(describedBy))
            {
                if (!_elementsById.TryGetValue(id, out var referenced)) continue;
                var text = ReferenceName(referenced, visited);
                if (text is not null) parts.Add(text);
            }
            if (parts.Count > 0) return string.Join(" ", parts.ToArray());
        }

        var title = Trimmed(element.GetAttribute("title"));
        if (title is not null && !string.Equals(title, name, StringComparison.Ordinal)) return title;
        return null;
    }

    static string? ComputeValue(Element element)
    {
        if (element is InputElement input)
        {
            var valueText = Trimmed(element.GetAttribute("aria-valuetext"));
            if (valueText is not null) return valueText;
            var valueNow = Trimmed(element.GetAttribute("aria-valuenow"));
            if (valueNow is not null) return valueNow;
            return input.Value;
        }
        var text = Trimmed(element.GetAttribute("aria-valuetext"));
        if (text is not null) return text;
        return Trimmed(element.GetAttribute("aria-valuenow"));
    }

    static bool IsStaticTextRole(string role) => role is
        "statictext" or "text" or "paragraph";

    AccessibilityTextPatternInfo? ComputeTextPattern(Element element)
    {
        if (element is not TextElement) return null;
        var value = Normalize(CollectVisibleText(element));
        int start = 0, end = 0;
        if (_root.Selection.TryGetSelectionRange(element, out int rangeStart, out int rangeEnd))
        {
            start = rangeStart;
            end = rangeEnd;
        }
        return new AccessibilityTextPatternInfo(value, start, end);
    }

    /// <summary>
    /// 计算访问键：显式 <c>accesskey</c> 优先（约定为 Ctrl+Alt；原文为大写字母时再加 Shift），
    /// 否则使用名称中的 <c>&amp;X</c> 助记符（Alt）。同一语义树中重复的访问键只保留第一个节点。
    /// </summary>
    void ComputeAccessKey(Element element, char? mnemonic, AccessibilityNode node)
    {
        char? key = mnemonic;
        var modifiers = AccessKeyModifiers.Alt;

        var explicitKey = FirstAccessKeyChar(element.GetAttribute("accesskey"));
        if (explicitKey.HasValue)
        {
            key = char.ToUpperInvariant(explicitKey.Value);
            modifiers = AccessKeyModifiers.Ctrl | AccessKeyModifiers.Alt;
            if (char.IsUpper(explicitKey.Value)) modifiers |= AccessKeyModifiers.Shift;
        }

        if (!key.HasValue) return;
        string text = char.ToUpperInvariant(key.Value).ToString();
        if (!_usedAccessKeys.Add(text)) return;
        node.AccessKey = text;
        node.AccessKeyModifiers = modifiers;
    }

    static char? FirstAccessKeyChar(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        foreach (char c in value!)
            if (!char.IsWhiteSpace(c)) return c;
        return null;
    }

    static string? FirstId(string? value)
    {
        var ids = SplitIds(value);
        return ids.Count == 0 ? null : ids[0];
    }

    static string? Token(Element element, string name) => Trimmed(element.GetAttribute(name))?.ToLowerInvariant();

    static string? ComputeHasPopup(Element element)
    {
        var value = Token(element, "aria-haspopup");
        if (value is null || value == "false") return null;
        if (value == "true") return "menu"; // ARIA 1.1 的 true 等价于 menu
        return value is "menu" or "listbox" or "tree" or "grid" or "dialog" ? value : null;
    }

    static string? ComputeCurrent(Element element)
    {
        var value = Token(element, "aria-current");
        if (value is null || value == "false") return null;
        return value == "true" || value is "page" or "step" or "location" or "date" or "time" ? value : null;
    }

    static string? ComputeInvalid(Element element)
    {
        var value = Token(element, "aria-invalid");
        if (value is null || value == "false") return null;
        return value;
    }

    /// <summary>三态属性：true / false；mixed 与缺失返回 null（mixed 由 <see cref="AccessibilityState.Mixed"/> 表达）。</summary>
    static bool? TriState(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (EqualsIgnoreCase(trimmed, "true")) return true;
        if (EqualsIgnoreCase(trimmed, "false")) return false;
        return null;
    }

    static double? ParseDouble(string? value)
    {
        if (value is null) return null;
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : null;
    }

    static string CollectVisibleText(Element root)
    {
        var sb = new StringBuilder();
        if (root is TextElement rootText && rootText.Content is { Length: > 0 } rootContent)
            sb.Append(rootContent).Append(' ');
        var stack = new Stack<Element>();
        var children = root.Children;
        for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            if (IsAriaHidden(element)) continue;
            if (element is TextNodeElement textNode)
            {
                sb.Append(textNode.Text).Append(' ');
                continue;
            }
            if (element is ImageElement image && !string.IsNullOrEmpty(image.Alt))
            {
                sb.Append(image.Alt).Append(' ');
                continue;
            }
            if (element is TextElement textElement && textElement.Content is { Length: > 0 } content)
                sb.Append(content).Append(' ');
            var kids = element.Children;
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
        }
        return sb.ToString();
    }

    static string Normalize(string text)
    {
        if (text.Length == 0) return "";
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    static string? Trimmed(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    static IReadOnlyList<string> SplitIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        return value!.Split(s_whitespace, StringSplitOptions.RemoveEmptyEntries);
    }

    // ------------------------------------------------------------ 状态与动作

    static AccessibilityState ComputeState(Element element, string role)
    {
        var state = AccessibilityState.None;

        if (element.IsDisabled || Truthy(element.GetAttribute("aria-disabled")) || Truthy(element.GetAttribute("disabled")))
            state |= AccessibilityState.Disabled;

        var checkedValue = element.GetAttribute("aria-checked");
        if (EqualsIgnoreCase(checkedValue, "mixed")) state |= AccessibilityState.Mixed;
        else if (EqualsIgnoreCase(checkedValue, "true")) state |= AccessibilityState.Checked;

        if (EqualsIgnoreCase(element.GetAttribute("aria-selected"), "true")) state |= AccessibilityState.Selected;
        if (EqualsIgnoreCase(element.GetAttribute("aria-expanded"), "true")) state |= AccessibilityState.Expanded;
        if (EqualsIgnoreCase(element.GetAttribute("aria-pressed"), "true") || EqualsIgnoreCase(element.GetAttribute("aria-pressed"), "mixed"))
            state |= AccessibilityState.Pressed;

        var invalid = element.GetAttribute("aria-invalid");
        if (EqualsIgnoreCase(invalid, "true") || EqualsIgnoreCase(invalid, "grammar") || EqualsIgnoreCase(invalid, "spelling"))
            state |= AccessibilityState.Invalid;

        if (EqualsIgnoreCase(element.GetAttribute("aria-busy"), "true")) state |= AccessibilityState.Busy;
        if (IsAriaHidden(element) || element.GetAttribute("hidden") is not null) state |= AccessibilityState.Hidden;
        if (element.IsFocused) state |= AccessibilityState.Focused;
        if (Truthy(element.GetAttribute("aria-required")) || Truthy(element.GetAttribute("required")))
            state |= AccessibilityState.Required;
        if ((element is InputElement input && input.ReadOnly) || Truthy(element.GetAttribute("aria-readonly"))
            || Truthy(element.GetAttribute("readonly")))
            state |= AccessibilityState.ReadOnly;
        if (Truthy(element.GetAttribute("aria-multiselectable"))) state |= AccessibilityState.MultiSelectable;
        if (ComputeLiveRegion(element, role) != AccessibilityLiveRegion.Off) state |= AccessibilityState.LiveRegion;
        if (IsStaticTextRole(role) || role == "heading") state |= AccessibilityState.ReadOnly;
        return state;
    }

    static AccessibilityAction ComputeActions(Element element, string role, AccessibilityState state)
    {
        var actions = AccessibilityAction.None;
        bool disabled = (state & AccessibilityState.Disabled) != 0;

        if (HasClickHandler(element) || IsInvokableRole(role)) actions |= AccessibilityAction.Invoke;
        if (element.IsFocusable) actions |= AccessibilityAction.Focus;
        if (role is "checkbox" or "radio" or "switch" || element.GetAttribute("aria-pressed") is not null)
            actions |= AccessibilityAction.Toggle;
        if (element is InputElement input && !input.IsDisabled && !input.ReadOnly)
            actions |= AccessibilityAction.SetValue;
        if (!disabled && (role is "slider" or "spinbutton"))
            actions |= AccessibilityAction.Increment | AccessibilityAction.Decrement;
        if (HasScrollAncestor(element)) actions |= AccessibilityAction.ScrollIntoView;
        if (element.GetAttribute("aria-expanded") is not null)
            actions |= AccessibilityAction.Expand | AccessibilityAction.Collapse;
        return actions;
    }

    static bool IsInvokableRole(string role) => role is
        "button" or "link" or "checkbox" or "radio" or "switch" or "tab" or "option"
        or "menuitem" or "menuitemcheckbox" or "menuitemradio" or "treeitem";

    static bool HasScrollAncestor(Element element)
    {
        for (var e = element.Parent; e is not null; e = e.Parent)
            if (e.Parent is null || RenderPipeline.ScrollAxes(e) != 0) return true;
        return false;
    }

    static AccessibilityLiveRegion ComputeLiveRegion(Element element, string role)
    {
        var live = element.GetAttribute("aria-live")?.Trim().ToLowerInvariant();
        if (live == "assertive") return AccessibilityLiveRegion.Assertive;
        if (live == "polite") return AccessibilityLiveRegion.Polite;
        if (live == "off") return AccessibilityLiveRegion.Off;
        if (role is "alert" or "alertdialog") return AccessibilityLiveRegion.Assertive;
        if (role is "status" or "log" or "marquee" or "timer") return AccessibilityLiveRegion.Polite;
        return AccessibilityLiveRegion.Off;
    }

    static int? ComputeHeadingLevel(Element element, string role)
    {
        if (role != "heading") return null;
        var aria = ParseInt(element.GetAttribute("aria-level"));
        if (aria is not null) return aria;
        var tag = element.Tag;
        if (tag.Length == 2 && (tag[0] == 'h' || tag[0] == 'H') && tag[1] >= '1' && tag[1] <= '6')
            return tag[1] - '0';
        return 2; // ARIA 的 aria-level 默认值
    }

    static bool Truthy(string? value)
    {
        if (value is null) return false;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return true; // HTML 布尔属性
        return !string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase);
    }

    static bool EqualsIgnoreCase(string? value, string expected) =>
        value is not null && string.Equals(value.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    static int? ParseInt(string? value)
    {
        if (value is null) return null;
        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : null;
    }

    static string IdFor(Element element) => element.Id ?? "el-" + element.UniqueId.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------ 平台快照

    AccessibilitySnapshot BuildSnapshotCore()
    {
        var nodes = new AccessibilityNodeSnapshot[_nodes.Count];
        for (int i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            var childIds = new string[node.Children.Count];
            for (int c = 0; c < childIds.Length; c++) childIds[c] = node.Children[c].Id;
            nodes[i] = new AccessibilityNodeSnapshot
            {
                Id = node.Id,
                Role = node.Role,
                Name = node.Name,
                Description = node.Description,
                Value = node.Value,
                States = StateNames(node.State),
                Actions = ActionKinds(node.Actions),
                Bounds = node.Bounds,
                ParentId = node.Parent?.Id,
                ChildIds = childIds,
                LabelledBy = node.LabelledBy,
                Controls = node.Controls,
                DescribedBy = node.DescribedBy,
                LiveRegion = ToPlatformPoliteness(node.LiveRegion),
                HeadingLevel = node.HeadingLevel,
                PosInSet = node.PosInSet,
                SetSize = node.SetSize,
                Text = node.Text is { } text ? ToTextSnapshot(text) : null,
            };
        }
        return new AccessibilitySnapshot { Version = _version, RootId = _rootNode?.Id, Nodes = nodes };
    }

    static IReadOnlyList<string> StateNames(AccessibilityState state)
    {
        if (state == AccessibilityState.None) return Array.Empty<string>();
        var names = new List<string>(4);
        if ((state & AccessibilityState.Disabled) != 0) names.Add("disabled");
        if ((state & AccessibilityState.Checked) != 0) names.Add("checked");
        if ((state & AccessibilityState.Mixed) != 0) names.Add("mixed");
        if ((state & AccessibilityState.Selected) != 0) names.Add("selected");
        if ((state & AccessibilityState.Expanded) != 0) names.Add("expanded");
        if ((state & AccessibilityState.Pressed) != 0) names.Add("pressed");
        if ((state & AccessibilityState.Invalid) != 0) names.Add("invalid");
        if ((state & AccessibilityState.Busy) != 0) names.Add("busy");
        if ((state & AccessibilityState.Hidden) != 0) names.Add("hidden");
        if ((state & AccessibilityState.Focused) != 0) names.Add("focused");
        if ((state & AccessibilityState.Required) != 0) names.Add("required");
        if ((state & AccessibilityState.ReadOnly) != 0) names.Add("readonly");
        if ((state & AccessibilityState.MultiSelectable) != 0) names.Add("multiselectable");
        if ((state & AccessibilityState.LiveRegion) != 0) names.Add("live");
        return names;
    }

    static IReadOnlyList<AccessibilityActionKind> ActionKinds(AccessibilityAction actions)
    {
        if (actions == AccessibilityAction.None) return Array.Empty<AccessibilityActionKind>();
        var list = new List<AccessibilityActionKind>(4);
        if ((actions & AccessibilityAction.Invoke) != 0) list.Add(AccessibilityActionKind.Invoke);
        if ((actions & AccessibilityAction.Focus) != 0) list.Add(AccessibilityActionKind.Focus);
        if ((actions & AccessibilityAction.Toggle) != 0) list.Add(AccessibilityActionKind.Toggle);
        if ((actions & AccessibilityAction.SetValue) != 0) list.Add(AccessibilityActionKind.SetValue);
        if ((actions & AccessibilityAction.Increment) != 0) list.Add(AccessibilityActionKind.Increment);
        if ((actions & AccessibilityAction.Decrement) != 0) list.Add(AccessibilityActionKind.Decrement);
        if ((actions & AccessibilityAction.ScrollIntoView) != 0) list.Add(AccessibilityActionKind.ScrollIntoView);
        if ((actions & AccessibilityAction.Expand) != 0) list.Add(AccessibilityActionKind.Expand);
        if ((actions & AccessibilityAction.Collapse) != 0) list.Add(AccessibilityActionKind.Collapse);
        return list;
    }

    static AccessibilityTextSnapshot ToTextSnapshot(AccessibilityTextInfo text) => new()
    {
        Value = text.Value,
        SelectionStart = text.SelectionStart,
        SelectionEnd = text.SelectionEnd,
        SelectionAnchor = text.SelectionAnchor,
        SelectionFocus = text.SelectionFocus,
        SelectedText = text.SelectedText,
        IsComposing = text.IsComposing,
        CompositionText = text.CompositionText,
        CompositionStart = text.CompositionStart,
        CompositionSelectionStart = text.CompositionSelectionStart,
        CompositionSelectionEnd = text.CompositionSelectionEnd,
    };

    static AccessibilityPoliteness ToPlatformPoliteness(AccessibilityLiveRegion live) => live switch
    {
        AccessibilityLiveRegion.Polite => AccessibilityPoliteness.Polite,
        AccessibilityLiveRegion.Assertive => AccessibilityPoliteness.Assertive,
        _ => AccessibilityPoliteness.Off,
    };

    static AccessibilityAction FromActionKind(AccessibilityActionKind action) => action switch
    {
        AccessibilityActionKind.Invoke => AccessibilityAction.Invoke,
        AccessibilityActionKind.Focus => AccessibilityAction.Focus,
        AccessibilityActionKind.Toggle => AccessibilityAction.Toggle,
        AccessibilityActionKind.SetValue => AccessibilityAction.SetValue,
        AccessibilityActionKind.Increment => AccessibilityAction.Increment,
        AccessibilityActionKind.Decrement => AccessibilityAction.Decrement,
        AccessibilityActionKind.ScrollIntoView => AccessibilityAction.ScrollIntoView,
        AccessibilityActionKind.Expand => AccessibilityAction.Expand,
        AccessibilityActionKind.Collapse => AccessibilityAction.Collapse,
        _ => AccessibilityAction.None,
    };

    static AccessibilityAction ToActionFlag(AccessibilityActionKind action) => FromActionKind(action);
}
