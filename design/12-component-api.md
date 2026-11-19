# 12 组件 API（接口级草案）

在 [05](05-ui-core.md) 的模型之上，把组件、Node、State、协调器的 API 形状定下来。这是框架的底层 C# API：既可以直接手写，也是 `.awaui` 文件（[14](14-awaui-format.md)）的编译目标，因此它必须同时满足「人和 AI 写起来顺手」与「代码生成器容易生成」。所有 API 不依赖运行时特性，netstandard2.0 可用（见 [11](11-compatibility.md)）。

> 实现现状（M1 第四批）：运行时在 `src/AwAUI.UI`，源生成器在 `src/tools/AwAUI.Generators`（netstandard2.0，Roslyn 4.14），胶水的发射逻辑在 `src/Shared/CodeGen/ComponentGlue.cs`（不含 Roslyn 类型，输出可按 C# 7.3 解析，供 `.awaui` 编译器共用）。
> - **所有运行时类型都在命名空间 `AwAUI.UI`**，而不是 `AwAUI`：`AwAUI.Text` 已是文本栈的命名空间，`Text` 元素只能是 `AwAUI.UI.Text`。应用代码（`namespace MyApp; using AwAUI.UI;`）不受影响；但位于其他 `AwAUI.*` 命名空间的代码里简单名 `Text` 会解析成命名空间，须写 `UI.Text` 或用别名。
> - 尚未实现：`TextArea / Canvas / Path` 元素，`WheelEvent / ScrollEvent / PinchEvent / DragEvent / CompositionEvent` 与对应的 `OnDoubleClick / OnContextMenu / OnWheel / OnScroll` 等处理器属性，`Resource<T>`，退出动画，组件 Ref，DevTools 组件元数据与源映射表，分析器 AW1002–AW1004。`OnTap / OnPan` 已声明但还没有手势识别器触发它们。样式、布局、绘制、命中测试与窗口宿主已在 M1 接入（`RenderPipeline`、`UIHost`，见 [05](05-ui-core.md)「M1 帧管线」）；第六批加入 `Svg` 元素、`Icon` 组件、图片来源与缓存、增量绘制（见下文「图片、SVG 与增量绘制」）；第七批 `Image` 画出位图像素，`Input` 有了光标、选区、输入法与剪贴板（见下文「文本输入」）。

## 一个完整的例子

```csharp
// TodoApp.cs
public partial class TodoApp : Component
{
    readonly State<List<Todo>> todos = new(new List<Todo>());
    readonly State<string> draft = new("");

    void Add()
    {
        if (draft.Value.Length == 0) return;
        todos.Update(list => list.Append(new Todo(Guid.NewGuid(), draft.Value)).ToList());
        draft.Value = "";
    }

    protected override Node Build() =>
        new Div { Class = "todo-app", Children =
        [
            new Text("待办事项") { Class = "title" },

            new Div { Class = "row", Children =
            [
                new Input
                {
                    Value = draft.Value,
                    Placeholder = "要做什么？",
                    OnInput = e => draft.Value = e.Value,
                    OnSubmit = _ => Add(),
                },
                new Button { Class = "primary", OnClick = _ => Add(), Children = [ "添加" ] },
            ]},

            todos.Value.Count == 0
                ? new Text("暂无事项") { Class = "empty" }
                : new Div { Class = "list", Children =
                  [
                      .. todos.Value.Select(t => new TodoRow { Key = t.Id, Todo = t, OnRemove = Remove }),
                  ]},
        ]};
}
```

```css
/* TodoApp.css —— 与组件同名，自动限定作用域 */
.todo-app { display: flex; flex-direction: column; gap: 12px; padding: 16px; }
.title    { font-size: 20px; font-weight: 600; }
.row      { display: flex; gap: 8px; }
.row > input { flex: 1; }
.empty    { color: var(--text-secondary); }
```

## Node：描述对象

```csharp
public abstract class Node
{
    public object? Key { get; set; }            // 同级之间的身份
}

public abstract class ElementNode : Node        // Div、Text、Image、Input ... 的基类
{
    public string? Id { get; set; }
    public ClassValue Class { get; set; }       // 可由 string 隐式转换
    public StyleValue Style { get; set; }       // 可由 string 隐式转换（目前只保存原始文本；强类型样式对象未实现）
    public NodeList Children { get; set; }      // 支持 [ ... ] 与 { ... } 两种写法；getter 按需创建列表
    public Ref<Element>? Ref { get; set; }

    // 事件（节选）
    public Action<PointerEvent>? OnClick { get; set; }
    public Action<PointerEvent>? OnPointerDown { get; set; }
    public Action<KeyEvent>? OnKeyDown { get; set; }
    public Action<TapEvent>? OnTap { get; set; }
    public Action<PanEvent>? OnPan { get; set; }
    public Action<PointerEvent>? OnClickCapture { get; set; }   // 捕获阶段变体：On*Capture
    // ...

    // 无障碍（节选）
    public Role Role { get; set; }
    public string? AriaLabel { get; set; }
    public int? TabIndex { get; set; }

    public AttributeMap Attributes { get; }     // 自定义属性，供 CSS 属性选择器使用
}
```

约定：

- **Node 是一次性的描述**，`Build()` 每次返回新对象，框架不保留对它的引用（比较完即丢弃）。
- `NodeList` 接受 `Node`、`string`（隐式成为文本节点）、`null`（忽略，用于条件渲染）以及序列展开。它是带 `Add(Node?)` / `Add(string?)` 的普通类而不是 `CollectionBuilder` 类型，所以集合表达式 `[a, "text", null, .. seq]` 与初始化器 `{ a, b }` 在 netstandard2.0 上都无需 polyfill；序列用 `AddRange`（不提供 `Add(IEnumerable<Node>)`，否则 `null` 元素会有歧义）。字符串经 `implicit operator Node(string)` 变成 `TextNode`。
- `ClassValue` 辅助条件类名：`Class = Cx("tab", ("active", isActive), ("disabled", !enabled))`。
- 类型化元素各自增加属性：`Text.Content`、`Image.Source / Alt / Width / Height`（`Source` 是 `ImageSource?`，字符串可隐式转换；第七批删除了 `Fit / Position / Rendering`，改由 CSS 提供）、`Svg.Document / Width / Height`、`Button.Disabled`、`Input.Value / Placeholder / InputType / Disabled / ReadOnly / OnInput / OnChange / OnSubmit` 等。每个内建元素有手写的 `Diff`，只写变化的字段并打精确的脏标记；只改事件处理器时只更新引用、不打任何脏标记。
- 内建元素目前是 `Div`、`Span`、`Text`、`Image`（标签 `img`）、`Svg`（标签 `svg`）、`Button`（标签 `button`）和一个简易 `Input`；另有内建组件 `Icon`。M1 的 `Input` 只在末尾插入/删除文本，没有光标、选区、IME 与剪贴板；受控值只在下一次重建时同步，不像 React 那样在事件后立即拉回。

## Component

```csharp
public abstract class Component : Node
{
    protected abstract Node Build();

    protected virtual void OnMount() { }
    protected virtual void OnUnmount() { }

    protected void UseEffect(Func<Action?> effect, params object?[] deps);
    protected T Use<T>();                                  // 读取祖先提供的上下文
    protected bool TryUse<T>(out T value);
    protected static Node Provide<T>(T value, Node child); // 向子树提供上下文（同 Context.Provide）
    protected void Invalidate();                           // 强制下一帧重建

    // 由源生成器实现的胶水；没有生成器时默认 PropsEqual = true、AdoptProps 什么都不做（适用于无 Props 的组件）
    protected virtual bool PropsEqual(Component other);
    protected virtual void AdoptProps(Component other);
    protected virtual bool CanAdoptProps(Component other); // 仅在有 init Props 时生成

    public bool IsMounted { get; }
    public UIRoot? Root { get; }
    public Component? ParentComponent { get; }
    public IReadOnlyList<Element> GetRootElements();
}
```

- 胶水是 `protected virtual`（不是 `protected internal`），所以生成的重写在任何程序集里写法相同。
- `UseEffect` 的槽位按 `Build()` 内的调用顺序识别，只能在 `Build()` 中调用。不传 deps 表示只在挂载时运行一次（`params` 无法区分「没有 deps」与空数组）。effect 函数本身不追踪 State 读取，由 deps 驱动。
- **生命周期顺序**：挂载时 `OnMount` 之后运行 effects，都在本帧所有元素变更完成后的提交阶段，子先于父。卸载时子先于父；每个组件先按声明顺序执行 effect 清理，再调用 `OnUnmount`。被移除子树里的所有回调都执行完后才分离元素、清空 Ref，所以 `OnUnmount` 仍能看到自己的元素。提交阶段写入的 State 在下一帧生效（唯一例外：捕获了错误的 `ErrorBoundary` 在同一帧切换到后备界面）。
- **重新挂载**：已卸载的组件实例可以再次挂载（例如 `ErrorBoundary.Reset` 后，或缓存的实例被条件渲染），它保留 State，并再次收到 `OnMount` 与 effects，`OnMount / OnUnmount` 始终成对。同一实例同时挂在两处仍是错误。

### Props 与实例的关系

组件类既是描述（父组件 `new TodoRow { Todo = t }`）也是存活实例（持有 State）。协调规则：

1. 首次出现：该对象成为**存活实例**，挂载并调用 `Build()`。
2. 之后父组件重建产生的同类型同 Key 的新对象只是**Props 载体**：生成的 `AdoptProps(other)` 把它的 Props 拷给存活实例，新对象随即丢弃；`PropsEqual` 判断无变化时跳过重建。
3. Props = 组件上的公共可写属性（纯语法层面的约定，不需要语义分析，使 C# 源生成器与 `.awaui` 编译器可以共用同一套胶水代码生成逻辑）。分析器保证组件内部不给自己的 Props 赋值（`AW1001`）。
   - 具体规则：public、非 static、非索引器、public get 加 public set/init、名字不是 `Key`、不是 ref 类结构。基类与类型名通过符号解析，Props 只按语法识别。
   - **init Props**（如 05 中的 `Step { get; init; }`）无法在 `AdoptProps` 中赋值：生成的 `CanAdoptProps` 在 init Prop 变化时返回 false，协调器随即**重新挂载**该组件（State 重置）。可写 Props 照常原地采纳。
   - 基类若是同一编译中的 partial 组件、或已声明 `AdoptProps` 重写，生成的方法调用 `base.`；否则由派生类拷贝基类的 Props。生成器只补出类里**没有手写**的胶水方法，例如只手写了 `PropsEqual`（用来忽略某个回调 Prop）时仍会生成 `AdoptProps`。
   - 非 partial 的组件只有同时手写 `PropsEqual` 与 `AdoptProps` 才不报 `AW1006`（否则其 Props 永远不会更新）。`AW1001` 是纯语法检查：对自身成员中给 `Prop` / `this.Prop` 的赋值、`++/--`、复合赋值报告；跳过构造函数、对象与 `with` 初始化器、嵌套类型以及 `override AdoptProps`。
   - **委托类 Props** 用 `EqualityComparer<T>.Default` 比较，即 `Delegate.Equals`（同一方法 + 同一目标）。方法组（`OnRemove = Remove`）和只捕获 `this` 的 lambda 在重建间相等，子组件不会因此重建；捕获局部变量的 lambda 每次都是新闭包，子组件会重建，这样不会留下引用旧闭包的处理器。副作用：`State<Func<..>>` 再次写入同一方法组会被忽略。
4. State 字段的初始化器只在存活实例上有意义；载体上的初始化是一次小的无用分配，分析器对「字段初始化器里做重活」给出警告（`AW1002`），重初始化请放进 `OnMount` 或惰性 `State`。

子内容透传：组件可声明 `public NodeList Children { get; set; }` 或具名的插槽属性（`Header`、`Footer`），在自己的 `Build()` 里放置。

## State

```csharp
public sealed class State<T>
{
    public State(T initial, IEqualityComparer<T>? comparer = null);
    public State(Func<T> lazyInitial, IEqualityComparer<T>? comparer = null);
    public T Value { get; set; }                 // 读：在 Build/Computed/Effect 中自动登记依赖；写：值相等则忽略
    public T Peek();                             // 读取但不登记依赖
    public void Update(Func<T, T> updater);
}

public sealed class Computed<T>
{
    public Computed(Func<T> compute, IEqualityComparer<T>? comparer = null);
    public T Value { get; }
    public T Peek();
}

public static class Reactive
{
    public static IDispatcher? DefaultDispatcher { get; set; }        // 进程级
    public static IDispatcher? CurrentThreadDispatcher { get; set; }  // 当前线程的（UIRoot 构造时注册）
    public static void Batch(Action action);                 // 合并多次写入
    public static IDisposable Effect(Action effect);         // 组件外的副作用；另有 Func<Action?> 重载（返回清理函数）
    public static T Untrack<T>(Func<T> read);
}
```

- **算法**：写入时向下推送 Dirty/Check 标记，重跑前向上拉取并核对实际读过的源的版本号，因此没有中间态（无毛刺）；`Computed` 算出相等的值时下游不重建。没有读者的 `Computed` 不挂接到源上（不会泄漏），用全局版本计数判断是否可能过期。依赖每次运行重新追踪，常见情况不分配。`Computed` 会缓存异常并在依赖变化前重复抛出；在 `Computed` 中写 State 直接抛异常；`Reactive.Effect` 写入自己读取的值超过 1000 轮视为循环（`AW1005`）。
- `Computed` 链的刷新按链长递归，程序化构造的数万级长链仍可能耗尽栈（UI 输入不会产生这种链，未处理）。
- **不可变更新**：State 以值相等判断是否变化，集合类状态应替换而不是原地修改（分析器对 `state.Value.Add(...)` 给出提示 `AW1003`）。
- **线程**：每个 State 在构造时记住一个调度器（当前线程的 `CurrentThreadDispatcher`，否则 `DefaultDispatcher`；都没有时每次写入使用当时的 `DefaultDispatcher`）。其他线程的写入与 `Update` 被投递到该调度器；读取不做线程检查。响应式运行时（追踪栈、批处理、待执行 effects）按线程独立。
- **粒度**：State 可以定义在组件外作为全局 Store，任何读取它的组件都会自动订阅。
- **异步**：`Resource<T>`（`Loading / Ready(value) / Failed(error)`）封装异步加载，配合 `switch` 表达式渲染。

## 协调器

对同一父节点下的新旧子列表：

1. 按「类型 + Key」匹配；无 Key 的按「类型 + 同类型中的序号」匹配。重复的 Key 报 `AW1008`（首次挂载时也检查），后出现的那个重新挂载。
2. 先按旧顺序卸载所有未匹配的旧节点，再按新顺序更新或挂载其余节点：
   - 元素节点 → 逐类型的 `Diff`（目前内建元素手写）把变化的属性、class、内联样式、事件处理器写到存活的 `Element` 上，只有真正变化的才打脏标记；
   - 组件节点 → `PropsEqual` 为真且自身无脏依赖则整棵子树跳过，否则 `AdoptProps` 后重建（`CanAdoptProps` 为假时重新挂载）；
   - `Build()` 返回同一个 Node 对象时整棵子树跳过。
3. 退出动画（延迟移除直到动画结束）尚未实现，目前移除是立即的。
4. 同步到元素树时只移动最长递增子序列之外的元素，带 Key 的重排移动次数最少；大列表改用哈希匹配。
5. 组件自己不产生元素：组件与 Fragment 的元素按顺序放进最近的祖先元素。`Element.Owner` 是渲染它的组件（包括插槽内容）。
6. 观察者只收到插入或移除子树的顶层元素通知；插入的子树打全部脏标记，父元素打 Style | Layout | Paint。

元素的事件处理器每次重建都是新委托，协调器只更新引用，不触发任何重算。

**栈深策略**：协调为了速度保持递归，但 Mount / Update 先检查剩余栈空间（`TryEnsureSufficientExecutionStack`，netstandard2.0/net48 上用 `EnsureSufficientExecutionStack` + catch）；不足时跳过该子树，把 `InsufficientExecutionStackException` 以 `ErrorPhase.Build` 报给最近的 `ErrorBoundary` 或 `UnhandledError`。卸载回调与分离元素用显式栈。

**帧驱动**：`UIRoot` 维护脏组件集合，自顶向下重建，每个组件每帧最多一次；对外暴露 `NeedsFrame`、`FrameRequested`（供平台帧时钟）与 `FramePipeline` 槽位（样式 / 布局 / 绘制阶段）。`Build()` 中写 State 第一次报 `AW1005` 警告；同一组件连续 50 帧（`MaxReentrantFrames`）在 Build 中被重新弄脏则报错误并停止重建它。`FrameRequested` 在 State 写入传播过程中同步触发，宿主必须**调度**一帧而不是在处理器里直接调用 `RunFrame`（目前未强制）。

实测（net10.0，非正式 Stopwatch，含首次 JIT）：父组件渲染 10 000 个带 Key 的子组件，挂载约 21 ms；Props 全相等的父组件重建 2.7–8 ms；整体反转（9 999 次移动）4–6.5 ms；100 万次经 Computed 与 Effect 的 State 写入约 250 ms。

## 错误处理

- `Build()` 或事件处理器抛出异常：由最近的 `ErrorBoundary` 组件捕获并渲染后备界面；没有边界时交给 `UIRoot.UnhandledError` 事件；仍无人处理则在本帧或本次分发结束时重新抛出。（规划中的 Debug 默认错误面板尚未实现。）
- `ErrorBoundary { Children; Fallback: Func<Exception, Node?>; OnError; Error; Reset() }`：在用后备界面重建之前，它只保留失败子树的第一个错误，并吸收该子树卸载过程中的错误；后备界面显示后，后备内容自身抛出的错误交给外层边界（或 `UnhandledError`）。提交阶段最多循环 1000 轮，使链式边界在同一帧内都切换到后备界面。
- 程序化焦点调用（`Element.Focus`、`FocusManager.Focus / ClearFocus / MoveFocus`）也是分发入口：检查 UI 线程，处理器中未被捕获的错误在最外层调用返回时抛出（帧内则在帧末）。
- 所有此类错误进入诊断通道（运行时异常的代码为 `AW0001`），DevTools 控制台可见并可跳转到组件源码。

## 命令式能力

```csharp
readonly Ref<Element> box = new();
...
new Div { Ref = box }
...
box.Current?.Focus();
box.Current?.ScrollIntoView();
var rect = box.Current?.GetBoundingRect();
box.Current?.Animate(keyframes, options);       // 命令式动画，返回可控制的 Animation 对象
```

目前 `Ref` 只支持元素（`ElementNode.Ref` 是 `Ref<Element>`）；`ScrollIntoView`、`Animate` 与真实的 `GetBoundingRect` 要等布局接入。

## 作用域样式

`X.cs` 旁的 `X.css` 由生成器关联到组件 `X`：其中的规则只匹配该组件 `Build()` 直接产出的元素（实现上给这些元素加一个作用域标记，并在选择器末段附加对应的属性选择器）。需要影响子组件内部时使用其暴露的 `::part()` 或 CSS 变量。

M1 实现（只针对 `.awaui` 的 `<style>`；纯 C# 组件旁的 `X.css` 关联尚未实现）：

- **编译期**：`.awaui` 编译器改写选择器 —— 作用域属性加在每个选择器最后一个复合选择器上、第一个伪元素之前（含 `:before / :after` 旧写法与 `::part`），`:is / :where / :not / :has` 内部不展开；嵌套规则与条件组规则（`@media`、`@supports`、`@layer`、`@container` 等）递归改写，`@keyframes`、`@font-face` 原样保留。单遍、显式栈、只插入文本。作用域 id = `"aw-"` + FNV-1a-32(`命名空间.类名`) 的 8 位十六进制（全小写）。该组件 `Build()` 创建的每个元素（含插槽内容与元素类型的标签，不含子组件的元素）设置 `ElementNode.__Scope`，`GetAttribute(scope)` 返回 `""`，所以 `.a[aw-xxxxxxxx]` 能匹配。样式文本以常量 `StyleSheetSource`（作用域）/ `GlobalStyleSheetSource`（`<style global>`）生成在类里。
- **运行时**：`Component` 有可重写的 `ScopedStyleSheet` / `GlobalStyleSheet`（默认 null），生成的类重写为上述常量。组件类型第一次挂载到某个 `UIRoot` 时（`Reconciler.Mount` 中、首次 `Build` 之前），其样式表作为 Author 来源加入管线：按首次挂载的顺序、同一类型先全局后作用域，按类型与文本去重，在根的生命周期内保留；`UIRoot.ComponentStyleSheets` 列出它们，解析诊断经 `UIRoot.Diagnostic` 报告。之后才接上的管线会补收已注册的样式表。DevTools 中这些样式表的 `sourceUrl` 是组件类型全名（全局的加 ` (global)`）。

## 元素与事件（M1 实现）

- `Element` 的成员名与 `AwAUI.Css.Selectors.StyleElement` 刻意一致（`Tag`、`Id`、`Classes`、`GetAttribute`、`Parts`、兄弟/索引访问等），自带与 Css 版位值相同的 `PseudoStates` 枚举，带稳定数字 id（供 DevTools）与 `Owner` 组件。文本节点（`TextNodeElement`，标签 `#text`）不计入元素兄弟。内建字段通过 `GetAttribute` 对属性选择器可见：id、class、style、role、aria-label、tabindex、src、alt、disabled、type、value、placeholder、readonly。根元素标签为 `root`。
- 脏标记 `ElementDirtyFlags { Style, Layout, Paint, Semantics }` 向祖先传播（`DescendantDirtyFlags`）；`ElementTreeObserver` 接收树变更通知（顺序见 [05](05-ui-core.md)）；公开 `ComputedStyle` 与 `BoundingRect`，样式、布局、绘制数据在一个内部的强类型字段里（原来的三个 `object?` 槽位已合并）。
- **事件分发**：捕获 → 目标 → 冒泡，`StopPropagation / StopImmediatePropagation / PreventDefault`。与 DOM 一样，目标上的捕获与冒泡处理器分两轮调用、每轮先检查停止标志，所以在目标的 `On*Capture` 里 `StopPropagation` 也会跳过目标的冒泡处理器；每元素每阶段只有一个处理器，两种停止在同一元素上效果相同。
- **指针**：指针捕获；悬停的 enter/leave 只在祖先链变化的部分触发（enter 由外到内，leave 由内到外）；触摸指针只在按下时悬停。`click` 只对左键、在按下与抬起目标的共同祖先上合成，禁用控件内不触发。`pointerdown` 聚焦最近的可聚焦祖先，除非处理器调用了 `PreventDefault`。命中测试由宿主提供的 `HitTester` 完成，M1 由 `RenderPipeline` 实现（按绘制顺序，见 [05](05-ui-core.md)）。
- **键盘与焦点**：Tab / Shift+Tab（正 tabindex 优先，其次树序，循环）；Enter、Space 激活按钮，Input 中 Enter 提交、Backspace 删除；焦点离开按钮时取消未完成的 Space 激活。focus-visible：键盘聚焦总是；指针聚焦只对文本输入框；程序化聚焦沿用上次的输入方式，仅修饰键不算键盘输入。事件顺序 blur → focusout → focus → focusin；被聚焦的子树移除时静默清除焦点。`KeyGesture` / `Shortcuts` 解析 `"Mod+S"` 并做 Ctrl ↔ Cmd 映射。
- 已知缺口：没有 HTML 的「焦点修正」（聚焦元素变为禁用或失去 TabIndex 时仍保持焦点）；禁用元素仍保留 `:hover / :active` 并收到 pointerdown/up（click 被抑制）。

## 图片、SVG 与增量绘制（第六批）

全部在命名空间 `AwAUI.UI`：

```csharp
public abstract class ImageSource : IEquatable<ImageSource>   // 相等 = 类型 + Key
{
    public abstract object Key { get; }
    public virtual bool PreferAsync { get; }                 // 是否在线程池解码
    protected internal abstract ImageContent Load(ImageLoadContext context);
    public static ImageSource FromBitmap(Bitmap bitmap);     // 调用方持有，框架不释放；显示期间不要改写或释放
    public static ImageSource FromBytes(byte[] data, string? key = null);
    public static ImageSource FromStream(Stream stream, string? key = null);   // 第七批：≤ MaxRetainedStreamBytes 时保留编码字节，可重复加载
    public const int MaxRetainedStreamBytes = 16 * 1024 * 1024;
    public static ImageSource FromFile(string path);
    public static ImageSource FromResource(string name);     // 经 ImageCache.ResourceResolver
    public static ImageSource FromUri(string uri);           // data: URI / 资源 / 文件
    public static ImageSource FromSvg(SvgDocument document);
    public static implicit operator ImageSource?(string? uri); // 所以 <img src="..."> 无需改动
}
```

- `ImageLoadContext`（`ResourceResolver`、`BaseDirectory`、`MaxEncodedBytes`、`SvgOptions`、`AsyncThresholdBytes` = 64 KB、`ReadAll`）；`ImageContent`（`Bitmap`、`Animation`、`Vector`、`NaturalSize`，`Decode`）；`ImageLoadState { Loading, Ready, Failed }`。
- `ImageCache`（`UIRoot.Images`）：`ResourceResolver`、`BaseDirectory`、`MaxEncodedBytes`（默认 64 MB）、`SvgOptions`（为 null 时等同 `ImagingSvgImageResolver.DefaultOptions`；给定的选项原样使用，需要自己设置 `ImageResolver`）、`AsyncDecoding`、`Count`、`PendingLoads`、事件 `Loaded`（第七批起推迟触发：从不在布局或绘制中途触发）；`MinFrameDuration`（10 ms）、`ClampedFrameDuration`（100 ms）、`FrameDuration(AnimatedImage, int)`。
- `UIClock`（抽象：`Now`、`Schedule(TimeSpan, Action)`、`CreateSystem(IDispatcher)`）与测试用的 `ManualUIClock`（`Advance`、`PendingCount`、`NextDue`）；`UIRoot.Clock { get; set; }`。
- ~~`ObjectFit`、`ImageRendering`、`ObjectPosition`~~：第七批删除（连同 `Image.Fit / Position / Rendering`、`ImageElement.Fit / ObjectPosition / Rendering` 与 `SetPresentation`）。这些值只来自 CSS，与 HTML 一致（决策 D35）；定位改用内部的 `ObjectFitting.Place / Crop / SnapToDevice`，计算值类型见 [16](16-css-properties.md)。
- 元素：抽象基类 `ReplacedElement`；`ImageElement`（`Source`、`LoadState`、`Content`、`CurrentFrame`、`AccessibleName` = `aria-label`，否则 `Alt`）；`SvgElement`（`Document`、`Recordings`）。
- `ImagingSvgImageResolver`（第七批，sealed，派生自 `AwAUI.Svg.SvgImageResolver`，`Instance`、`DefaultOptions`）：用 `AwAUI.Imaging` 解码 SVG `<image>` 的 `data:` URI，坏数据或不支持的格式返回 null，不加载外部引用；无状态，可在线程池上使用。
- `Icon` 组件：`Document`、`Size`、`Label`、`Class`。
- 增量绘制：`RenderPipeline.IncrementalPaint { get; set; }`、`LastDamage`（`Rect?`）、`LastRecordedElements`；`FrameTimings.RecordedElements / ReusedElements`（新增 10 参数构造函数，旧的保留）；`UIHost.PartialRaster { get; set; }`（默认每帧按后端能力判断，显式赋值后固定）、`UIHost.LastDamage`（`PixelRect?`）；`UIHost.BackendName` 反映后端当前的 `Name`（GPU 回退到软件后随之变化）。
- 丢帧（第七批）：`UIHost.DroppedFrames`（`long`，渲染目标报告未呈现的帧数）；丢帧那一帧的 `HostFrameStatistics.Rendered` 为 false（见 [05](05-ui-core.md)「增量绘制」）。

语义见 [05](05-ui-core.md)「增量绘制」与 [03](03-renderer.md)「图片」。第七批起 `.awaui` 的 `<img>` 的 `width / height` 映射到 `Image.Width / Height`（数值），内联 `<svg>` 映射到 `Svg` 元素（见 [17](17-awaui-grammar.md)）。

## 文本输入（第七批）

- `InputElement`（`Input/InputElement.cs`）新增：`IsPassword`、`SelectionStart`、`SelectionEnd`、`SelectionAnchor`、`SelectionFocus`、`SelectedText`、`IsComposing`、`CompositionText`、`CompositionStart`、`CompositionSelectionStart`、`CompositionSelectionEnd`、`SetSelectionRange(anchor, focus)`、`SelectAll()`；`GetReflectedAttribute` 提供 `selectionStart` / `selectionEnd`。
- `InputManager`：`TextInputMethod { get; set; }`、`Clipboard { get; set; }`、`TextInputTarget`、`AfterFrame()`、`const MaxCompositionLength = 4096`。内部的 `InputTextClient` 实现 `AwAUI.Platform.ITextInputClient`。
- `KeyEvent.Location`；`Shortcuts.IsPrimaryShortcut(mods)`、`Shortcuts.ShortcutKey(physical, logicalKey)`；`KeyGesture.Matches(KeyEvent)` 的字母键按逻辑键匹配。
- 行为见 [05](05-ui-core.md)「文本输入」，平台接口见 [04](04-platform.md)。

## 诊断代码（面向人与 AI 的可操作信息）

| 代码 | 含义 |
|---|---|
| AW1001 | 组件内部不应给自己的 Props 赋值 |
| AW1002 | 字段初始化器开销过大，应移到 `OnMount` 或惰性 State |
| AW1003 | 原地修改 State 中的集合不会触发更新 |
| AW1004 | 动态生成的子列表缺少 `Key` |
| AW0001 | 运行时异常（转入诊断通道） |
| AW1005 | 在 `Build()` 中写 State 会导致循环重建（运行时：首次为警告；连续 50 帧在 Build 中被重新弄脏则为错误并停止重建该组件；`Reactive.Effect` 之间循环超过 1000 轮时抛出的异常也带此码） |
| AW1006 | 组件类不是 `partial`，生成器无法补出胶水，其 Props 不会更新（生成器，警告；同时手写 `PropsEqual` 与 `AdoptProps` 时不报） |
| AW1007 | 嵌套组件的外层类型不是 `partial`（生成器，警告） |
| AW1008 | 同级出现重复的 `Key`，后出现的节点被重新挂载（运行时，警告） |
| AW1009 | 组件是 `file` 局部类型，无法生成 Props 胶水（生成器，警告） |
| AW1010 | 图片加载或解码失败：格式无法识别、数据损坏、超过 `ImageCache.MaxEncodedBytes`、文件或资源不存在等（运行时，警告；元素什么都不显示）。不用 AW2001 是因为它已被 CSS 诊断占用 |
| AW2001 | 使用了任何样式表中都未定义的 class |
| AW2002 | CSS 属性不被支持（警告；消息给出计划支持的期、替代写法或按编辑距离的名称建议） |
| AW2003 | CSS 值无效（错误；范围覆盖该值） |
| AW2004 | 不支持或未知的选择器特性，该规则被丢弃 |
| AW2005 | 不支持或未知的 at 规则，保留但不生效 |
| AW2006 | 选择器语法错误；含函数式伪类嵌套超过 32 层、或匹配递归上界超过 1024 的选择器 |
| AW2007 | 媒体查询无效（整条按规范视为 `not all`）或使用了不支持的媒体特性 |
| AW2008 | CSS 语法错误（分词或规则结构），按 CSS Syntax 3 错误恢复 |
| AW2009 | at 规则的序言或位置无效（如 `@import` 出现在其他规则之后），该规则被丢弃 |
| AW2010 | 声明位置错误（含 @keyframes 中的 `!important`） |
| AW2011 | 规则出现在不允许嵌套规则的位置（如 `@font-face` 块内），该规则被丢弃 |
| AW3001 | `Image` 缺少 `Alt`；可点击元素缺少可访问名称 |

目前已实现：AW0001、AW1001、AW1005–AW1010，以及 CSS 的 AW2002–AW2011（AW2001 需要构建期分析器）；AW1002–AW1004 与 AW3001 尚未实现。生成器诊断使用重建的外部位置（路径 + 范围），源码中的 `#pragma warning disable` 无法抑制，需通过 `.editorconfig` 或 `NoWarn`。

每条诊断都带明确的位置和修复建议，可提供自动修复的尽量提供。
