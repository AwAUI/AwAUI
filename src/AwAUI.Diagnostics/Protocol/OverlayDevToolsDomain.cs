using System;
using System.Collections.Generic;
using AwAUI.Diagnostics.Inspection;
using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>
/// Overlay 域（design/20 <c>Layout / Overlay</c>）：节点高亮与拾取。执行者是各目标的 <see cref="DevToolsTarget.Overlay"/>。
/// </summary>
/// <remarks>
/// <para><b>方法</b>（都需要 enable，在目标的 UI 线程上执行）：</para>
/// <list type="bullet">
/// <item><c>highlightNode({ nodeId, config?: HighlightConfig })</c>，<c>HighlightConfig = { showInfo?, showRulers?, showExtensionLines?,
/// contentColor?, paddingColor?, borderColor?, marginColor? }</c>（CDP 式；颜色为 <c>"#rrggbbaa"</c>，也接受 #rgb / #rgba / #rrggbb；
/// 省略的字段取 <see cref="HighlightConfig"/> 的默认值）。</item>
/// <item><c>hideHighlight()</c>、<c>setInspectMode({ mode: "none" | "pick" | "measure" })</c>。</item>
/// <item><c>setShowOptions({ layoutGrids?, rulers?, hitTargets?, touches? })</c>：持续显示的辅助内容；省略的字段取 false / 空数组。</item>
/// </list>
/// <para><b>事件</b>（发给所有启用了 Overlay 的会话，带 <c>target</c>）：<c>nodePicked { nodeId }</c>、<c>inspectModeCanceled {}</c>。
/// 拾取到节点后不会自动退出拾取模式。</para>
/// <para>叠加层在目标上是全局的；高亮、检查模式或显示选项的设置者停用 Overlay（或断开）时，它设置的高亮被隐藏、
/// 检查模式回到 none、显示选项恢复为全部关闭。后设置者生效。</para>
/// </remarks>
public sealed class OverlayDevToolsDomain : DevToolsDomain
{
    readonly object _gate = new();
    readonly Dictionary<DevToolsTarget, DevToolsSession> _highlightOwner = new();
    readonly Dictionary<DevToolsTarget, DevToolsSession> _modeOwner = new();
    readonly Dictionary<DevToolsTarget, (DevToolsSession Owner, OverlayShowOptions Options)> _showOptions = new();

    internal OverlayDevToolsDomain() : base("Overlay") { }

    protected override void RegisterMethods(DevToolsMethodTable methods)
    {
        InspectionProtocol.Add(methods, "highlightNode", HighlightNode, requiresEnable: true);
        InspectionProtocol.Add(methods, "hideHighlight", HideHighlight, requiresEnable: true);
        InspectionProtocol.Add(methods, "setInspectMode", SetInspectMode, requiresEnable: true);
        InspectionProtocol.Add(methods, "setShowOptions", SetShowOptions, requiresEnable: true);
    }

    JsonValue? HighlightNode(DevToolsCallContext ctx)
    {
        var overlay = InspectionProtocol.RequireOverlay(ctx);
        long nodeId = ctx.GetInt64("nodeId");
        var config = ParseConfig(ctx.GetOptionalObject("config"));
        InspectionProtocol.RequireStillEnabled(ctx, this);
        if (!overlay.HighlightNode(nodeId, config)) throw InspectionProtocol.NodeNotFound(nodeId);
        if (!TrySetOwner(_highlightOwner, ctx))
        {
            // 会话在处理期间停用了 Overlay（撤销已经执行过）：自行撤销
            overlay.HideHighlight();
            throw InspectionProtocol.DomainNotEnabled(this);
        }
        return null;
    }

    JsonValue? HideHighlight(DevToolsCallContext ctx)
    {
        var overlay = InspectionProtocol.RequireOverlay(ctx);
        InspectionProtocol.RequireStillEnabled(ctx, this);
        overlay.HideHighlight();
        lock (_gate) _highlightOwner.Remove(ctx.Target!);
        return null;
    }

    JsonValue? SetInspectMode(DevToolsCallContext ctx)
    {
        var overlay = InspectionProtocol.RequireOverlay(ctx);
        var mode = ctx.GetString("mode") switch
        {
            "none" => InspectMode.None,
            "pick" => InspectMode.Pick,
            "measure" => InspectMode.Measure,
            _ => throw DevToolsProtocolException.InvalidParams("Parameter 'mode' must be \"none\", \"pick\" or \"measure\".",
                new JsonObject { ["parameter"] = "mode" }),
        };
        InspectionProtocol.RequireStillEnabled(ctx, this);
        overlay.SetInspectMode(mode);
        if (mode == InspectMode.None)
        {
            lock (_gate) _modeOwner.Remove(ctx.Target!);
        }
        else if (!TrySetOwner(_modeOwner, ctx))
        {
            overlay.SetInspectMode(InspectMode.None);
            throw InspectionProtocol.DomainNotEnabled(this);
        }
        return null;
    }

    JsonValue? SetShowOptions(DevToolsCallContext ctx)
    {
        var overlay = InspectionProtocol.RequireOverlay(ctx);
        var options = new OverlayShowOptions
        {
            LayoutGrids = ParseNodeIds(ctx, "layoutGrids"),
            Rulers = ctx.GetBoolean("rulers", false),
            HitTargets = ctx.GetBoolean("hitTargets", false),
            Touches = ctx.GetBoolean("touches", false),
        };
        InspectionProtocol.RequireStillEnabled(ctx, this);
        overlay.SetShowOptions(options);
        bool recorded;
        lock (_gate)
        {
            recorded = ctx.Session.IsDomainEnabled(this);
            if (recorded) _showOptions[ctx.Target!] = (ctx.Session, options);
        }
        if (!recorded)
        {
            // 处理期间会话停用了 Overlay：停用时的撤销已经执行过，这里要自行恢复
            ShowDefaults(overlay);
            throw InspectionProtocol.DomainNotEnabled(this);
        }
        return null;
    }

    static IReadOnlyList<long> ParseNodeIds(DevToolsCallContext ctx, string name)
    {
        switch (ctx.Params[name])
        {
            case null:
            case JsonNull:
                return Array.Empty<long>();
            case JsonArray a:
            {
                var list = new long[a.Count];
                for (int i = 0; i < list.Length; i++)
                {
                    if (a[i] is not JsonNumber n || !n.TryGetInt64(out long id) || id <= 0 || id > InspectionProtocol.MaxSafeId)
                        throw DevToolsProtocolException.InvalidParams(
                            $"Parameter '{name}' must be an array of node ids.", new JsonObject { ["parameter"] = name });
                    list[i] = id;
                }
                return list;
            }
            default:
                throw DevToolsProtocolException.InvalidParams(
                    $"Parameter '{name}' must be an array of node ids.", new JsonObject { ["parameter"] = name });
        }
    }

    /// <summary>恢复显示选项为全部关闭。目标已脱离 Agent 或接缝不支持时静默跳过。</summary>
    static void ShowDefaults(OverlayHost? overlay)
    {
        try { overlay?.SetShowOptions(new OverlayShowOptions()); }
        catch (NotSupportedException) { /* 接缝不支持：没有可供撤销的状态 */ }
    }

    /// <summary>
    /// 记录设置者。会话已停用 Overlay 时返回 false：停用先清除启用标记再（在同一把锁下）收集设置者，
    /// 因此要么这里的记录被停用时的撤销看到，要么这里看到会话已停用。
    /// </summary>
    bool TrySetOwner(Dictionary<DevToolsTarget, DevToolsSession> owners, DevToolsCallContext ctx)
    {
        lock (_gate)
        {
            if (!ctx.Session.IsDomainEnabled(this)) return false;
            owners[ctx.Target!] = ctx.Session;
            return true;
        }
    }

    static HighlightConfig ParseConfig(JsonObject? o)
    {
        var c = new HighlightConfig();
        if (o is null) return c;
        c.ShowInfo = Bool(o, "showInfo", c.ShowInfo);
        c.ShowRulers = Bool(o, "showRulers", c.ShowRulers);
        c.ShowExtensionLines = Bool(o, "showExtensionLines", c.ShowExtensionLines);
        c.ContentColor = ColorValue(o, "contentColor", c.ContentColor);
        c.PaddingColor = ColorValue(o, "paddingColor", c.PaddingColor);
        c.BorderColor = ColorValue(o, "borderColor", c.BorderColor);
        c.MarginColor = ColorValue(o, "marginColor", c.MarginColor);
        return c;
    }

    static bool Bool(JsonObject o, string name, bool fallback) => o[name] switch
    {
        null or JsonNull => fallback,
        JsonBoolean b => b.GetBoolean(),
        _ => throw BadConfig(name, "a boolean"),
    };

    static Color ColorValue(JsonObject o, string name, Color fallback) => o[name] switch
    {
        null or JsonNull => fallback,
        JsonString s when Color.TryParse(s.Value, out var c) => c,
        _ => throw BadConfig(name, "a color string \"#rrggbbaa\""),
    };

    static DevToolsProtocolException BadConfig(string name, string expected) =>
        DevToolsProtocolException.InvalidParams($"config.{name} must be {expected}.", new JsonObject { ["parameter"] = "config." + name });

    // ---- 会话与目标 ----

    internal override void SessionDisabledFor(DevToolsSession session)
    {
        var hide = new List<DevToolsTarget>();
        var reset = new List<DevToolsTarget>();
        var showDefaults = new List<DevToolsTarget>();
        lock (_gate)
        {
            Take(_highlightOwner, session, hide);
            Take(_modeOwner, session, reset);
            foreach (var kv in _showOptions)
                if (kv.Value.Owner == session) showDefaults.Add(kv.Key);
            foreach (var t in showDefaults) _showOptions.Remove(t);
        }
        foreach (var t in hide)
            if (t.Agent == Agent) t.RunOnUIThread(() => t.Overlay?.HideHighlight());
        foreach (var t in reset)
            if (t.Agent == Agent) t.RunOnUIThread(() => t.Overlay?.SetInspectMode(InspectMode.None));
        foreach (var t in showDefaults)
        {
            if (t.Agent != Agent) continue;
            var overlay = t.Overlay;
            t.RunOnUIThread(() => ShowDefaults(overlay));
        }
    }

    static void Take(Dictionary<DevToolsTarget, DevToolsSession> owners, DevToolsSession session, List<DevToolsTarget> taken)
    {
        foreach (var kv in owners)
            if (kv.Value == session) taken.Add(kv.Key);
        foreach (var t in taken) owners.Remove(t);
    }

    internal void ForgetTarget(DevToolsTarget target)
    {
        lock (_gate)
        {
            _highlightOwner.Remove(target);
            _modeOwner.Remove(target);
            _showOptions.Remove(target);
        }
    }

    // ---- 通知（来自 OverlayHost） ----

    internal void OnNodePicked(DevToolsTarget target, long nodeId) =>
        target.RunOnUIThread(() => Emit("nodePicked", new JsonObject { ["nodeId"] = nodeId }, target.Id));

    internal void OnInspectModeCanceled(DevToolsTarget target) =>
        target.RunOnUIThread(() =>
        {
            lock (_gate) _modeOwner.Remove(target);
            Emit("inspectModeCanceled", null, target.Id);
        });
}
