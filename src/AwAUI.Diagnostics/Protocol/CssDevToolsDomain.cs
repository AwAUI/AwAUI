using System;
using System.Collections.Generic;
using AwAUI.Diagnostics.Inspection;
using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>
/// Css 域（design/20 <c>Css</c>）：匹配规则、计算值、实时编辑与样式表。数据源是各目标的 <see cref="DevToolsTarget.Styles"/>。
/// </summary>
/// <remarks>
/// <para><b>方法</b>（都在目标的 UI 线程上执行；读取类方法不要求 enable）：</para>
/// <list type="bullet">
/// <item><c>getMatchedRules({ nodeId }) → { inline: Declaration[], matched: RuleMatch[], inherited: { fromNodeId, matched }[], pseudoElements: { name, matched }[] }</c></item>
/// <item><c>getInlineStyle({ nodeId }) → { declarations: Declaration[] }</c>（design/06 列出，design/20 未定义形状）</item>
/// <item><c>getComputedStyle({ nodeId }) → { properties: ComputedProperty[] }</c></item>
/// <item><c>setStyleText({ target: { nodeId } | { ruleId }, text }) → { declarations }</c>：替换整个声明块；
/// 增删改与切换（注释掉）声明都通过它完成。无法解析的文本返回 -32005；规则不存在返回 -32602。</item>
/// <item><c>addRule({ selector, text? }) → { rule }</c>、<c>getStyleSheets() → { sheets }</c>、<c>getStyleSheetText({ sheetId }) → { text }</c>、
/// <c>setStyleSheetText({ sheetId, text }) → { diagnostics }</c>、<c>clearOverrides({ nodeId? })</c>；样式表不存在返回 -32602。</item>
/// <item><c>forcePseudoState({ nodeId, states })</c>：需要 enable；状态名取自 <see cref="StyleInspector.ForceablePseudoStates"/>，
/// 空数组取消。会话停用 Css 或断开时，它强制的状态被自动撤销。</item>
/// </list>
/// <para><b>形状</b>（design/20 未定义的部分）：
/// <c>StyleSheetInfo = { sheetId, origin, sourceUrl?, title?, disabled, editable, ownerNodeId? }</c>；
/// <c>Diagnostic = { severity: "error" | "warning" | "info", message, code?, line?, column? }</c>；
/// <c>Declaration.range = { start, end }</c> 是声明块文本中的 UTF-16 区间。origin 取值 <c>"default" | "author" | "devtools"</c>。</para>
/// <para><b>事件</b>（带 <c>target</c>）：<c>styleSheetChanged { sheetId }</c>、<c>styleSheetAdded { sheet }</c>、<c>styleSheetRemoved { sheetId }</c> 立即推送；
/// <c>computedStyleInvalidated { nodeIds }</c> 按帧合并，每个会话只收到已通过 Tree 域发送给它的节点。</para>
/// </remarks>
public sealed class CssDevToolsDomain : DevToolsDomain
{
    sealed class PendingInvalidations
    {
        public readonly HashSet<long> Set = new();
        public readonly List<long> Order = new();
        public int PruneAt = 1024;
    }

    readonly object _gate = new();
    // 目标 → 节点 → 强制了伪状态的会话（后设置者为准）
    readonly Dictionary<DevToolsTarget, Dictionary<long, DevToolsSession>> _forced = new();
    readonly Dictionary<DevToolsTarget, PendingInvalidations> _invalidated = new();

    internal CssDevToolsDomain() : base("Css") { }

    protected override void RegisterMethods(DevToolsMethodTable methods)
    {
        InspectionProtocol.Add(methods, "getMatchedRules", GetMatchedRules);
        InspectionProtocol.Add(methods, "getInlineStyle", GetInlineStyle);
        InspectionProtocol.Add(methods, "getComputedStyle", GetComputedStyle);
        InspectionProtocol.Add(methods, "setStyleText", SetStyleText);
        InspectionProtocol.Add(methods, "addRule", AddRule);
        InspectionProtocol.Add(methods, "getStyleSheets", GetStyleSheets);
        InspectionProtocol.Add(methods, "getStyleSheetText", GetStyleSheetText);
        InspectionProtocol.Add(methods, "setStyleSheetText", SetStyleSheetText);
        InspectionProtocol.Add(methods, "forcePseudoState", ForcePseudoState, requiresEnable: true);
        InspectionProtocol.Add(methods, "clearOverrides", ClearOverrides);
    }

    // ================================================================ 方法

    JsonValue? GetMatchedRules(DevToolsCallContext ctx)
    {
        var styles = InspectionProtocol.RequireStyles(ctx);
        long nodeId = ctx.GetInt64("nodeId");
        var m = styles.GetMatchedRules(nodeId) ?? throw InspectionProtocol.NodeNotFound(nodeId);
        var inherited = new JsonArray();
        foreach (var i in m.Inherited)
            inherited.Add(new JsonObject { ["fromNodeId"] = i.FromNodeId, ["matched"] = Matches(i.Matched) });
        var pseudo = new JsonArray();
        foreach (var p in m.PseudoElements)
            pseudo.Add(new JsonObject { ["name"] = p.Name, ["matched"] = Matches(p.Matched) });
        return new JsonObject
        {
            ["inline"] = Declarations(m.Inline),
            ["matched"] = Matches(m.Matched),
            ["inherited"] = inherited,
            ["pseudoElements"] = pseudo,
        };
    }

    JsonValue? GetInlineStyle(DevToolsCallContext ctx)
    {
        var styles = InspectionProtocol.RequireStyles(ctx);
        long nodeId = ctx.GetInt64("nodeId");
        var decls = styles.GetInlineStyle(nodeId) ?? throw InspectionProtocol.NodeNotFound(nodeId);
        return new JsonObject { ["declarations"] = Declarations(decls) };
    }

    JsonValue? GetComputedStyle(DevToolsCallContext ctx)
    {
        var styles = InspectionProtocol.RequireStyles(ctx);
        long nodeId = ctx.GetInt64("nodeId");
        var props = styles.GetComputedStyle(nodeId) ?? throw InspectionProtocol.NodeNotFound(nodeId);
        var arr = new JsonArray();
        foreach (var p in props)
        {
            var o = new JsonObject { ["name"] = p.Name, ["value"] = p.Value };
            if (p.FromRuleId != null || p.FromNodeId != 0 || p.FromOrigin != null)
            {
                var from = new JsonObject();
                if (p.FromRuleId != null) from["ruleId"] = p.FromRuleId;
                if (p.FromNodeId != 0) from["nodeId"] = p.FromNodeId;
                from["origin"] = p.FromOrigin is { } origin ? OriginName(origin) : "initial";
                o["from"] = from;
            }
            arr.Add(o);
        }
        return new JsonObject { ["properties"] = arr };
    }

    JsonValue? SetStyleText(DevToolsCallContext ctx)
    {
        var styles = InspectionProtocol.RequireStyles(ctx);
        var t = ctx.GetOptionalObject("target") ?? throw DevToolsProtocolException.InvalidParams(
            "Missing required parameter 'target'.", new JsonObject { ["parameter"] = "target" });
        string text = ctx.GetString("text");
        bool hasNode = t["nodeId"] is { IsNull: false }, hasRule = t["ruleId"] is { IsNull: false };
        StyleTextTarget target;
        if (hasNode && !hasRule && t["nodeId"] is JsonNumber n && n.TryGetInt64(out long nodeId))
            target = StyleTextTarget.ForNode(nodeId);
        else if (hasRule && !hasNode && t["ruleId"] is JsonString r)
            target = StyleTextTarget.ForRule(r.Value);
        else
            throw DevToolsProtocolException.InvalidParams("Parameter 'target' must be { nodeId: integer } or { ruleId: string }.",
                new JsonObject { ["parameter"] = "target" });

        var decls = styles.SetStyleText(target, text);
        if (decls is null)
        {
            if (target.IsInline) throw InspectionProtocol.NodeNotFound(target.NodeId);
            throw DevToolsProtocolException.InvalidParams($"Rule '{target.RuleId}' not found.", new JsonObject { ["ruleId"] = target.RuleId });
        }
        return new JsonObject { ["declarations"] = Declarations(decls) };
    }

    JsonValue? AddRule(DevToolsCallContext ctx)
    {
        var styles = InspectionProtocol.RequireStyles(ctx);
        string selector = ctx.GetString("selector");
        string? text = ctx.GetOptionalString("text");
        var rule = styles.AddRule(selector, text) ?? throw new InvalidOperationException("AddRule returned null.");
        return new JsonObject { ["rule"] = Rule(rule) };
    }

    JsonValue? GetStyleSheets(DevToolsCallContext ctx)
    {
        var styles = InspectionProtocol.RequireStyles(ctx);
        var arr = new JsonArray();
        foreach (var s in styles.GetStyleSheets()) arr.Add(Sheet(s));
        return new JsonObject { ["sheets"] = arr };
    }

    static DevToolsProtocolException SheetNotFound(string sheetId) =>
        DevToolsProtocolException.InvalidParams($"Style sheet '{sheetId}' not found.", new JsonObject { ["sheetId"] = sheetId });

    JsonValue? GetStyleSheetText(DevToolsCallContext ctx)
    {
        var styles = InspectionProtocol.RequireStyles(ctx);
        string sheetId = ctx.GetString("sheetId");
        string text = styles.GetStyleSheetText(sheetId) ?? throw SheetNotFound(sheetId);
        return new JsonObject { ["text"] = text };
    }

    JsonValue? SetStyleSheetText(DevToolsCallContext ctx)
    {
        var styles = InspectionProtocol.RequireStyles(ctx);
        string sheetId = ctx.GetString("sheetId");
        string text = ctx.GetString("text");
        var diags = styles.SetStyleSheetText(sheetId, text) ?? throw SheetNotFound(sheetId);
        var arr = new JsonArray();
        foreach (var d in diags)
        {
            var o = new JsonObject
            {
                ["severity"] = d.Severity switch
                {
                    StyleDiagnosticSeverity.Error => "error",
                    StyleDiagnosticSeverity.Warning => "warning",
                    _ => "info",
                },
                ["message"] = d.Message,
            };
            if (d.Code != null) o["code"] = d.Code;
            if (d.Line > 0) o["line"] = d.Line;
            if (d.Column > 0) o["column"] = d.Column;
            arr.Add(o);
        }
        return new JsonObject { ["diagnostics"] = arr };
    }

    JsonValue? ForcePseudoState(DevToolsCallContext ctx)
    {
        var styles = InspectionProtocol.RequireStyles(ctx);
        var target = ctx.Target!;
        long nodeId = ctx.GetInt64("nodeId");
        var requested = ctx.GetStringArray("states");
        var states = new List<string>(requested.Count);
        foreach (var s in requested)
        {
            if (!IsForceable(s))
                throw DevToolsProtocolException.InvalidParams($"Unknown pseudo state '{s}'.",
                    new JsonObject { ["parameter"] = "states", ["allowed"] = InspectionProtocol.StringArray(StyleInspector.ForceablePseudoStates) });
            if (!states.Contains(s)) states.Add(s);
        }
        InspectionProtocol.RequireStillEnabled(ctx, this);
        if (!styles.ForcePseudoState(nodeId, states)) throw InspectionProtocol.NodeNotFound(nodeId);
        bool recorded;
        lock (_gate)
        {
            // 会话在处理期间停用了 Css（停用先清除启用标记，再在这把锁下收集要撤销的节点）：不再记录，自行撤销
            recorded = ctx.Session.IsDomainEnabled(this);
            if (recorded)
            {
                if (!_forced.TryGetValue(target, out var map)) _forced[target] = map = new Dictionary<long, DevToolsSession>();
                if (states.Count == 0) map.Remove(nodeId);
                else map[nodeId] = ctx.Session;
            }
        }
        if (!recorded)
        {
            if (states.Count > 0) styles.ForcePseudoState(nodeId, Array.Empty<string>());
            throw InspectionProtocol.DomainNotEnabled(this);
        }
        return null;
    }

    static bool IsForceable(string state)
    {
        foreach (var s in StyleInspector.ForceablePseudoStates)
            if (s == state) return true;
        return false;
    }

    JsonValue? ClearOverrides(DevToolsCallContext ctx)
    {
        var styles = InspectionProtocol.RequireStyles(ctx);
        long? nodeId = ctx.GetOptionalInt64("nodeId");
        if (!styles.ClearOverrides(nodeId)) throw InspectionProtocol.NodeNotFound(nodeId ?? 0);
        lock (_gate)
            if (_forced.TryGetValue(ctx.Target!, out var map))
            {
                if (nodeId is { } id) map.Remove(id);
                else map.Clear();
            }
        return null;
    }

    // ================================================================ 序列化

    internal static string OriginName(StyleOrigin origin) => origin switch
    {
        StyleOrigin.Default => "default",
        StyleOrigin.DevTools => "devtools",
        _ => "author",
    };

    static string StatusName(StyleDeclarationStatus status) => status switch
    {
        StyleDeclarationStatus.Overridden => "overridden",
        StyleDeclarationStatus.Invalid => "invalid",
        StyleDeclarationStatus.Unsupported => "unsupported",
        StyleDeclarationStatus.Inactive => "inactive",
        _ => "active",
    };

    static JsonArray Declarations(IReadOnlyList<StyleDeclarationInfo> decls)
    {
        var arr = new JsonArray();
        foreach (var d in decls)
        {
            var o = new JsonObject
            {
                ["name"] = d.Name,
                ["value"] = d.Value,
                ["important"] = d.Important,
                ["status"] = StatusName(d.Status),
            };
            if (d.Reason != null) o["reason"] = d.Reason;
            if (d.Disabled) o["disabled"] = true;
            if (d.Range is { } r) o["range"] = new JsonObject { ["start"] = r.Start, ["end"] = r.End };
            arr.Add(o);
        }
        return arr;
    }

    static JsonObject Rule(StyleRuleInfo r)
    {
        var o = new JsonObject
        {
            ["id"] = r.Id,
            ["selectorText"] = r.SelectorText,
            ["origin"] = OriginName(r.Origin),
        };
        if (r.Layer != null) o["layer"] = r.Layer;
        if (r.Media != null) o["media"] = r.Media;
        o["sheetId"] = r.SheetId;
        if (r.Source is { } s) o["source"] = InspectionProtocol.SourceJson(s);
        o["declarations"] = Declarations(r.Declarations);
        return o;
    }

    static JsonArray Matches(List<StyleRuleMatch> matches)
    {
        var arr = new JsonArray();
        foreach (var m in matches)
            arr.Add(new JsonObject
            {
                ["rule"] = Rule(m.Rule),
                ["matchedSelector"] = m.MatchedSelector,
                ["specificity"] = new JsonArray { m.Specificity.Ids, m.Specificity.Classes, m.Specificity.Types },
            });
        return arr;
    }

    internal static JsonObject Sheet(StyleSheetInfo s)
    {
        var o = new JsonObject { ["sheetId"] = s.Id, ["origin"] = OriginName(s.Origin) };
        if (s.SourceUrl != null) o["sourceUrl"] = s.SourceUrl;
        if (s.Title != null) o["title"] = s.Title;
        o["disabled"] = s.Disabled;
        o["editable"] = s.Editable;
        if (s.OwnerNodeId != 0) o["ownerNodeId"] = s.OwnerNodeId;
        return o;
    }

    // ================================================================ 会话与目标

    internal override void SessionDisabledFor(DevToolsSession session)
    {
        var undo = new List<(DevToolsTarget Target, long NodeId)>();
        lock (_gate)
        {
            foreach (var kv in _forced)
            {
                List<long>? owned = null;
                foreach (var f in kv.Value)
                    if (f.Value == session) (owned ??= new List<long>()).Add(f.Key);
                if (owned is null) continue;
                foreach (var id in owned)
                {
                    kv.Value.Remove(id);
                    undo.Add((kv.Key, id));
                }
            }
        }
        var empty = Array.Empty<string>();
        foreach (var (target, nodeId) in undo)
        {
            if (target.Agent != Agent) continue;
            target.RunOnUIThread(() => target.Styles?.ForcePseudoState(nodeId, empty));
        }
    }

    protected override void OnDisabled()
    {
        lock (_gate) _invalidated.Clear();
    }

    internal void ForgetTarget(DevToolsTarget target)
    {
        lock (_gate)
        {
            _forced.Remove(target);
            _invalidated.Remove(target);
        }
    }

    // ================================================================ 变更通知（来自 StyleInspector）

    internal void OnStyleSheetChanged(DevToolsTarget target, string sheetId) =>
        target.RunOnUIThread(() => Emit("styleSheetChanged", new JsonObject { ["sheetId"] = sheetId }, target.Id));

    internal void OnStyleSheetAdded(DevToolsTarget target, StyleSheetInfo sheet) =>
        target.RunOnUIThread(() => Emit("styleSheetAdded", new JsonObject { ["sheet"] = Sheet(sheet) }, target.Id));

    internal void OnStyleSheetRemoved(DevToolsTarget target, string sheetId) =>
        target.RunOnUIThread(() => Emit("styleSheetRemoved", new JsonObject { ["sheetId"] = sheetId }, target.Id));

    internal void OnComputedStyleInvalidated(DevToolsTarget target, long nodeId) =>
        target.RunOnUIThread(() =>
        {
            var tree = Agent?.Tree;
            if (tree is null || !tree.IsTrackedByAny(target, nodeId)) return;
            lock (_gate)
            {
                if (!_invalidated.TryGetValue(target, out var p)) _invalidated[target] = p = new PendingInvalidations();
                if (!p.Set.Add(nodeId)) return;
                p.Order.Add(nodeId);
                if (p.Set.Count < p.PruneAt) return;
                // 宿主长时间不 FlushFrame 时丢弃已不再被任何会话持有的节点（节点 id 不复用，否则会无限累积）
                p.Order.RemoveAll(id => !tree.IsTrackedByAny(target, id) && p.Set.Remove(id));
                p.PruneAt = Math.Max(1024, 2 * p.Set.Count);
            }
        });

    /// <summary>帧边界：向每个会话发出本帧失效的、它已拿到的节点。</summary>
    protected override void OnFrame()
    {
        List<KeyValuePair<DevToolsTarget, PendingInvalidations>> batch;
        lock (_gate)
        {
            if (_invalidated.Count == 0) return;
            batch = new List<KeyValuePair<DevToolsTarget, PendingInvalidations>>(_invalidated);
            _invalidated.Clear();
        }
        var agent = Agent;
        if (agent is null) return;
        foreach (var kv in batch)
        {
            var target = kv.Key;
            foreach (var session in agent.Sessions)
            {
                if (!session.IsAuthorized || !session.IsDomainEnabled(this)) continue;
                JsonArray? ids = null;
                foreach (var id in kv.Value.Order)
                    if (agent.Tree.IsTracked(session, target, id)) (ids ??= new JsonArray()).Add(id);
                if (ids != null) EmitTo(session, "computedStyleInvalidated", new JsonObject { ["nodeIds"] = ids }, target.Id);
            }
        }
    }
}
