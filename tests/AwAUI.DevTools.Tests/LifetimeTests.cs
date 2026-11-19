using AwAUI.Platform;

namespace AwAUI.DevTools.Tests;

/// <summary>面板与应用窗口的生命周期：应用窗口关闭时面板随之关闭并断开；释放后协议回调不再触及面板。</summary>
public class LifetimeTests
{
    [Fact]
    public void Closing_the_app_window_closes_the_panel_and_detaches_its_session()
    {
        using var h = new Harness();
        h.Open();
        h.ClickPanel(h.PanelRow(h.AppById("increment").UniqueId)!);
        var agent = h.App.DevTools!.Agent;
        int sessions = agent.Sessions.Count;
        bool closed = false;
        h.P.Closed += _ => closed = true;
        h.AppWindow.Close();
        h.Settle();
        Assert.True(closed);
        Assert.False(h.P.IsOpen);
        Assert.False(h.P.Client.IsConnected);
        Assert.Empty(h.Platform.Windows);
        Assert.True(agent.Sessions.Count < sessions, "the in-process session is still attached");
        // 释放后的打开请求明确失败
        Assert.Throws<ObjectDisposedException>(() => h.P.Open());
    }

    [Fact]
    public void Disposing_the_panel_with_requests_in_flight_is_safe()
    {
        using var h = new Harness();
        h.P.Open();
        // 握手与 getDocument 的响应还在调度器里排队时释放
        h.P.Dispose();
        h.Settle();
        Assert.False(h.P.IsOpen);
        Assert.Single(h.Platform.Windows);
        // 应用不受影响，DevTools 仍可用
        h.ClickApp(h.AppById("increment"));
        Assert.Contains("Count: 1", h.AppById("count").TextContent);
        Assert.Null(h.App.DevTools!.HighlightedElement);
    }
}
