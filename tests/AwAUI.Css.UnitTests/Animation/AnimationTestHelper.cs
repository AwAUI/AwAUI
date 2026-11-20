using AwAUI.Css.Animation;
using AwAUI.Css.Cascade;
using AwAUI.Css.Selectors;
using AwAUI.Css.UnitTests.Cascade;
using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Animation;

/// <summary>插值与动画测试的辅助：按 CSS 文本得到计算值、把计算值序列化为 getComputedStyle 文本。</summary>
internal static class AnimationTestHelper
{
    static readonly StyleEngine s_engine = new();

    /// <summary>属性 <paramref name="property"/> 取 <paramref name="value"/> 时的计算值（装箱）。</summary>
    public static object Computed(string property, string value)
    {
        Assert.True(CssProperties.TryGet(property, out var p), property);
        var style = CascadeTestHelper.One("x { " + property + ": " + value + " }", new El("x"));
        var v = AnimationDispatch.GetValue(style, p.Id);
        return v;
    }

    /// <summary>计算值的 CSS 文本（与 getComputedStyle 相同的序列化）。</summary>
    public static string Text(string property, object value)
    {
        Assert.True(CssProperties.TryGet(property, out var p), property);
        lock (s_engine)
        {
            var list = new List<KeyValuePair<CssPropertyId, object>> { new(p.Id, value) };
            return s_engine.ApplyComputedValues(ComputedStyle.Initial, list).GetPropertyValueText(p.Id);
        }
    }

    /// <summary>在进度 <paramref name="p"/> 上插值两个 CSS 值，返回结果的 CSS 文本。</summary>
    public static string Lerp(string property, string from, string to, double p)
    {
        Assert.True(CssProperties.TryGet(property, out var prop), property);
        CssInterpolation.Interpolate(prop.Id, Computed(property, from), Computed(property, to), p, out var r);
        return Text(property, r);
    }

    /// <summary>是否按计算值平滑插值（否则离散）。</summary>
    public static bool IsSmooth(string property, string from, string to)
    {
        Assert.True(CssProperties.TryGet(property, out var prop), property);
        return CssInterpolation.Interpolate(prop.Id, Computed(property, from), Computed(property, to), 0.5, out _);
    }
}

/// <summary>单个元素上的动画驱动：模拟宿主每帧的重算 / 推进。</summary>
internal sealed class AnimationHarness
{
    public AnimationHarness(string css, string classes, string tag = "div", CssEnvironment? env = null)
    {
        Engine = new StyleEngine(env);
        Engine.AddStyleSheet(StyleSheet.Parse(css));
        Animator = new CssAnimator(Engine);
        Target = new El(tag, null, classes);
        Root = new El("html").Add(Target);
    }

    public StyleEngine Engine { get; }
    public CssAnimator Animator { get; }
    public El Root { get; }
    public El Target { get; }
    public CssAnimationState? State;
    public ComputedStyle? Style;
    public bool ParentRendered = true;
    public readonly List<CssAnimationEvent> Events = new();

    /// <summary>样式输入变化（或首次）：完整重算。</summary>
    public ComputedStyle Resolve(double now)
    {
        var parent = Engine.Resolve(Root, null);
        Style = Animator.Resolve(Target, parent, PseudoElementKind.None, Style, ParentRendered, ref State, now, Events);
        return Style;
    }

    /// <summary>只有时间前进。</summary>
    public ComputedStyle Tick(double now)
    {
        if (State is null) return Style!;
        Style = Animator.Tick(State, now, Events);
        return Style;
    }

    public ComputedStyle SetClasses(string classes, double now)
    {
        Target.SetClasses(classes);
        return Resolve(now);
    }

    public string Text(string property) => Style!.GetPropertyValueText(property) ?? string.Empty;

    /// <summary>事件的简写列表（如 <c>run:opacity@0</c>）。</summary>
    public string EventLog()
    {
        static string Short(CssAnimationEventType t) => t switch
        {
            CssAnimationEventType.AnimationStart => "start",
            CssAnimationEventType.AnimationIteration => "iteration",
            CssAnimationEventType.AnimationEnd => "end",
            CssAnimationEventType.AnimationCancel => "cancel",
            CssAnimationEventType.TransitionRun => "trun",
            CssAnimationEventType.TransitionStart => "tstart",
            CssAnimationEventType.TransitionEnd => "tend",
            _ => "tcancel",
        };
        return string.Join(" ", Events.Select(e => Short(e.Type) + ":" + e.Name + "@" + e.ElapsedTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
