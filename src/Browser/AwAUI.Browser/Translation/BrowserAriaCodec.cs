using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.Platform;

namespace AwAUI.Browser;

// 本文件属于 Translation/：只用 netstandard2.0 可用的 API，可由 tests/AwAUI.Browser.Tests 以源码链接方式在普通 .NET 上测试。

/// <summary>
/// 语义树快照 → 影子 ARIA DOM 模型（紧凑 JSON）的编码，以及 JS 胶水与 C# 之间共用的动作 / 实时区域编号。
/// 输出只含数据，不含 HTML：JS 端用 <c>textContent</c> 与 <c>setAttribute</c> 消费它。
/// <para>
/// JSON 形状（省略字段表示没有值）：
/// <code>
/// {"v":1,"sv":&lt;语义树版本&gt;,"r":"&lt;根 id&gt;","n":[
///   {"i":"id","r":"aria role","nm":"name","d":"description","v":"value",
///    "st":["checked"],"a":[0,1],"b":[x,y,w,h],"c":["child id"],
///    "lv":1,"lvl":2,"ps":1,"ss":10,"hp":"listbox","md":1,"ks":"Ctrl+K","ad":"active id",
///    "lb":["id"],"ct":["id"],"db":["id"]}
/// ]}
/// </code>
/// <c>lv</c> 1 = polite、2 = assertive；<c>a</c> 使用 <see cref="ActionInvoke"/> 等编号；
/// 坐标与尺寸是窗口客户区中的逻辑像素。
/// </para>
/// </summary>
internal static class BrowserAriaCodec
{
    /// <summary>JSON 结构的版本；与 JS 的 <c>A11Y_FORMAT_VERSION</c> 一致。</summary>
    public const int FormatVersion = 1;

    // 动作编号：与 AccessibilityActionKind 的成员顺序一致，也与 JS 的 A11Y_ACTION_* 常量一致。
    public const int ActionInvoke = 0;
    public const int ActionFocus = 1;
    public const int ActionToggle = 2;
    public const int ActionSetValue = 3;
    public const int ActionIncrement = 4;
    public const int ActionDecrement = 5;
    public const int ActionScrollIntoView = 6;
    public const int ActionExpand = 7;
    public const int ActionCollapse = 8;

    // 实时区域：与 AccessibilityPoliteness 一致。
    public const int LiveOff = 0;
    public const int LivePolite = 1;
    public const int LiveAssertive = 2;

    /// <summary>SetValue 等动作的字符串参数长度上限（1M 字符，与文本提交一致）。</summary>
    public const int MaxValueLength = CompositionTracker.MaxCommitLength;

    static readonly HashSet<string> s_knownStates = new HashSet<string>(StringComparer.Ordinal)
    {
        "disabled", "checked", "mixed", "selected", "expanded", "pressed", "invalid", "busy",
        "hidden", "focused", "required", "readonly", "multiselectable", "live",
    };

    // 支持 aria-activedescendant 的复合角色。
    static readonly HashSet<string> s_compositeRoles = new HashSet<string>(StringComparer.Ordinal)
    {
        "application", "combobox", "grid", "group", "listbox", "menu", "menubar", "radiogroup",
        "row", "searchbox", "tablist", "textbox", "toolbar", "tree", "treegrid",
    };

    /// <summary>把一版语义树快照编码成 JSON。<paramref name="keyShortcuts"/> 可为节点提供额外的快捷键文本。</summary>
    public static string Encode(AccessibilitySnapshot snapshot, IReadOnlyDictionary<string, string>? keyShortcuts = null)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var nodes = snapshot.Nodes ?? Array.Empty<AccessibilityNodeSnapshot>();
        var activeDescendants = ComputeActiveDescendants(nodes);

        var sb = new StringBuilder(256 + nodes.Count * 192);
        sb.Append("{\"v\":").Append(FormatVersion.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"sv\":").Append(snapshot.Version.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(snapshot.RootId))
        {
            sb.Append(",\"r\":");
            AppendString(sb, snapshot.RootId!);
        }
        sb.Append(",\"n\":[");
        bool first = true;
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node is null || string.IsNullOrEmpty(node.Id)) continue;
            if (!first) sb.Append(',');
            first = false;
            AppendNode(sb, node, activeDescendants, keyShortcuts);
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>语义树的角色名 → ARIA 角色。未知 / 空角色映射为 <c>group</c>，避免向 DOM 写入任意字符串。</summary>
    public static string ToAriaRole(string? role)
    {
        if (string.IsNullOrEmpty(role)) return "group";
        var normalized = role!.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "": return "group";
            case "root": return "application";
            case "generic": return "group";
            case "text": return "paragraph";
            case "statictext": return "paragraph";
            case "image": return "img";
            case "none":
            case "presentation": return "presentation";
        }
        if (normalized.Length > 64) return "group";
        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            bool safe = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!safe) return "group";
        }
        return normalized;
    }

    /// <summary>状态名是否是语义树会产生的已知状态。</summary>
    public static bool IsKnownState(string? state) => state is not null && s_knownStates.Contains(state);

    /// <summary>
    /// 状态名 → ARIA 属性。JS 胶水按同样的规则更新 DOM；这里供单元测试与 DevTools 等纯 C# 使用方复用。
    /// <c>focused</c> 由真实 DOM 焦点表达（不写属性），映射为 <c>data-awaui-focused</c> 便于调试。
    /// </summary>
    public static bool TryMapState(string? state, out string attribute, out string value)
    {
        switch (state)
        {
            case "disabled": attribute = "aria-disabled"; value = "true"; return true;
            case "checked": attribute = "aria-checked"; value = "true"; return true;
            case "mixed": attribute = "aria-checked"; value = "mixed"; return true;
            case "selected": attribute = "aria-selected"; value = "true"; return true;
            case "expanded": attribute = "aria-expanded"; value = "true"; return true;
            case "pressed": attribute = "aria-pressed"; value = "true"; return true;
            case "invalid": attribute = "aria-invalid"; value = "true"; return true;
            case "busy": attribute = "aria-busy"; value = "true"; return true;
            case "hidden": attribute = "aria-hidden"; value = "true"; return true;
            case "focused": attribute = "data-awaui-focused"; value = "true"; return true;
            case "required": attribute = "aria-required"; value = "true"; return true;
            case "readonly": attribute = "aria-readonly"; value = "true"; return true;
            case "multiselectable": attribute = "aria-multiselectable"; value = "true"; return true;
            case "live": attribute = "aria-live"; value = "polite"; return true;
            default: attribute = ""; value = ""; return false;
        }
    }

    /// <summary>动作 → JS 编号；未知动作返回 -1。</summary>
    public static int ActionCode(AccessibilityActionKind action)
    {
        switch (action)
        {
            case AccessibilityActionKind.Invoke: return ActionInvoke;
            case AccessibilityActionKind.Focus: return ActionFocus;
            case AccessibilityActionKind.Toggle: return ActionToggle;
            case AccessibilityActionKind.SetValue: return ActionSetValue;
            case AccessibilityActionKind.Increment: return ActionIncrement;
            case AccessibilityActionKind.Decrement: return ActionDecrement;
            case AccessibilityActionKind.ScrollIntoView: return ActionScrollIntoView;
            case AccessibilityActionKind.Expand: return ActionExpand;
            case AccessibilityActionKind.Collapse: return ActionCollapse;
            default: return -1;
        }
    }

    /// <summary>解析来自 DOM 的动作编号；只有已知的 9 个动作被接受。</summary>
    public static bool TryParseAction(int code, out AccessibilityActionKind action)
    {
        switch (code)
        {
            case ActionInvoke: action = AccessibilityActionKind.Invoke; return true;
            case ActionFocus: action = AccessibilityActionKind.Focus; return true;
            case ActionToggle: action = AccessibilityActionKind.Toggle; return true;
            case ActionSetValue: action = AccessibilityActionKind.SetValue; return true;
            case ActionIncrement: action = AccessibilityActionKind.Increment; return true;
            case ActionDecrement: action = AccessibilityActionKind.Decrement; return true;
            case ActionScrollIntoView: action = AccessibilityActionKind.ScrollIntoView; return true;
            case ActionExpand: action = AccessibilityActionKind.Expand; return true;
            case ActionCollapse: action = AccessibilityActionKind.Collapse; return true;
            default: action = AccessibilityActionKind.Invoke; return false;
        }
    }

    /// <summary>
    /// 收集每个节点当前可执行的动作，供平台桥在把 DOM 动作回传给框架之前做白名单校验。
    /// 快照里没有的节点、或不在列表中的动作都会被拒绝。
    /// </summary>
    public static Dictionary<string, List<AccessibilityActionKind>> CollectActions(AccessibilitySnapshot snapshot)
    {
        var result = new Dictionary<string, List<AccessibilityActionKind>>(StringComparer.Ordinal);
        if (snapshot?.Nodes is not { } nodes) return result;
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node is null || string.IsNullOrEmpty(node.Id)) continue;
            var source = node.Actions;
            if (source is null || source.Count == 0) continue;
            var list = new List<AccessibilityActionKind>(source.Count);
            for (int a = 0; a < source.Count; a++) list.Add(source[a]);
            result[node.Id] = list;
        }
        return result;
    }

    /// <summary>来自 DOM 的 SetValue 文本：去掉控制字符并限制长度（不拆代理对）。</summary>
    public static string SanitizeValue(string? value) => TextInputFilter.SanitizeCommit(value);

    /// <summary>
    /// 白名单检查：节点必须在最近一版快照里，且动作必须由该节点声明。
    /// 平台桥在把 DOM 动作转给框架前调用它，防止页面脚本伪造动作。
    /// </summary>
    public static bool IsActionAllowed(IReadOnlyDictionary<string, List<AccessibilityActionKind>>? actions,
        string? nodeId, AccessibilityActionKind action)
    {
        if (actions is null || string.IsNullOrEmpty(nodeId)) return false;
        if (!actions.TryGetValue(nodeId!, out var list) || list is null) return false;
        for (int i = 0; i < list.Count; i++)
            if (list[i] == action) return true;
        return false;
    }

    // ------------------------------------------------------------ 节点

    static void AppendNode(StringBuilder sb, AccessibilityNodeSnapshot node,
        Dictionary<string, string> activeDescendants, IReadOnlyDictionary<string, string>? keyShortcuts)
    {
        string role = ToAriaRole(node.Role);

        sb.Append("{\"i\":");
        AppendString(sb, node.Id);
        sb.Append(",\"r\":");
        AppendString(sb, role);

        if (!string.IsNullOrEmpty(node.Name))
        {
            sb.Append(",\"nm\":");
            AppendString(sb, node.Name!);
        }
        if (!string.IsNullOrEmpty(node.Description))
        {
            sb.Append(",\"d\":");
            AppendString(sb, node.Description!);
        }
        if (!string.IsNullOrEmpty(node.Value))
        {
            sb.Append(",\"v\":");
            AppendString(sb, node.Value!);
        }

        var states = node.States;
        if (states is not null && states.Count > 0)
        {
            bool started = false;
            for (int i = 0; i < states.Count; i++)
            {
                if (!IsKnownState(states[i])) continue;
                if (!started)
                {
                    sb.Append(",\"st\":[");
                    started = true;
                }
                else sb.Append(',');
                AppendString(sb, states[i]);
            }
            if (started) sb.Append(']');
        }

        var actions = node.Actions;
        if (actions is not null && actions.Count > 0)
        {
            bool started = false;
            for (int i = 0; i < actions.Count; i++)
            {
                int code = ActionCode(actions[i]);
                if (code < 0) continue;
                if (!started)
                {
                    sb.Append(",\"a\":[");
                    started = true;
                }
                else sb.Append(',');
                sb.Append(code.ToString(CultureInfo.InvariantCulture));
            }
            if (started) sb.Append(']');
        }

        var bounds = node.Bounds;
        sb.Append(",\"b\":[");
        AppendNumber(sb, bounds.X);
        sb.Append(',');
        AppendNumber(sb, bounds.Y);
        sb.Append(',');
        AppendNumber(sb, bounds.Width);
        sb.Append(',');
        AppendNumber(sb, bounds.Height);
        sb.Append(']');

        AppendIdArray(sb, "c", node.ChildIds);

        int live = node.LiveRegion == AccessibilityPoliteness.Polite ? LivePolite
            : node.LiveRegion == AccessibilityPoliteness.Assertive ? LiveAssertive
            : LiveOff;
        if (live != LiveOff)
            sb.Append(",\"lv\":").Append(live.ToString(CultureInfo.InvariantCulture));

        if (node.HeadingLevel is int level)
            sb.Append(",\"lvl\":").Append(level.ToString(CultureInfo.InvariantCulture));
        if (node.PosInSet is int posInSet)
            sb.Append(",\"ps\":").Append(posInSet.ToString(CultureInfo.InvariantCulture));
        if (node.SetSize is int setSize)
            sb.Append(",\"ss\":").Append(setSize.ToString(CultureInfo.InvariantCulture));

        string? hasPopup = ComputeHasPopup(node, role);
        if (hasPopup is not null)
        {
            sb.Append(",\"hp\":");
            AppendString(sb, hasPopup);
        }
        if (IsModalRole(role))
            sb.Append(",\"md\":1");

        if (keyShortcuts is not null && keyShortcuts.TryGetValue(node.Id, out var shortcuts) && !string.IsNullOrEmpty(shortcuts))
        {
            sb.Append(",\"ks\":");
            AppendString(sb, shortcuts);
        }

        if (activeDescendants.TryGetValue(node.Id, out var active))
        {
            sb.Append(",\"ad\":");
            AppendString(sb, active);
        }

        AppendIdArray(sb, "lb", node.LabelledBy);
        AppendIdArray(sb, "ct", node.Controls);
        AppendIdArray(sb, "db", node.DescribedBy);

        sb.Append('}');
    }

    static void AppendIdArray(StringBuilder sb, string key, IReadOnlyList<string>? ids)
    {
        if (ids is null || ids.Count == 0) return;
        bool started = false;
        for (int i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            if (string.IsNullOrEmpty(id)) continue;
            if (!started)
            {
                sb.Append(",\"").Append(key).Append("\":[");
                started = true;
            }
            else sb.Append(',');
            AppendString(sb, id);
        }
        if (started) sb.Append(']');
    }

    static string? ComputeHasPopup(AccessibilityNodeSnapshot node, string role)
    {
        if (role == "combobox") return "listbox";
        if ((role == "button" || role == "menuitem" || role == "tab" || role == "treeitem")
            && HasAction(node.Actions, AccessibilityActionKind.Expand)) return "true";
        return null;
    }

    static bool IsModalRole(string role) => role == "dialog" || role == "alertdialog";

    static bool HasAction(IReadOnlyList<AccessibilityActionKind>? actions, AccessibilityActionKind action)
    {
        if (actions is null) return false;
        for (int i = 0; i < actions.Count; i++)
            if (actions[i] == action) return true;
        return false;
    }

    static bool HasState(IReadOnlyList<string>? states, string state)
    {
        if (states is null) return false;
        for (int i = 0; i < states.Count; i++)
            if (string.Equals(states[i], state, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 复合控件里获得焦点的后代 → <c>aria-activedescendant</c>。语义树把焦点放在真实元素上；
    /// 对 listbox / grid / menu 等容器，向读屏器同时报告「活动项」有助于虚拟光标定位。
    /// </summary>
    static Dictionary<string, string> ComputeActiveDescendants(IReadOnlyList<AccessibilityNodeSnapshot> nodes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (nodes is null || nodes.Count == 0) return result;

        var byId = new Dictionary<string, AccessibilityNodeSnapshot>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node is null || string.IsNullOrEmpty(node.Id)) continue;
            if (!byId.ContainsKey(node.Id)) byId.Add(node.Id, node);
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            var focused = nodes[i];
            if (focused is null || string.IsNullOrEmpty(focused.Id)) continue;
            if (!HasState(focused.States, "focused")) continue;

            var parentId = focused.ParentId;
            int guard = 0;
            while (!string.IsNullOrEmpty(parentId) && guard++ < 10_000)
            {
                if (!byId.TryGetValue(parentId!, out var parent)) break;
                if (s_compositeRoles.Contains(ToAriaRole(parent.Role)))
                {
                    if (!result.ContainsKey(parent.Id)) result.Add(parent.Id, focused.Id);
                    break;
                }
                parentId = parent.ParentId;
            }
        }
        return result;
    }

    // ------------------------------------------------------------ JSON 原语

    static void AppendNumber(StringBuilder sb, float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            sb.Append('0');
            return;
        }
        sb.Append(value.ToString("0.####", CultureInfo.InvariantCulture));
    }

    /// <summary>JSON 字符串转义。控制字符、代理项、引号与反斜杠都被转义，其余字符原样输出。</summary>
    static void AppendString(StringBuilder sb, string value)
    {
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || (c >= '\uD800' && c <= '\uDFFF'))
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
