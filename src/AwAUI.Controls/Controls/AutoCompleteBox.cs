using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AwAUI.Controls.Primitives;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.Threading;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIPortal = AwAUI.UI.Portal;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>
/// An editable text field that filters a list of suggestions as the user types.
/// </summary>
/// <remarks>
/// The suggestions open while the typed text matches at least <see cref="MinimumPrefixLength"/> characters. Up and Down
/// move the highlight (Page Up and Page Down by ten), Enter and Tab accept the highlighted suggestion, and Escape closes
/// the list. A pointer press outside dismisses it. Selecting an item sets <see cref="Text"/> to the item's text and reports
/// <see cref="SelectedIndexChanged"/>. Use <see cref="ItemText"/> for items that do not render their own text.
/// <para>
/// Matching is a case-insensitive prefix match by default; set <see cref="ItemFilter"/> for custom matching. Alternatively
/// set <see cref="ItemsSourceProvider"/> to fetch suggestions asynchronously as the user types; results replace the popup
/// list, stale responses are ignored, and the input reports <c>aria-busy</c> while a request is in flight.
/// </para>
/// </remarks>
public sealed partial class AutoCompleteBox : ItemsControl
{
    const int PageSize = 10;
    readonly ControllableState<string> _text = new(StringComparer.Ordinal);
    readonly ControllableState<int> _selected = new();
    readonly ControllableState<bool> _open = new();
    readonly PopperScope _popper = new();
    readonly Ref<Element> _popup = new();
    readonly Ref<Element> _input = new();
    readonly string _popupId = PrimitiveIds.Next("autocomplete-popup");
    readonly IDispatcher? _dispatcher = Reactive.CurrentThreadDispatcher;
    readonly List<object?> _asyncItems = new();
    DismissableLayer<Element>? _layer;
    ItemView? _asyncView;
    CancellationTokenSource? _asyncCts;
    string? _asyncLoadedQuery;
    string? _asyncRequestedQuery;
    string? _asyncFailedQuery;
    int _asyncGeneration;
    IDisposable? _debounceTimer;
    string? _debounceQuery;
    int _debounceGeneration;
    bool _debounceBusy;
    List<object?> _filtered = new();
    ItemView? _itemsView;
    int _highlight = -1;
    bool _closeRequested;

    /// <summary>Current text. Controlled when set; otherwise <see cref="DefaultText"/> applies.</summary>
    public string? Text { get; set; }

    public string? DefaultText { get; set; }

    /// <summary>Requests a change to <see cref="Text"/>. <c>@bind</c>-able.</summary>
    public Action<string>? TextChanged { get; set; }

    /// <summary>The selected item's index in the whole item source, or -1. Controlled when set.</summary>
    public int? SelectedIndex { get; set; }

    public int DefaultSelectedIndex { get; set; } = -1;

    /// <summary>Requests a change to <see cref="SelectedIndex"/>. <c>@bind</c>-able.</summary>
    public Action<int>? SelectedIndexChanged { get; set; }

    /// <summary>Controls whether the suggestion list is open. Null uses internal state.</summary>
    public bool? IsDropDownOpen { get; set; }

    public bool DefaultDropDownOpen { get; set; }

    /// <summary>Requests a change to <see cref="IsDropDownOpen"/>. <c>@bind</c>-able.</summary>
    public Action<bool>? IsDropDownOpenChanged { get; set; }

    /// <summary>The number of typed characters before suggestions open. Zero opens on focus.</summary>
    public int MinimumPrefixLength { get; set; }

    public bool ShowButton { get; set; } = true;

    public bool IsEnabled { get; set; } = true;

    public bool IsReadOnly { get; set; }

    public string? Watermark { get; set; }

    public double MaxDropDownHeight { get; set; } = 320;

    /// <summary>Provides the filter text for an item. Defaults to the item's rendered text.</summary>
    public Func<object?, string?>? ItemText { get; set; }

    /// <summary>
    /// Custom match predicate for the typed text, receiving the item and the current text. When null, items match when
    /// their <see cref="ItemText"/> starts with the text, ignoring case. Ignored when <see cref="ItemsSourceProvider"/> is set.
    /// </summary>
    public Func<object?, string, bool>? ItemFilter { get; set; }

    /// <summary>
    /// Fetches suggestions for the typed text asynchronously. When set, each request replaces the popup's items and the
    /// static <see cref="ItemsControl.ItemsSource"/> is not read. <see cref="IsItemEnabled"/> still filters the results.
    /// The provider receives a token canceled when a newer request supersedes it; late results are ignored. The provider
    /// may complete on any thread; completion is marshalled to the UI thread when the component has a dispatcher.
    /// </summary>
    public Func<string, CancellationToken, Task<IEnumerable<object?>?>>? ItemsSourceProvider { get; set; }

    /// <summary>
    /// Delay before an <see cref="ItemsSourceProvider"/> request starts, so rapid typing issues one request instead of one
    /// per character. <see cref="TimeSpan.Zero"/> (the default) requests immediately. The delay needs a UI clock
    /// (<c>Root.Clock</c>); without one the request starts immediately. <see cref="Refresh"/> bypasses the delay.
    /// </summary>
    public TimeSpan ItemsSourceProviderDebounce { get; set; }

    /// <summary>Reports a failed <see cref="ItemsSourceProvider"/> request on the UI thread. The popup shows no suggestions.</summary>
    public Action<Exception>? ItemsSourceError { get; set; }

    /// <summary>True while an <see cref="ItemsSourceProvider"/> request is in flight.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>The last <see cref="ItemsSourceProvider"/> failure, or null. Cleared when a new request starts.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Re-runs the <see cref="ItemsSourceProvider"/> for the current text, bypassing <see cref="ItemsSourceProviderDebounce"/>.</summary>
    public void Refresh()
    {
        _asyncLoadedQuery = null;
        _asyncFailedQuery = null;
        if (IsMounted) RequestAsync(_text.Value, immediate: true);
    }

    /// <summary>The selected item, or null.</summary>
    public object? SelectedItem => _selected.Value >= 0 && _selected.Value < ItemsSourceCount ? ItemAt(_selected.Value) : null;

    int ItemsSourceCount { get; set; }

    protected override Node Build()
    {
        var items = ItemsSourceProvider is null
            ? ReadItems()
            : _asyncView ??= new ItemView(Array.Empty<object?>(), null);
        _itemsView = items;
        ItemsSourceCount = items.Count;
        string text = _text.Sync(Text, DefaultText ?? "", TextChanged);
        int selected = _selected.Sync(SelectedIndex, DefaultSelectedIndex, SelectedIndexChanged);
        bool enabled = IsEnabled;
        bool open = _open.Sync(IsDropDownOpen, DefaultDropDownOpen, IsDropDownOpenChanged) && enabled;

        _filtered = ItemsSourceProvider is null ? Filter(items, text) : FilterSuggestions(items);
        if (!open) _highlight = -1;
        else _highlight = _filtered.Count == 0 ? -1 : Math.Max(0, Math.Min(_highlight, _filtered.Count - 1));
        _closeRequested = false;

        var input = new Input
        {
            Ref = _input,
            Role = Role.TextBox,
            Value = text,
            Placeholder = Watermark,
            ReadOnly = IsReadOnly,
            Disabled = !enabled,
            InputType = "text",
            OnInput = e => OnInput(e),
            OnKeyDown = e => OnKeyDown(e, text),
        };
        input.Attributes["part"] = "input";
        input.Attributes["role"] = "combobox";
        input.Attributes["aria-autocomplete"] = "list";
        input.Attributes["aria-expanded"] = PartProps.Bool(open);
        input.Attributes["aria-busy"] = IsBusy ? "true" : null;
        input.Attributes["aria-controls"] = open ? _popupId : null;
        input.Attributes["aria-activedescendant"] = open && _highlight >= 0
            ? OptionId(_highlight) : null;

        var box = new Div { Children = [input] };
        box.Attributes["part"] = "container";
        if (ShowButton)
        {
            var indicator = new Span();
            indicator.Attributes["part"] = "indicator";
            indicator.Attributes["aria-hidden"] = "true";
            var toggle = new UIButton
            {
                Disabled = !enabled,
                Children = [indicator],
                OnClick = e =>
                {
                    if (e.DefaultPrevented || !enabled) return;
                    e.PreventDefault();
                    if (open) Close(commit: false);
                    else Open();
                },
            };
            toggle.Class = "aw-autocomplete-box-button";
            toggle.Attributes["part"] = "toggle";
            toggle.Attributes["tabindex"] = "-1";
            toggle.AriaLabel = "Show suggestions";
            box.Children.Add(toggle);
        }

        // 根元素承载 Id / Class / Style / 展开属性；[part="container"] 在内层，样式与 Primitive 部件都按这个层次书写
        var root = Apply(new Div { Children = [box] }, "aw-autocomplete-box");

        _popper.DefaultAnchor = this;
        UseDismissal(open);
        // 受控或默认展开时首次拉取；输入路径已在 OnInput 里直接发请求，这里对同一文本是幂等的
        UseEffect(() =>
        {
            if (ItemsSourceProvider is not null && open) RequestAsync(text);
            return null;
        }, open, text);

        return Provide(_popper, new Fragment([new PopperAnchor { AsChild = true, Children = [root] }, new PresenceGate
        {
            Present = open,
            Children = [new UIPortal { Children = [BuildPopup(items, open)] }],
        }]));
    }

    PopperContent BuildPopup(ItemView items, bool open)
    {
        var options = new NodeList();
        for (int i = 0; i < _filtered.Count; i++)
        {
            int index = i; // capture for the click handler
            var item = _filtered[i];
            bool highlighted = i == _highlight;
            var option = new Div
            {
                Id = OptionId(i),
                Role = Role.Option,
                Class = "aw-autocomplete-box-item",
                Children = [PresentItem(item)],
                OnClick = e =>
                {
                    if (e.DefaultPrevented || !open || _closeRequested) return;
                    Select(index);
                },
                OnPointerMove = e =>
                {
                    if (e.DefaultPrevented || e.PointerType == PointerType.Touch || !open || _closeRequested) return;
                    if (_highlight != index)
                    {
                        _highlight = index;
                        Invalidate();
                    }
                },
            };
            option.Attributes["part"] = "item";
            option.Attributes["aria-selected"] = PartProps.Bool(SelectedMatches(item));
            option.Attributes["data-highlighted"] = highlighted ? "" : null;
            options.Add(option);
        }

        var attrs = new AttributeDictionary
        {
            ["part"] = "popup",
            ["data-state"] = PartProps.OpenState(open),
            ["aria-hidden"] = open ? null : "true",
            ["aria-busy"] = IsBusy ? "true" : null,
            ["onpointerdown"] = (Action<PointerEvent>)(e => e.PreventDefault()),
        };
        if (_input.Current?.Id is { } inputId) attrs["aria-labelledby"] = inputId;

        return new PopperContent
        {
            Id = _popupId,
            Ref = _popup,
            Role = Role.ListBox,
            Align = PopperAlign.Start,
            Side = Side.Bottom,
            SideOffset = 4,
            CollisionPadding = 8,
            Class = "aw-autocomplete-box-popup",
            Style = "overflow: auto; scroll-behavior: auto; overscroll-behavior: contain; " +
                "min-width: var(--aw-popper-anchor-width, 0px); max-width: var(--aw-popper-available-width, 100vw); " +
                "max-height: " + MaxDropDownHeight.ToString("0.###", CultureInfo.InvariantCulture) + "px" +
                (open ? "" : "; pointer-events: none"),
            Attributes = attrs,
            Children = options,
        };
    }

    void UseDismissal(bool open)
    {
        _layer ??= new DismissableLayer<Element>(target =>
            _popper.AnchorElement?.IsInclusiveAncestorOf(target) == true || _popup.Current?.IsInclusiveAncestorOf(target) == true)
        {
            OnDismiss = _ =>
            {
                if (!_open.Value || _closeRequested) return;
                Close(commit: false);
            },
        };
        _layer.Parent = TryUse<DismissableLayerScope>(out var scope) ? scope.Layer : null;
        var layer = _layer;
        UseEffect(() =>
        {
            if (!open || Root is not { } root) return null;
            var services = PrimitiveRoot.For(root);
            services.AddLayer(layer);
            return () => services.RemoveLayer(layer);
        }, open);
    }

    void OnInput(InputEvent e)
    {
        if (IsReadOnly || !IsEnabled) return;
        _text.Set(e.Value);
        _selected.Set(-1);
        if (ItemsSourceProvider is not null)
        {
            bool typedOpen = (e.Value?.Length ?? 0) >= MinimumPrefixLength;
            if (typedOpen)
            {
                if (!_open.Value) _open.Set(true);
                RequestAsync(e.Value);
            }
            else if (_open.Value)
            {
                _open.Set(false);
                _asyncFailedQuery = null;
            }
            else
            {
                // 文本太短且列表已关闭：清掉旧建议，避免下次展开前闪回
                ClearSuggestions();
                Invalidate();
            }
            return;
        }
        if (_itemsView is not { } items) return;
        _filtered = Filter(items, e.Value);
        _highlight = _filtered.Count > 0 ? 0 : -1;
        bool shouldOpen = _filtered.Count > 0 && (e.Value?.Length ?? 0) >= MinimumPrefixLength;
        if (shouldOpen && !_open.Value) _open.Set(true);
        else if (!shouldOpen && _open.Value) _open.Set(false);
        else Invalidate();
    }

    void OnKeyDown(KeyEvent e, string text)
    {
        if (e.DefaultPrevented || !IsEnabled) return;
        if (e.Modifiers == KeyModifiers.Alt && e.Key is "ArrowDown" or "ArrowUp")
        {
            e.PreventDefault();
            if (!e.IsRepeat) { if (_open.Value) Close(commit: false); else Open(); }
            return;
        }
        if (e.Key == "Escape")
        {
            if (!_open.Value) return;
            e.PreventDefault();
            Close(commit: false);
            return;
        }
        if (e.Key == "Enter")
        {
            if (!_open.Value || _highlight < 0) return;
            e.PreventDefault();
            Select(_highlight);
            return;
        }
        if (e.Key == "Tab")
        {
            if (_open.Value) Close(commit: false);
            return;
        }
        if (e.Modifiers != KeyModifiers.None) return;
        int direction = e.Key switch
        {
            "ArrowDown" => +1,
            "ArrowUp" => -1,
            "PageDown" => +PageSize,
            "PageUp" => -PageSize,
            _ => 0,
        };
        if (direction != 0)
        {
            e.PreventDefault();
            if (!_open.Value)
            {
                if (direction > 0) Open();
                return;
            }
            if (_filtered.Count == 0) return;
            _highlight = (_highlight + direction + _filtered.Count) % _filtered.Count;
            Invalidate();
        }
    }

    void Open()
    {
        if (ItemsSourceProvider is not null)
        {
            // 重新展开视为一次重试：上一次失败不应阻止同一文本再次请求
            _asyncFailedQuery = null;
            _open.Set(true);
            RequestAsync(_text.Value);
            Invalidate();
            return;
        }
        if (_itemsView is not { } items) return;
        string text = _text.Value;
        _filtered = Filter(items, text);
        _highlight = _filtered.Count > 0 ? 0 : -1;
        _open.Set(true);
        Invalidate();
    }

    void Select(int filteredIndex)
    {
        if ((uint)filteredIndex >= (uint)_filtered.Count) return;
        var item = _filtered[filteredIndex];
        int index = IndexOf(item);
        string? label = TextOf(item);
        _selected.Set(index);
        if (label is not null) _text.Set(label);
        Close(commit: true);
    }

    void Close(bool commit)
    {
        if (_closeRequested) return;
        _closeRequested = true;
        _highlight = -1;
        _asyncFailedQuery = null;
        _open.Set(false);
        Invalidate();
    }

    int IndexOf(object? item)
    {
        if (_itemsView is not { } items) return -1;
        for (int i = 0; i < items.Count; i++)
            if (Equals(items[i], item)) return i;
        return -1;
    }

    bool SelectedMatches(object? item) => _selected.Value >= 0 && Equals(ItemAt(_selected.Value), item);

    object? ItemAt(int index)
    {
        return _itemsView is { } items && (uint)index < (uint)items.Count ? items[index] : null;
    }

    List<object?> Filter(ItemView items, string? text)
    {
        var result = new List<object?>();
        string prefix = text ?? "";
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!(IsItemEnabled?.Invoke(item) ?? true)) continue;
            if (IsMatch(item, prefix)) result.Add(item);
        }
        return result;
    }

    bool IsMatch(object? item, string text)
    {
        if (text.Length == 0) return true;
        if (ItemFilter is { } filter) return filter(item, text);
        return (TextOf(item) ?? "").StartsWith(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Provider results are already scoped to the query; only the enabled filter applies.</summary>
    List<object?> FilterSuggestions(ItemView items)
    {
        var result = new List<object?>();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (IsItemEnabled?.Invoke(item) ?? true) result.Add(item);
        }
        return result;
    }

    void ClearSuggestions()
    {
        _asyncItems.Clear();
        _asyncView = new ItemView(_asyncItems, _asyncView);
        _filtered = new List<object?>();
        _highlight = -1;
    }

    void RequestAsync(string? text, bool immediate = false)
    {
        if (ItemsSourceProvider is null) return;
        string query = text ?? "";
        var debounce = ItemsSourceProviderDebounce;
        bool canDebounce = !immediate && debounce > TimeSpan.Zero && Root?.Clock is not null;

        if (canDebounce)
        {
            // 同一文本已经在等待：保留计时器；换文本则重新计时
            if (_debounceTimer is not null && string.Equals(_debounceQuery, query, StringComparison.Ordinal)) return;
            CancelDebounce();
            // 文本已经有结果 / 在途 / 刚失败：不再请求（在途请求保留，不会被取消）
            if (IsSatisfied(query)) return;

            _debounceQuery = query;
            int version = ++_debounceGeneration;
            // 等待期间丢弃旧请求：旧结果不再有意义，也不再占用令牌
            _asyncCts?.Cancel();
            _asyncCts?.Dispose();
            _asyncCts = null;
            _asyncGeneration++;
            IsBusy = true;
            _debounceBusy = true;
            Error = null;
            ClearSuggestions();
            Invalidate();

            var clock = Root!.Clock!;
            _debounceTimer = clock.Schedule(debounce, () =>
            {
                _debounceTimer = null;
                _debounceBusy = false;
                if (version != _debounceGeneration) return;
                _debounceQuery = null;
                RequestNow(query);
            });
            return;
        }

        CancelDebounce();
        if (IsSatisfied(query)) return;
        RequestNow(query);
    }

    /// <summary>文本的请求是否已满足：已加载、正在请求或刚失败（不重复请求）。</summary>
    bool IsSatisfied(string query) =>
        string.Equals(_asyncLoadedQuery, query, StringComparison.Ordinal)
        || (IsBusy && string.Equals(_asyncRequestedQuery, query, StringComparison.Ordinal))
        || string.Equals(_asyncFailedQuery, query, StringComparison.Ordinal);

    void CancelDebounce()
    {
        bool waiting = _debounceBusy;
        _debounceBusy = false;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
        _debounceQuery = null;
        _debounceGeneration++;
        // 只有「等待计时」造成的忙碌需要在这里结束；真正的在途请求仍保持 IsBusy
        if (waiting && _asyncCts is null && IsBusy)
        {
            IsBusy = false;
            Error = null;
            Invalidate();
        }
    }

    void RequestNow(string query)
    {
        _debounceBusy = false;
        if (ItemsSourceProvider is not { } provider)
        {
            IsBusy = false;
            Invalidate();
            return;
        }
        if (IsSatisfied(query)) return;

        _asyncCts?.Cancel();
        _asyncCts?.Dispose();
        var cts = new CancellationTokenSource();
        _asyncCts = cts;
        int generation = ++_asyncGeneration;
        _asyncRequestedQuery = query;
        IsBusy = true;
        Error = null;
        ClearSuggestions();
        Invalidate();

        Task<IEnumerable<object?>?> task;
        try
        {
            task = provider(query, cts.Token) ?? Task.FromResult<IEnumerable<object?>?>(null);
        }
        catch (Exception e)
        {
            CompleteAsync(generation, null, e);
            return;
        }

        if (task.IsCompleted)
        {
            CompleteTask(generation, task);
            return;
        }

        task.ContinueWith(t => Dispatch(() => CompleteTask(generation, t)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    void CompleteTask(int generation, Task<IEnumerable<object?>?> task)
    {
        if (task.Status == TaskStatus.RanToCompletion)
        {
            try
            {
                var result = task.Result is { } sequence ? new List<object?>(sequence) : null;
                CompleteAsync(generation, result, null);
            }
            catch (Exception e)
            {
                CompleteAsync(generation, null, e);
            }
            return;
        }

        if (task.IsCanceled)
        {
            CompleteAsync(generation, null, new TaskCanceledException(task));
            return;
        }

        var error = task.Exception is { InnerExceptions.Count: 1 } aggregate
            ? aggregate.InnerExceptions[0]
            : task.Exception;
        CompleteAsync(generation, null, error);
    }

    void CompleteAsync(int generation, List<object?>? items, Exception? error)
    {
        // 被更新请求取代的响应直接丢弃；计数在调度前已递增，因此这里不需要再加锁
        if (generation != _asyncGeneration) return;
        _debounceBusy = false;
        _asyncCts?.Dispose();
        _asyncCts = null;
        IsBusy = false;
        if (!IsMounted) return;

        if (error is not null)
        {
            Error = error;
            _asyncFailedQuery = _asyncRequestedQuery;
            ClearSuggestions();
            Invalidate();
            ItemsSourceError?.Invoke(error);
            return;
        }

        _asyncLoadedQuery = _asyncRequestedQuery;
        _asyncFailedQuery = null;
        _asyncItems.Clear();
        if (items is not null) _asyncItems.AddRange(items);
        _asyncView = new ItemView(_asyncItems, _asyncView);
        _itemsView = _asyncView;
        ItemsSourceCount = _asyncView.Count;
        _filtered = FilterSuggestions(_asyncView);
        _highlight = _filtered.Count > 0 ? 0 : -1;
        Invalidate();
    }

    void Dispatch(Action action)
    {
        // 与 TreeView 的懒加载一致：宿主有调度器时封送 UI 线程；无调度器（无头测试）由调用方保证线程
        if (_dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Post(action);
            return;
        }
        action();
    }

    protected override void OnUnmount()
    {
        CancelDebounce();
        _asyncCts?.Cancel();
        _asyncCts?.Dispose();
        _asyncCts = null;
        _asyncGeneration++;
        base.OnUnmount();
    }

    /// <summary>Returns false for a suggestion that cannot be selected by the user.</summary>
    public Func<object?, bool>? IsItemEnabled { get; set; }

    string OptionId(int index) => _popupId + "-option-" + index.ToString(CultureInfo.InvariantCulture);

    string? TextOf(object? item)
    {
        if (ItemText is { } text) return text(item);
        if (item is not Node node) return item?.ToString();
        var result = new System.Text.StringBuilder();
        AppendText(node, result);
        return result.ToString();
    }

    static void AppendText(Node node, System.Text.StringBuilder result)
    {
        switch (node)
        {
            case TextNode text:
                result.Append(text.Text);
                return;
            case UIText text:
                result.Append(text.Content);
                break;
            case TextBlock text:
                result.Append(text.Text);
                break;
        }
        var children = node switch
        {
            ElementNode element => element.Children,
            Fragment fragment => fragment.Children,
            TextBlock text => text.Children,
            _ => null,
        };
        if (children is not null)
            foreach (var child in children) AppendText(child, result);
    }
}
