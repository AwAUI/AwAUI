using System.Runtime.CompilerServices;
using AwAUI.Css.Animation;
using AwAUI.Css.Cascade;
using AwAUI.Css.Selectors;
using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Animation;

/// <summary>Tailwind v4 / tw-animate-css / shadcn 的动画工具类（tests/TestFiles/Tailwind/shadcn/full.css 中的真实输出）。</summary>
public class TailwindAnimationTests
{
    static string RepoRoot([CallerFilePath] string path = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(path)! })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
            if (dir != null) return dir.FullName;
        }
        throw new InvalidOperationException("找不到仓库根目录");
    }

    static readonly Lazy<StyleSheet> s_sheet = new(() =>
        StyleSheet.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tests", "TestFiles", "Tailwind", "shadcn", "full.css")).Replace("\r\n", "\n")));

    sealed class Page
    {
        public readonly StyleEngine Engine = new();
        public readonly CssAnimator Animator;
        public readonly El Html, Body, Target;
        public CssAnimationState? State;
        public ComputedStyle? Style;
        public readonly List<CssAnimationEvent> Events = new();

        public Page(El target)
        {
            Engine.AddStyleSheet(s_sheet.Value);
            Animator = new CssAnimator(Engine);
            Target = target;
            Body = new El("body").Add(target);
            Html = new El("html").Add(Body);
        }

        public ComputedStyle Resolve(double now)
        {
            var html = Engine.Resolve(Html, null);
            var body = Engine.Resolve(Body, html);
            Style = Animator.Resolve(Target, body, PseudoElementKind.None, Style, true, ref State, now, Events);
            return Style;
        }

        public ComputedStyle Tick(double now) => Style = Animator.Tick(State!, now, Events);
    }

    [Fact]
    public void Animate_in_fade_in_zoom_in()
    {
        var p = new Page(new El("div", null, "animate-in fade-in-0 zoom-in-95"));
        p.Resolve(0);
        Assert.Equal(0f, p.Style!.Opacity);
        var t0 = p.Style.Transform;
        Assert.Equal("translate3d(0px, 0px, 0px) scale3d(0.95, 0.95, 0.95) rotate(0deg)", t0.ToString());
        Assert.Equal("start:enter", string.Join(" ", p.Events.Select(e => (e.Type == CssAnimationEventType.AnimationStart ? "start" : e.Type.ToString()) + ":" + e.Name)));

        // .15s ease：一半时间时进度为 ease(0.5)
        double eased = CssEasingFunction.Ease.Evaluate(0.5);
        p.Tick(0.075);
        Assert.Equal(eased, p.Style.Opacity, 4);
        var scale = p.Style.Transform.Functions[1];
        Assert.Equal(CssTransformFunctionKind.Scale3D, scale.Kind);
        Assert.Equal(0.95 + 0.05 * eased, scale.A, 4);

        p.Tick(0.15);
        Assert.Equal(1f, p.Style.Opacity);
        Assert.True(p.Style.Transform.IsNone);
        Assert.Contains(p.Events, e => e.Type == CssAnimationEventType.AnimationEnd && e.Name == "enter" && Math.Abs(e.ElapsedTime - 0.15) < 1e-6);
    }

    [Fact]
    public void Data_state_open_animate_in_starts_when_the_attribute_appears()
    {
        var target = new El("div", null, "data-[state=open]:animate-in data-[state=open]:fade-in-0 data-[state=closed]:animate-out data-[state=closed]:fade-out-0");
        target.Attr("data-state", "closed");
        var p = new Page(target);
        p.Resolve(0);
        Assert.Contains(p.Events, e => e.Name == "exit" && e.Type == CssAnimationEventType.AnimationStart);
        p.Tick(1);
        Assert.Contains(p.Events, e => e.Name == "exit" && e.Type == CssAnimationEventType.AnimationEnd);
        target.Attr("data-state", "open");
        p.Resolve(2);
        Assert.Equal(0f, p.Style!.Opacity);
        Assert.Contains(p.Events, e => e.Name == "enter" && e.Type == CssAnimationEventType.AnimationStart);
    }

    [Fact]
    public void Accordion_down_animates_to_the_measured_height()
    {
        var target = new El("div", null, "data-[state=open]:animate-accordion-down overflow-hidden");
        target.Attr("data-state", "open");
        target.Style("--radix-accordion-content-height: 120px");
        var p = new Page(target);
        p.Resolve(0);
        Assert.Equal("0px", p.Style!.GetPropertyValueText("height"));
        double eased = CssEasingFunction.EaseOut.Evaluate(0.5);
        p.Tick(0.1);
        Assert.Equal(120 * eased, ((SizeValue)p.Style.Height).Value.Length, 3);
        p.Tick(0.2);
        Assert.Equal("auto", p.Style.GetPropertyValueText("height"));
    }

    [Fact]
    public void Transition_colors_utility()
    {
        var target = new El("button", null, "transition-colors bg-primary hover:bg-primary/90");
        var p = new Page(target);
        p.Resolve(0);
        var from = p.Style!.BackgroundColor.Value;
        target.State = PseudoStates.Hover;
        p.Resolve(0);
        Assert.Equal(from, p.Style.BackgroundColor.Value);
        p.Tick(0.075);
        var mid = p.Style.BackgroundColor.Value;
        Assert.NotEqual(from, mid);
        p.Tick(0.15);
        Assert.True(p.State is null || p.State.TransitionCount == 0);
        Assert.Contains(p.Events, e => e.Type == CssAnimationEventType.TransitionEnd && e.Name == "background-color");
    }
}
