namespace AwAUI.UI.UnitTests;

public class AnimationHoverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hover_after_slide_does_not_restart_the_transform(bool restyle_on_completion_frame)
    {
        var clock = new ManualUIClock();
        var h = new PipelineHarness(new Div { Id = "panel", Class = "panel", OnTransitionRun = _ => { } }, """
            .panel {
              position: fixed;
              top: 0;
              right: 0;
              width: 100px;
              height: 100px;
              background-color: white;
              transition: background-color 150ms, transform 150ms;
              animation: slide-in 500ms ease-in-out;
            }
            .panel:hover { background-color: #eeeeee; }
            @keyframes slide-in { from { transform: translateX(100%); } }
            """, deferMount: true);
        h.Root.Clock = clock;
        h.MountNow();

        clock.Advance(TimeSpan.FromMilliseconds(restyle_on_completion_frame ? 480 : 520));
        h.Root.RunFrame();
        var panel = h.El("panel");
        h.Move(390, 20);
        Assert.Same(panel, h.Root.Input.HoveredElement);
        if (restyle_on_completion_frame) clock.Advance(TimeSpan.FromMilliseconds(40));
        h.Root.RunFrame();

        Assert.Equal(panel.BoundingRect, panel.GetBoundingClientRect());
        Assert.Same(panel, h.Pipeline.HitTest(new Point(390, 20)));
        Assert.DoesNotContain("transform", h.Pipeline.GetAnimationState(panel)!.TransitioningProperties);
        Assert.Contains("background-color", h.Pipeline.GetAnimationState(panel)!.TransitioningProperties);
    }
}
