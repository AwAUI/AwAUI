using AwAUI.Threading;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// <see cref="Resource{T}"/> 的单元测试。每个用例都清掉当前线程的调度器（测试线程可能复用了别的用例登记过的调度器），
/// 只有明确需要一个调度器的用例才自行注入。
/// </summary>
public sealed class ResourceTests : IDisposable
{
    readonly IDispatcher? _previousDispatcher;

    public ResourceTests()
    {
        _previousDispatcher = Reactive.CurrentThreadDispatcher;
        Reactive.CurrentThreadDispatcher = null;
    }

    public void Dispose() => Reactive.CurrentThreadDispatcher = _previousDispatcher;

    [Fact]
    public async Task Load_success_produces_a_success_snapshot()
    {
        var resource = new Resource<int>();
        Assert.True(resource.IsLoading);
        Assert.Equal(ResourceStatus.Loading, resource.Status);

        await resource.Load(_ => Task.FromResult(42));

        Assert.Equal(ResourceStatus.Success, resource.Status);
        Assert.True(resource.IsSuccess);
        Assert.True(resource.HasValue);
        Assert.False(resource.IsLoading);
        Assert.False(resource.IsReloading);
        Assert.Equal(42, resource.Value);
        Assert.Null(resource.Error);
        Assert.Equal(42, resource.Snapshot.Value);
    }

    [Fact]
    public async Task Load_failure_captures_the_exception()
    {
        var boom = new InvalidOperationException("boom");
        var resource = new Resource<int>();

        await resource.Load(_ => throw boom);

        Assert.Equal(ResourceStatus.Failure, resource.Status);
        Assert.True(resource.IsFailure);
        Assert.Same(boom, resource.Error);
        Assert.False(resource.HasValue);
    }

    [Fact]
    public async Task Asynchronous_failure_is_captured_as_well()
    {
        var boom = new InvalidOperationException("async boom");
        var resource = new Resource<int>();

        await resource.Load(async _ =>
        {
            await Task.Yield();
            throw boom;
        });

        Assert.True(resource.IsFailure);
        Assert.Same(boom, resource.Error);
    }

    [Fact]
    public async Task A_loader_returning_null_is_a_failure()
    {
        var resource = new Resource<int>();

        await resource.Load(_ => null!);

        Assert.True(resource.IsFailure);
        Assert.IsType<InvalidOperationException>(resource.Error);
    }

    [Fact]
    public async Task Cancellation_is_not_a_failure()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new Resource<int>();

        var load = resource.Load(async token =>
        {
            started.SetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return 1;
        });
        await started.Task;
        Assert.True(resource.IsBusy);

        resource.Cancel();
        await load;

        Assert.Equal(ResourceStatus.Loading, resource.Status);
        Assert.False(resource.IsFailure);
        Assert.Null(resource.Error);
        Assert.False(resource.IsReloading);
    }

    [Fact]
    public async Task A_superseded_load_is_discarded()
    {
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new Resource<string>();

        var firstLoad = resource.Load(_ => first.Task);
        var secondLoad = resource.Load(_ => second.Task);

        first.SetResult("old");
        await firstLoad;
        Assert.Equal(ResourceStatus.Loading, resource.Status); // 旧结果没有写进快照
        Assert.Null(resource.Value);

        second.SetResult("new");
        await secondLoad;
        Assert.Equal(ResourceStatus.Success, resource.Status);
        Assert.Equal("new", resource.Value);
    }

    [Fact]
    public async Task A_late_response_from_a_cancelled_load_is_discarded()
    {
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new Resource<int>();

        var load = resource.Load(_ => gate.Task); // 不理会取消令牌
        resource.Cancel();
        gate.SetResult(9);
        await load;

        Assert.Equal(ResourceStatus.Loading, resource.Status);
        Assert.False(resource.IsFailure);
    }

    [Fact]
    public async Task Reload_keeps_the_previous_value_while_in_flight()
    {
        int calls = 0;
        TaskCompletionSource<int>? gate = null;
        var resource = new Resource<int>();
        await resource.Load(_ =>
        {
            calls++;
            return gate is null ? Task.FromResult(7) : gate.Task;
        });
        Assert.Equal(7, resource.Value);
        Assert.Equal(1, calls);

        gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reload = resource.Reload();

        Assert.True(resource.IsReloading);
        Assert.True(resource.IsBusy);
        Assert.Equal(ResourceStatus.Success, resource.Status);
        Assert.Equal(7, resource.Value); // 旧值继续显示

        gate.SetResult(8);
        await reload;

        Assert.Equal(2, calls);
        Assert.False(resource.IsReloading);
        Assert.Equal(8, resource.Value);
    }

    [Fact]
    public async Task Reload_failure_replaces_the_value_with_the_error()
    {
        int calls = 0;
        var boom = new InvalidOperationException("reload failed");
        var resource = new Resource<int>();
        await resource.Load(_ =>
        {
            calls++;
            return calls == 1 ? Task.FromResult(1) : throw boom;
        });

        await resource.Reload();

        Assert.Equal(2, calls);
        Assert.True(resource.IsFailure);
        Assert.Same(boom, resource.Error);
        Assert.False(resource.IsReloading);
        Assert.False(resource.HasValue);
    }

    [Fact]
    public async Task Cancel_during_reload_restores_the_previous_snapshot()
    {
        TaskCompletionSource<int>? gate = null;
        var resource = new Resource<int>();
        await resource.Load(_ => gate is null ? Task.FromResult(3) : gate.Task);

        gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = resource.Reload();
        Assert.True(resource.IsReloading);

        resource.Cancel();

        Assert.False(resource.IsReloading);
        Assert.Equal(ResourceStatus.Success, resource.Status);
        Assert.Equal(3, resource.Value);
    }

    [Fact]
    public void Completion_is_marshalled_to_the_given_dispatcher()
    {
        var dispatcher = new TestDispatcher();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new Resource<int>(dispatcher);

        var load = resource.Load(_ => gate.Task);
        Assert.Equal(ResourceStatus.Loading, resource.Status);

        gate.SetResult(21); // 续体异步在线程池上运行，完成不在调度器线程
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (dispatcher.Pending == 0 && DateTime.UtcNow < deadline) Thread.Sleep(5);

        // 完成在线程池上发生：结果先投递到调度器，Pump 之后才写进快照
        Assert.Equal(1, dispatcher.Pending);
        Assert.True(load.IsCompleted);
        Assert.Equal(ResourceStatus.Loading, resource.Status);
        Assert.Equal(1, dispatcher.Pump());

        Assert.Equal(ResourceStatus.Success, resource.Status);
        Assert.Equal(21, resource.Value);
    }

    [Fact]
    public async Task A_load_completes_without_a_dispatcher()
    {
        var resource = new Resource<int>();

        await resource.Load(_ => Task.Run(() => 5));

        Assert.Equal(ResourceStatus.Success, resource.Status);
        Assert.Equal(5, resource.Value);
        Assert.False(resource.IsBusy);
    }

    [Fact]
    public async Task Reading_the_resource_in_Build_creates_a_dependency()
    {
        var resource = new Resource<string>();
        var component = new Lambda
        {
            Render = () =>
            {
                var snapshot = resource.Snapshot; // 读取快照即登记依赖
                return new Text(snapshot.Status switch
                {
                    ResourceStatus.Loading => "loading",
                    ResourceStatus.Failure => "failed",
                    _ => snapshot.Value ?? "none",
                });
            },
        };
        var root = new UIRoot();
        root.Mount(component);

        Assert.Equal("loading", root.RootElement.TextContent);
        Assert.Equal(1, component.Builds);

        await resource.Load(_ => Task.FromResult("hello"));

        Assert.True(root.NeedsFrame);
        root.RunFrame();
        Assert.Equal("hello", root.RootElement.TextContent);
        Assert.Equal(2, component.Builds);
    }

    [Fact]
    public async Task Dispose_cancels_in_flight_work_and_ignores_later_results()
    {
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new Resource<int>();

        var load = resource.Load(_ => gate.Task);
        resource.Dispose();
        Assert.True(resource.IsDisposed);

        gate.SetResult(3);
        await load;
        Assert.Equal(ResourceStatus.Loading, resource.Status);

        await resource.Load(_ => Task.FromResult(4)); // 已释放：立即完成，不改变快照
        Assert.Equal(ResourceStatus.Loading, resource.Status);
        Assert.Equal(0, resource.Value);
    }
}
