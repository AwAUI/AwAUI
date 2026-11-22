using System;
using System.Collections.Generic;
using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 按子树覆盖手势参数：<see cref="Context.Provide{T}"/> 提供 <see cref="GestureSettings"/> 后，
/// 子树内的识别器在本次指针序列使用它，子树外仍按 <see cref="GestureSettings.Default"/>（design/09「可用性约束」）。
/// </summary>
public class GestureSettingsScopeTests
{
    static RawPointerEvent Event(RawPointerEventType type, long id, float x, float y) =>
        new(type, PointerType.Touch, id, new Point(x, y),
            type is RawPointerEventType.Down or RawPointerEventType.Up ? PointerButtons.Left : PointerButtons.None,
            type == RawPointerEventType.Up ? PointerButtons.None : PointerButtons.Left,
            KeyModifiers.None, TimeSpan.Zero);

    static void Drag(UIRoot root, Element target, float distance)
    {
        root.Input.DispatchPointer(Event(RawPointerEventType.Down, 1, 0, 0), target);
        root.Input.DispatchPointer(Event(RawPointerEventType.Move, 1, distance, 0), target);
        root.Input.DispatchPointer(Event(RawPointerEventType.Up, 1, distance, 0), target);
    }

    static (UIRoot Root, Element Inside, Element Outside) MountSplit(ElementNode inside, ElementNode outside, GestureSettings settings)
    {
        var root = new UIRoot();
        root.Mount(new Lambda
        {
            Render = () => new Div
            {
                Children =
                [
                    Context.Provide(settings, inside),
                    outside,
                ],
            },
        });
        return (root, root.RootElement.ById(inside.Id!)!, root.RootElement.ById(outside.Id!)!);
    }

    [Fact]
    public void Tap_in_a_subtree_uses_the_provided_touch_slop()
    {
        var insideTaps = new List<TapEvent>();
        var outsideTaps = new List<TapEvent>();
        var (root, inside, outside) = MountSplit(
            new Div { Id = "in", OnTap = insideTaps.Add },
            new Div { Id = "out", OnTap = outsideTaps.Add },
            new GestureSettings { TouchSlop = 40f });

        inside.AddGestureRecognizer(new TapGestureRecognizer());
        outside.AddGestureRecognizer(new TapGestureRecognizer());

        // 30 px：子树内（40 px 容差）仍算点按；子树外（默认 8 px）失败。
        Drag(root, inside, 30f);
        Drag(root, outside, 30f);

        Assert.Single(insideTaps);
        Assert.Empty(outsideTaps);
    }

    [Fact]
    public void Pan_in_a_subtree_uses_the_provided_touch_slop_and_does_not_leak()
    {
        bool insideStarted = false;
        bool outsideStarted = false;
        var insidePan = new PanGestureRecognizer();
        insidePan.Started += _ => insideStarted = true;
        var outsidePan = new PanGestureRecognizer();
        outsidePan.Started += _ => outsideStarted = true;

        var (root, inside, outside) = MountSplit(
            new Div { Id = "in" },
            new Div { Id = "out" },
            new GestureSettings { TouchSlop = 40f });

        inside.AddGestureRecognizer(insidePan);
        outside.AddGestureRecognizer(outsidePan);

        // 30 px：子树内未超过 40 px 容差，不开始拖动；子树外超过默认 8 px，开始。
        Drag(root, inside, 30f);
        Drag(root, outside, 30f);

        Assert.False(insideStarted);
        Assert.True(outsideStarted);
    }

    [Fact]
    public void The_setting_is_resolved_for_each_sequence()
    {
        var taps = new List<TapEvent>();
        var settings = new GestureSettings { TouchSlop = 40f };
        var (root, inside, _) = MountSplit(
            new Div { Id = "in", OnTap = taps.Add },
            new Div { Id = "out" },
            settings);

        var tap = new TapGestureRecognizer();
        inside.AddGestureRecognizer(tap);

        Drag(root, inside, 30f);          // 子树内：使用 40 px 容差，成功
        Assert.Single(taps);
        Assert.Same(settings, tap.Settings);
        Assert.Equal(40f, tap.Settings.TouchSlop);

        // 同一识别器的下一次序列仍从子树设置重新求值
        Drag(root, inside, 30f);
        Assert.Equal(2, taps.Count);
        Assert.Same(settings, tap.Settings);
    }
}
