# 14 `.awaui` 单文件组件格式

`.awaui` 是默认的 UI 编写方式：一个文件内包含**标记、C# 逻辑、作用域样式**。实现思路与 Blazor 的 Razor 引擎相同 —— 构建期把 `.awaui` 编译成普通的 C# 组件类 —— 但编译器是自研的、独立的一套实现，核心框架不依赖它。

```
Counter.awaui ──(AwAUI.Markup 编译器，构建期)──► Counter.awaui.g.cs（partial class Counter : Component）
                                                      │  使用 12-component-api.md 定义的 Node API
                                                      ▼
                                                 C# 编译器 → 程序集
```

两种写法的关系：

| | `.awaui` | 纯 C#（[12](12-component-api.md)） |
|---|---|---|
| 定位 | 默认、推荐的编写方式 | 底层 API，也是 `.awaui` 的编译目标 |
| 适合 | 绝大多数界面组件 | 高度动态的结构、库作者、不想引入标记编译器的项目 |
| 互操作 | 两者产出的都是同一种 `Component`，可以在同一项目里混用、互相引用 | |

## 一个例子

```razor
@* TodoApp.awaui *@
@using MyApp.Models

<div class="todo-app">
    <text class="title">待办事项（@todos.Value.Count）</text>

    <div class="row">
        <input @bind="draft" placeholder="要做什么？" @onsubmit="Add" />
        <Button class="primary" @onclick="Add">添加</Button>
    </div>

    @if (todos.Value.Count == 0)
    {
        <text class="empty">暂无事项</text>
    }
    else
    {
        <div class="list">
            @foreach (var t in todos.Value)
            {
                <TodoRow @key="t.Id" Todo="t" OnRemove="Remove" />
            }
        </div>
    }
</div>

@code {
    public string Title { get; set; } = "待办";          // Props：公共可写属性

    readonly State<List<Todo>> todos = new(new List<Todo>());
    readonly State<string> draft = new("");

    void Add()
    {
        if (draft.Value.Length == 0) return;
        todos.Update(l => l.Append(new Todo(Guid.NewGuid(), draft.Value)).ToList());
        draft.Value = "";
    }

    void Remove(Todo t) => todos.Update(l => l.Where(x => x.Id != t.Id).ToList());
}

<style>
    .todo-app { display: flex; flex-direction: column; gap: 12px; padding: 16px; }
    .row { display: flex; gap: 8px; }
    .row > input { flex: 1; }
    .empty { color: var(--text-secondary); }
</style>
```

语法刻意贴近 Razor/Blazor：这是开发者和 AI 已经熟悉的写法，迁移成本最低。

## 语法

### 文件结构

| 部分 | 说明 |
|---|---|
| 指令 | 文件顶部：`@using`、`@namespace`、`@inherits`、`@implements`、`@attribute`、`@typeparam`（泛型组件）；同目录及祖先目录的 `_Imports.awaui` 可提供全局 `@using`，项目的 C# 全局 using（`<Using Include="..." />`）与 MSBuild 属性 `AwAUIMarkupNamespaces` 同样参与解析（见 [17](17-awaui-grammar.md)） |
| 标记 | 组件的界面，编译为 `Build()` 的返回值；允许多个根节点（编译为片段） |
| `@code { }` | 类成员：Props、State、方法、生命周期重写。可有多个，也可改用同名的 `X.awaui.cs` 代码后置文件（`partial class`，与 Blazor 的 `X.razor.cs` 对应） |
| `<style>` | 作用域样式，等价于同名的 `X.awaui.css`（见 [08](08-css.md)）；`<style global>` 为全局样式，等价于 `X.awaui.global.css` |
| 样式文件 | `X.awaui.css` 由 MSBuild 自动纳入 `AdditionalFiles`，生成器按文件名关联到 `X`：选择器附加作用域属性（唯一），`::deep` 之后的部分穿透到子组件内部；`X.awaui.global.css` 不限定作用域、随组件注册。项目级共享样式用 `<AwAUIGlobalStylesheet Include="..." />`，生成 `AwAUIGlobalStyles` 并在模块初始化时登记到 `AppStyleSheets` |

类名取自文件名，命名空间取自项目根命名空间 + 目录（可用 `@namespace` 覆盖）。

### 标签

| 写法 | 含义 |
|---|---|
| 小写标签 `<div>` `<span>` `<text>` `<img>` `<input>` `<textarea>` `<canvas>` `<svg>` | 内建元素（固定的一张表，未知的小写标签是编译错误） |
| 大写开头 `<Button>` `<TodoRow>` | 组件，按 C# 的命名空间、`@using` 与框架内置命名空间解析；标准控件只需写类名 |
| `<Ns.Component>` | 带限定名的组件 |
| `<Component T="int">` | 泛型组件，类型参数可推断时可省略 |

元素内直接书写的文本自动成为文本节点，不必手写 `<text>`；需要给文字单独加样式时才用 `<text>` / `<span>`。

内联 `<svg>…</svg>`（第七批）：整个子树在编译期序列化为 SVG 文本，运行时解析一次并在组件类型的所有实例间共享，所以内联 svg 里不能有动态内容（表达式、控制流、组件、子元素上的指令都报 AW4012）；动态文档用 `<Svg Document="@doc" />`。`<img>` 的 `width` / `height` 是数值。细节见 [17](17-awaui-grammar.md)「代码生成」。

### 属性

| 写法 | 含义 |
|---|---|
| `class="a b"` | 字面量。内建元素的属性按属性表转换为强类型（`tabindex="0"` → `int`，`disabled` → `true`） |
| `class="tab @(active ? "on" : "")"` | 字面量与表达式混合（仅字符串类型的属性） |
| `Todo="t"`、`Count="@(a + b)"` | 组件属性：值是 C# 表达式（与 Blazor 一致：非字符串类型的属性值按表达式解析） |
| `@onclick="Add"`、`@onclick="e => Select(e, item)"` | 事件处理器：方法组或 lambda |
| `@bind="draft"`、`@bind:event="change"` | 双向绑定语法糖：展开为赋值属性 + 变更事件，目标是 `State<T>` |
| `@key="t.Id"` | 协调用的 Key |
| `@ref="box"` | 获取元素或组件引用 |
| `@attributes="dict"` | 属性透传 |
| `style="padding: @(p)px"` | 内联样式；纯字面量部分在编译期解析校验 |
| `aria-label`、`role`、`data-*` | 无障碍与自定义属性 |

内建元素的属性名采用 HTML 的拼写（小写、连字符），组件属性名与 C# 属性名完全一致。未知属性是编译错误，并提示最接近的名称。

### C# 嵌入

| 写法 | 含义 |
|---|---|
| `@name`、`@obj.Prop`、`@Method(x)` | 隐式表达式 |
| `@(a + b)` | 显式表达式 |
| `@if / else if / else`、`@switch` | 条件 |
| `@foreach`、`@for`、`@while` | 循环（循环体内的组件与元素缺少 `@key` 时给出警告） |
| `@{ var x = ...; }` | 代码块（局部变量、局部计算） |
| `@* 注释 *@` | 注释，不进入输出 |
| `@@` | 字面量 `@` |

代码块内可以再写标记，标记内可以再写代码，任意嵌套。

### 插槽与模板

```razor
<Card>
    <Header><text class="h">标题</text></Header>      @* 具名插槽：对应 Card 上的 NodeList 类型属性 Header *@
    正文内容                                          @* 默认插槽：对应 Children *@
</Card>

<VirtualList Items="rows">
    <ItemTemplate Context="row">                      @* 带参数的模板：对应 Func<T, Node> 类型属性 *@
        <RowView Data="row" />
    </ItemTemplate>
</VirtualList>
```

### 空白

采用与 JSX 相近的规则：仅由空白和换行组成的文本被丢弃，行首行尾的缩进被去除，行内空白保留。结果可预测，不会因为格式化代码而改变界面。

## 编译器

独立的程序集，纯 C#，`netstandard2.0`：

| 程序集 | 作用 |
|---|---|
| `AwAUI.Markup.Compiler` | 编译器本体：`.awaui` 文本 → C# 源码 + 诊断 + 源映射。不依赖 Roslyn，可在任何地方调用（生成器、命令行、测试、工具） |
| `AwAUI.Markup.Generator` | Roslyn 增量源生成器：收集项目中的 `*.awaui`，调用编译器，把结果加入编译 |
| `AwAUI.Markup`（NuGet 包） | 打包上面两者 + MSBuild `.props/.targets`（把 `*.awaui` 自动纳入构建输入） |
| `awaui` 命令行工具 | `awaui compile`（查看生成的 C#）、`awaui format`、`awaui check` |

M1 现状：

- `AwAUI.Markup.Compiler` 的入口是 `MarkupCompiler.Compile(text, filePath?, options?)`，返回 `MarkupCompilation`（生成的代码、按位置排序的全部诊断、类名与命名空间、作用域 id、作用域与全局样式文本）。
- `AwAUI.Markup.Generator`（`src/Markup/AwAUI.Markup.Generator`）已实现：Roslyn 4.14 的 `IIncrementalGenerator`，以源码链接的方式包含编译器、`src/Shared/CodeGen` 与 polyfill（分析器不能带普通依赖；因此编译器的公共类型也存在于生成器程序集里）。管线值全部值相等，无关的编辑不重新生成，改一个文件只重新生成该文件。
- NuGet 包 `AwAUI.Markup`：生成器 DLL 放在 `analyzers/dotnet/cs`，`build/` 与 `buildTransitive/` 下有 `AwAUI.Markup.props`（`CompilerVisibleProperty` 暴露 `RootNamespace`、`ProjectDir`）与 `AwAUI.Markup.targets`（把 `**/*.awaui` 加入 `AdditionalFiles`，`EnableDefaultAwauiItems=false` 关闭；附带 Watch 项）。用 PackageReference 的使用方无需额外设置（已用真实包验证）。
- 仓库内用 ProjectReference 的使用方（MSBuild 不会为 ProjectReference 自动导入）必须显式 `Import` 上述 `.props` / `.targets`，并以 `OutputItemType="Analyzer"`（`ReferenceOutputAssembly="false"`）引用生成器；`samples/Counter` 是示例，它设 `RootNamespace=CounterSample`，所以 `Markup/` 下的组件在 `CounterSample.Markup`。
- 命名空间 = `RootNamespace` + 各级文件夹名（`@namespace` 可覆盖），每段都清洗为合法标识符（`my-app.2d` → `my_app._2d`）。hint name = 相对路径（`/` 换成 `.`，其他字符换成 `_`）+ 路径的 8 位哈希；链接进来的同名文件在解析阶段按稳定顺序加 `_2`、`_3`……
- `awaui` 命令行、开发期样式推送与热重载尚未实现；`<style>` 与内联样式没有编译期 CSS 校验（编译器不能依赖 `AwAUI.Css`），错误在运行时以 AW2xxx 报告。

### 管线

```
源文本
 → 词法/语法分析   标记与 C# 的切换；C# 片段只做轻量扫描（括号/引号/注释配对）以确定边界，
                    不做完整的 C# 语法分析，类型检查交给 C# 编译器
 → 语法树          保留全部位置信息与错误恢复（一个文件尽量报出所有错误）
 → 语义处理        标签分类（元素 / 组件 / 插槽）、内建元素属性的类型转换、指令处理、
                    @bind 展开、Key 检查、<style> 提取并交给 CSS 编译管线
 → 中间表示        与输出语言无关的组件描述
 → C# 生成         partial class + Build()，目标是 12 的 Node API；
                    带 #line 映射，编译错误与断点直接落在 .awaui 的行列上
```

### 设计要点

- **类型安全全部来自 C# 编译器**：标记编译器只负责结构，属性类型不匹配、成员不存在等错误由生成的 C# 触发，经 `#line` 映射回 `.awaui`。
- **生成器之间互不可见的问题**：Roslyn 源生成器看不到其他生成器的输出。两个后果及对策：
  - `.awaui` 组件之间互相引用时需要对方的属性信息 → 生成器先做一遍**声明阶段**（只提取各文件的类骨架），再做完整生成，与 Razor 相同（详见 [17](17-awaui-grammar.md)）。
  - `.awaui` 产生的组件无法再被「组件胶水生成器」处理 → 胶水代码的生成逻辑做成共享库，标记编译器在生成组件类时直接产出，且胶水只依赖语法层面的信息（「公共可写属性即 Props」）。共享库已实现为 `src/Shared/CodeGen/ComponentGlue.cs`（`ComponentGlueModel` / `ComponentGlueEmitter`，以源码链接方式共享），C# 源生成器 `AwAUI.Generators` 与标记编译器都在使用（见 [17](17-awaui-grammar.md)）。
  - **胶水归属**：`.awaui` 类的 Props 胶水只由标记生成器产出（它从 Roslyn 类信息看到 `@code`、代码隐藏文件与没有胶水的基类中的 Props）。C# 胶水生成器看不到生成的代码，并且跳过在 `*.awaui.cs` 文件里有声明的类。所以代码隐藏文件**要命名为 `X.awaui.cs`**，或者不重复写 `: Component` 基类列表；否则名为 `X.cs` 且写了基类的代码隐藏文件会让两个生成器都产出 `PropsEqual / AdoptProps`（CS0111）。
  - 手写的 C# 组件派生自 `.awaui` 组件时拿不到胶水：C# 生成器看不到生成的基类（Razor 有同样的限制）。
- **生成的 C# 使用保守的语法**（C# 7.3 可编译），因此 .NET Framework 项目即使不提升语言版本也能使用 `.awaui`；`@code` 块内用什么语法则取决于项目自己的语言版本。
- **源映射**同时供 DevTools 使用：元素与组件可以跳转到 `.awaui` 中的具体标签。
- **增量**：每个 `.awaui` 独立编译，互不影响；只有改动的文件重新生成。
- **诊断**沿用统一的代码体系（`AW4xxx` 为标记相关），带位置和修复建议。

### 开发期更新

| 改动 | 生效方式 |
|---|---|
| `<style>` 内容或 `X.awaui.css` | 开发服务器把新的样式文本经 DevTools 通道推给运行中的应用，走运行时 CSS 路径即时生效，不需要重新编译；新文本在应用侧用同一个 `ScopedCss.Rewrite` 与作用域改写（DevTools 推送本身尚未接入） |
| 标记或 `@code` | 重新生成 C# → .NET 热重载应用增量（现代 .NET）；.NET Framework 上需重新启动 |

## 工具支持

自定义格式最大的成本不在编译器，而在编辑器体验，需要分期投入：

| 阶段 | 内容 |
|---|---|
| 1 | 语法高亮（TextMate 语法，VS Code / Rider / Visual Studio 通用）；构建错误精确定位；`awaui format` |
| 2 | 语言服务器：标记部分的补全（标签、属性、class 名、CSS 属性）、诊断、悬停、跳转 |
| 3 | C# 片段的完整智能感知：把 `.awaui` 投影为虚拟 C# 文档转交 C# 语言服务，再把结果映射回来（Razor 工具链的做法，工作量大） |

对 AI 编码而言，阶段 1 已经足够：模型不依赖编辑器补全，依赖的是**清晰的语法规则 + 精确的编译诊断 + 运行后可检查**（见 [06](06-devtools.md) 的 AI 闭环）。

## 已确定的细节

正式文法、C# 边界规则、代码生成形状与诊断见 [17-awaui-grammar.md](17-awaui-grammar.md)。内建元素名为 `div span text img input textarea canvas svg path`；`@bind` 支持 `State<T>` 与任意可赋值表达式；代码块只有 `@code { }` 一种；编辑器支持先做语法高亮与精确诊断，语言服务器后置。
