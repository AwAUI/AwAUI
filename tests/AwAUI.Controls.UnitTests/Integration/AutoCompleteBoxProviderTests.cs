using System.Globalization;
using AwAUI.UI;
using Xunit;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>
/// AutoCompleteBox 的定制匹配与异步建议源（design/9c 遗留限制）：自定义 <c>ItemFilter</c>、异步结果替换静态项、
/// 过期响应丢弃、失败回调、忙碌状态与 <c>Refresh</c>。
/// </summary>
public class AutoCompleteBoxProviderTests
{
    static InputElement Input(UIHarness h) =>
        Assert.IsType<InputElement>(h.FindAll(e => e.GetAttribute("part") == "input").Single());

    static List<Element> Options(UIHarness h) =>
        h.FindAll(e => e.Role == Role.Option && e.IsVisible);

    static void Retype(UIHarness h, string text)
    {
        var input = Input(h);
        input.SetSelectionRange(0, input.Value.Length);
        h.TypeText(text);
    }

    [Fact]
    public void ItemFilter_replaces_the_default_prefix_match()
    {
        var h = new UIHarness(() => new AutoCompleteBox
        {
            ItemsSource = new[] { "apple", "banana", "cherry" },
            ItemFilter = (item, text) => item?.ToString()?.Contains(text, StringComparison.OrdinalIgnoreCase) == true,
        });

        Input(h).Focus();
        h.TypeText("an");

        var labels = Options(h).Select(e => e.TextContent()).ToList();
        Assert.Equal(new[] { "banana" }, labels);
    }

    [Fact]
    public void Provider_results_replace_static_items_and_report_busy()
    {
        var pending = new TaskCompletionSource<IEnumerable<object?>?>();
        var requests = new List<string>();
        var h = new UIHarness(() => new AutoCompleteBox
        {
            ItemsSource = new[] { "static-item" },
            ItemsSourceProvider = (text, _) =>
            {
                requests.Add(text);
                return pending.Task;
            },
        });

        Input(h).Focus();
        h.TypeText("ab");

        Assert.Equal(new[] { "ab" }, requests);
        Assert.Empty(Options(h));
        Assert.Equal("true", Input(h).GetAttribute("aria-busy"));

        pending.SetResult(new object?[] { "abacus", "abbey" });
        h.Frame();

        Assert.Equal(new[] { "abacus", "abbey" }, Options(h).Select(e => e.TextContent()));
        Assert.Null(Input(h).GetAttribute("aria-busy"));
        Assert.DoesNotContain(Options(h), e => e.TextContent() == "static-item");
    }

    [Fact]
    public void Superseded_responses_are_ignored()
    {
        var requests = new List<TaskCompletionSource<IEnumerable<object?>?>>();
        var h = new UIHarness(() => new AutoCompleteBox
        {
            ItemsSourceProvider = (_, _) =>
            {
                var tcs = new TaskCompletionSource<IEnumerable<object?>?>();
                requests.Add(tcs);
                return tcs.Task;
            },
        });

        Input(h).Focus();
        h.TypeText("a");
        Retype(h, "ab");
        Assert.Equal(2, requests.Count);

        // 旧请求晚到：不得覆盖新文本的建议
        requests[0].SetResult(new object?[] { "stale" });
        h.Frame();
        Assert.Empty(Options(h));

        requests[1].SetResult(new object?[] { "abacus" });
        h.Frame();
        Assert.Equal(new[] { "abacus" }, Options(h).Select(e => e.TextContent()));
    }

    [Fact]
    public void Provider_failure_is_reported_and_leaves_no_suggestions()
    {
        var errors = new List<Exception>();
        var h = new UIHarness(() => new AutoCompleteBox
        {
            ItemsSourceProvider = (_, _) => Task.FromException<IEnumerable<object?>?>(new InvalidOperationException("offline")),
            ItemsSourceError = errors.Add,
        });

        Input(h).Focus();
        h.TypeText("a");

        var error = Assert.Single(errors);
        Assert.Equal("offline", error.Message);
        Assert.Empty(Options(h));
        Assert.Null(Input(h).GetAttribute("aria-busy"));
    }

    [Fact]
    public void Provider_requests_are_debounced_and_refresh_bypasses_the_delay()
    {
        var requests = new List<string>();
        AutoCompleteBox? box = null;
        var clock = new ManualUIClock();
        var h = new UIHarness(() => box ??= new AutoCompleteBox
        {
            ItemsSourceProvider = (text, _) =>
            {
                requests.Add(text);
                return Task.FromResult<IEnumerable<object?>?>(new object?[] { text });
            },
            ItemsSourceProviderDebounce = TimeSpan.FromMilliseconds(150),
        });
        h.Root.Clock = clock;
        Input(h).Focus();
        h.TypeText("a");
        h.Frame();

        Assert.Empty(requests);
        Assert.Equal("true", Input(h).GetAttribute("aria-busy"));

        // 连续输入只发最后一次查询
        Retype(h, "ab");
        h.Frame();
        clock.Advance(TimeSpan.FromMilliseconds(149));
        h.Frame();
        Assert.Empty(requests);

        clock.Advance(TimeSpan.FromMilliseconds(2));
        h.Frame();
        Assert.Equal(new[] { "ab" }, requests);
        Assert.Equal(new[] { "ab" }, Options(h).Select(e => e.TextContent()));
        Assert.Null(Input(h).GetAttribute("aria-busy"));

        // Refresh 立即请求，不等待
        box!.Refresh();
        h.Frame();
        Assert.Equal(new[] { "ab", "ab" }, requests);
    }

    [Fact]
    public void Returning_to_a_loaded_text_cancels_the_pending_request()
    {
        int calls = 0;
        var h = new UIHarness(() => new AutoCompleteBox
        {
            ItemsSourceProvider = (text, _) =>
            {
                calls++;
                return Task.FromResult<IEnumerable<object?>?>(new object?[] { text });
            },
            ItemsSourceProviderDebounce = TimeSpan.FromMilliseconds(100),
        });
        var clock = new ManualUIClock();
        h.Root.Clock = clock;
        Input(h).Focus();
        h.TypeText("a");
        clock.Advance(TimeSpan.FromMilliseconds(101));
        h.Frame();
        Assert.Equal(1, calls);

        Retype(h, "ab");
        h.Frame();
        Assert.Equal("true", Input(h).GetAttribute("aria-busy"));
        Retype(h, "a");
        h.Frame();

        // “a” 已有结果：等待中的 “ab” 被取消，忙碌状态结束
        Assert.Null(Input(h).GetAttribute("aria-busy"));
        clock.Advance(TimeSpan.FromMilliseconds(200));
        h.Frame();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Refresh_requests_the_current_text_again()
    {
        int calls = 0;
        AutoCompleteBox? box = null;
        var h = new UIHarness(() => box ??= new AutoCompleteBox
        {
            ItemsSourceProvider = (text, _) =>
            {
                calls++;
                return Task.FromResult<IEnumerable<object?>?>(new object?[] { text + calls.ToString(CultureInfo.InvariantCulture) });
            },
        });

        Input(h).Focus();
        h.TypeText("q");
        Assert.Equal(1, calls);
        Assert.Equal(new[] { "q1" }, Options(h).Select(e => e.TextContent()));

        box!.Refresh();
        h.Frame();
        Assert.Equal(2, calls);
        Assert.Equal(new[] { "q2" }, Options(h).Select(e => e.TextContent()));
    }
}

static class ElementTextExtensions
{
    public static string TextContent(this Element element)
    {
        var text = new System.Text.StringBuilder();
        Append(element, text);
        return text.ToString();
    }

    static void Append(Element element, System.Text.StringBuilder text)
    {
        if (element is TextNodeElement node) text.Append(node.Text);
        else if (element is TextElement { Content: { } content }) text.Append(content);
        for (int i = 0; i < element.Children.Count; i++) Append(element.Children[i], text);
    }
}
