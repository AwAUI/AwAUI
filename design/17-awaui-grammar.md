# 17 `.awaui` 文法

[14](14-awaui-format.md) 的正式化：词法模式、文法、C# 边界规则、代码生成形状与诊断。此前留待讨论的几点在这里按建议确定：

- `@bind` 的目标可以是 `State<T>`，也可以是任意可赋值表达式（字段、属性、索引器）。
- 代码块只有 `@code { }` 一种写法，不提供别名。
- 内建元素名：`div` `span` `text` `img` `input` `textarea` `canvas` `svg` `path`，以及片段 `<>…</>`。

## 词法模式

解析器在三种模式间切换，由当前上下文决定：

| 模式 | 进入 | 离开 |
|---|---|---|
| **标记** | 文件开头；代码模式中在语句位置遇到 `<` + 标签名或 `<>` | 遇到 `@`；标签闭合且处于代码块内 |
| **代码** | `@` 之后；`@code {`、`@{`、控制流的 `{` 内 | 表达式结束；配对的 `}` |
| **样式** | `<style>` | `</style>`（内容原样交给 CSS 解析器，其中不解释 `@` 转义以免与 at 规则冲突） |

文件编码 UTF-8；换行不敏感。

## 文法（EBNF）

```ebnf
document      = { directive } { top-item } ;
top-item      = markup-node | code-block | style-block | comment ;

directive     = "@using" cs-name [ "=" cs-name ] NEWLINE
              | "@namespace" cs-name NEWLINE
              | "@inherits" cs-type NEWLINE
              | "@implements" cs-type NEWLINE
              | "@typeparam" IDENT [ cs-constraint ] NEWLINE
              | "@attribute" "[" cs-balanced "]" NEWLINE ;

code-block    = "@code" "{" cs-class-members "}" ;
style-block   = "<style" [ WS "global" ] ">" css-text "</style>" ;
comment       = "@*" { ANY } "*@"
              | "<!--" { ANY } ( "-->" | "--!>" ) ;   (* 第八批：HTML 注释，与 @* *@ 等价 *)

markup-node   = element | fragment | text | expression | control | stmt-block | comment ;

element       = "<" tag-name { attribute } ( "/>" | ">" { markup-node } "</" tag-name ">" ) ;
fragment      = "<>" { markup-node } "</>" ;
tag-name      = intrinsic-name                      (* 小写开头：内建元素 *)
              | cs-name [ "<" cs-type-args ">" ] ;  (* 大写开头：组件或插槽 *)

attribute     = attr-name [ "=" attr-value ]
              | "@" directive-attr [ ":" IDENT ] [ "=" attr-value ] ;
attr-name     = IDENT { ( "-" | ":" ) IDENT } ;
directive-attr= "key" | "ref" | "bind" | "attributes" | "on" IDENT ;
attr-value    = '"' { attr-text | expression } '"'
              | "'" { attr-text | expression } "'"
              | expression ;                        (* 无引号：@expr 或 @(expr) *)

text          = { CHAR - ( "<" | "@" ) | "@@" | entity } ;
entity        = "&" ( IDENT | "#" DIGITS | "#x" HEX ) ";" ;

expression    = "@" implicit-expr | "@(" cs-balanced ")" ;
stmt-block    = "@{" cs-statements "}" ;

control       = "@if" "(" cs-balanced ")" body { "else" "if" "(" cs-balanced ")" body } [ "else" body ]
              | "@foreach" "(" cs-balanced ")" body
              | "@for" "(" cs-balanced ")" body
              | "@while" "(" cs-balanced ")" body
              | "@switch" "(" cs-balanced ")" "{" { switch-section } "}" ;
body          = "{" { cs-statement | markup-node } "}" ;
switch-section= ( "case" cs-balanced ":" | "default" ":" ) { cs-statement | markup-node } ;
```

## C# 边界规则

编译器不解析完整的 C#，只做足以确定边界的轻量扫描：

- **`cs-balanced`**：扫描到配对的结束定界符，期间正确跳过嵌套的 `() [] {}`、字符串（普通、逐字、内插、原始字符串）、字符字面量、注释。
- **隐式表达式 `@name…`**：从标识符开始，贪婪地接受后续的
  `.标识符`、`?.标识符`、`(…)`、`[…]`、`!`（后接 `.` 或 `[` 时）；遇到其他任何字符即结束。
  `@user.Name` 是一个表达式；`@user.Name.` 的末尾句点属于文本；需要泛型调用、运算符、`await` 时使用显式形式 `@( … )`。
  邮箱式文本（`a@b.com`，`@` 前紧邻字母数字）不视为表达式。
- **代码中的标记**：在 `body` 与 `@{ }` 内，处于语句开始位置的 `<` 后接标签名或 `>` 即切换到标记模式；元素闭合后回到代码模式。因此 `if (a < b)` 中的 `<` 不会被误判。
- **代码块中的单行文本**：`@:` 之后到行尾按标记处理（用于在代码块里输出不带标签的文本）。

## 属性语义

| 目标 | 值的解释 |
|---|---|
| 内建元素的已知属性 | 纯字面量按属性表的类型在编译期转换（数值、布尔、枚举、长度）；含表达式时：字符串类型做拼接，其他类型要求整个值是单一表达式 |
| 内建元素的布尔属性 | 只写属性名即为 `true`；`disabled="@flag"` 按表达式 |
| `class` | 字面量与表达式可混合；表达式可为 `string`、`ClassValue` 或 `bool` 条件对 |
| `style` | 字面量部分编译期解析为强类型声明；含表达式的声明运行时解析该条声明的值 |
| 组件的 `String` 类别属性 | 引号内是字面量，可混合 `@expr` |
| 组件的其他类别属性 | 引号内整体是 C# 表达式（`Count="items.Length"`），`@` 可省略；`Bool` 类别只写属性名即为 `true` |
| `@onxxx` | 方法组、lambda，或返回委托的表达式；参数个数可为 0 或 1（编译器按需包装） |
| `@bind` | 展开为「值属性 + 变更事件」。`input` 默认 `value` + `oninput`；`@bind:event="change"` 改为失焦时提交；组件上 `@bind-Value="x"` 对应 `Value` + `ValueChanged` |
| `@key` / `@ref` / `@attributes` | 见 14 |

### 组件信息从哪里来

区分「字符串属性 / 表达式属性 / 插槽」需要知道组件有哪些属性、各是什么类型。编译器本体不依赖 Roslyn，通过一个抽象的**组件目录**查询：

```csharp
public abstract class ComponentCatalog
{
    // 按当前文件的 @using 与命名空间解析标签名；找不到返回 null
    public abstract ComponentInfo? Resolve(string tagName, ImportScope scope);
}

public sealed class ComponentInfo
{
    public string FullName { get; }
    public IReadOnlyList<PropInfo> Props { get; }     // 名称 + 类别
}

public enum PropKind { String, Bool, Slot /* NodeList */, Template /* Func<T, Node> */, Delegate, Other }
```

源生成器分两个阶段构造目录（与 Razor 的做法相同）：

1. **声明阶段**：对项目中每个 `.awaui` 只解析指令与 `@code` 块的成员声明，生成只有签名的类骨架，加入一份临时编译。
2. **生成阶段**：临时编译里同时有手写的 C# 组件、引用程序集中的组件（如标准控件）和所有 `.awaui` 组件的骨架，据此实现 `ComponentCatalog`，再对每个文件做完整的代码生成。

命令行工具等没有 Roslyn 的场景使用一个纯语法的目录实现（只认识本项目的 `.awaui` 文件与随包分发的控件清单）。

## 插槽

父标签是组件时，子标签名若与父组件的某个 `Slot` 或 `Template` 类别的属性同名，就是具名插槽，生成属性赋值；否则按普通子节点处理并归入默认插槽 `Children`。两者同名冲突时插槽优先，并给出警告，提示用限定名引用组件。

- `Slot`（`NodeList`）：`<Header>…</Header>` → `Header = …`
- `Template`（`Func<T, Node>`）：`<ItemTemplate Context="row">…</ItemTemplate>` → `ItemTemplate = row => …`；省略 `Context` 时参数名为 `context`

## 代码生成

输入：

```razor
<div class="counter">
    <text>计数：@count.Value</text>
    @if (count.Value > 0)
    {
        <Button @onclick="Reset">重置</Button>
    }
</div>

@code {
    readonly State<int> count = new State<int>(0);
    void Reset() => count.Value = 0;
}
```

输出（与 M1 实现一致的示意，省略 `#line`、`#nullable` 与 pragma；有 `<style>` 时才有样式成员）：

```csharp
// <auto-generated/>
namespace MyApp
{
    using global::System;
    using global::System.Collections.Generic;
    using global::System.Linq;
    using global::AwAUI.UI;

    [global::AwAUI.UI.ComponentSource("Counter.awaui")]
    public partial class Counter : global::AwAUI.UI.Component
    {
        protected override global::AwAUI.UI.Node Build()
        {
            var __n0 = new global::AwAUI.UI.Div();
            __n0.Class = "counter";
            __n0.__Scope = __StyleScope;                  // 作用域样式标记
            var __c1 = new global::AwAUI.UI.NodeList();
            var __n2 = new global::AwAUI.UI.Text();
            __n2.__Scope = __StyleScope;
            var __c3 = new global::AwAUI.UI.NodeList();
            __c3.Add("计数：");
            __c3.Add(global::AwAUI.UI.Node.From(count.Value));
            __n2.Children = __c3;                         // 每个子列表新建一次、赋值一次
            __c1.Add(__n2);
            if (count.Value > 0)
            {
                var __n4 = new global::AwAUI.UI.Button();
                __n4.OnClick = global::AwAUI.UI.EventHandlers.Wrap<global::AwAUI.UI.PointerEvent>(Reset);
                __n4.__Scope = __StyleScope;              // 解析为元素类型的大写标签也带作用域
                var __c5 = new global::AwAUI.UI.NodeList();
                __c5.Add("重置");
                __n4.Children = __c5;
                __c1.Add(__n4);
            }
            __n0.Children = __c1;
            return __n0;
        }

        // @code 块原样搬入（带 #line 映射）
        readonly State<int> count = new State<int>(0);
        void Reset() => count.Value = 0;

        // 组件胶水（#nullable disable）：PropsEqual / AdoptProps / CanAdoptProps
        // 样式（#pragma warning disable 0108）：__StyleScope、StyleSheetSource、GlobalStyleSheetSource 常量，
        //   以及 protected override string ScopedStyleSheet => StyleSheetSource;（全局同理）
    }
}
```

要点：

- 生成**命令式的构建代码**而不是嵌套初始化器：控制流可以直接翻译为 C# 语句，且只用 C# 7.3 语法。
- 所有框架类型用 `global::` 全限定，避免与用户命名冲突；局部变量用 `__` 前缀。运行时类型在命名空间 `AwAUI.UI`（`AwAUI.Text` 是文本栈的命名空间，见 [12](12-component-api.md)），所以前缀是 `global::AwAUI.UI.`。
- 组件胶水由共享的 `ComponentGlueEmitter`（`src/Shared/CodeGen/ComponentGlue.cs`）在 partial 类内产出，与 C# 源生成器同一份逻辑，包在 `#nullable disable` 里（避免可空项目中的 CS8600）。Props 来自生成器提供的 Roslyn 类信息（覆盖 `@code`、代码隐藏文件与没有胶水的基类），没有 Roslyn 时（CLI、直接调用编译器）退回对 `@code` 的语法扫描（线性时间）；已手写的胶水方法被排除。**`.awaui` 类的胶水只由标记生成器产出**，C# 胶水生成器跳过在 `*.awaui.cs` 中有声明的类（见 [14](14-awaui-format.md)）。
- 默认 using（Razor 式，放在命名空间内）：`global::System`、`System.Collections.Generic`、`System.Linq`、`AwAUI.UI`，所以 `@code` 可以直接写 `State<T>`、`List<T>`，`<Button>` 解析为 `AwAUI.UI.Button`。
- 内建元素按运行时实际存在的成员映射（`CodeGen/RuntimeElements.cs`，与语言层的 `Analysis/IntrinsicElements` 分开）：`div / span / text / img / input / svg` 及其属性与事件（`svg` 第七批加入）。语言允许但运行时缺少的元素或事件报 **AW4012** 错误并跳过（C# 仍可编译），行为未实现的属性报 AW4012 警告并作为自定义属性透传；组件上没有可写成员时的 `@ref / @attributes` 也报 AW4012。AW4012 由代码生成步骤（`MarkupCompiler.Compile`）报告，不在 `Analyze` 中，所以解析与格式化不依赖运行时。`button` 没有加为小写内建元素；解析为 `ElementNode` 子类的大写标签（如 `AwAUI.UI.Button`）按元素类型处理：HTML 式的小写属性（class、aria-\*、data-\*、role、tabindex）照内建元素映射，并带组件的作用域。
- 属性值：类型为字符串的已知属性若只有单一表达式，直接赋值（由 C# 检查类型，null 即不设置），这与上表「字符串类型做拼接」略有不同 —— `id="@intValue"` 会在该属性处报 CS0029；字面量与表达式混合时拼接；`Attributes` 中总是字符串；HTML 式布尔透传属性为 `""` / 不设置，表达式写成 `(expr) ? "" : null`；`role` 字面量映射到 `Role` 枚举（ARIA 小写名加别名 img、none、gridcell），未知值报 AW4004。`style` 不做编译期解析（编译器不能依赖 `AwAUI.Css`），按文本交给运行时。
- **HTML 注释**（第八批）：`<!-- -->` 与 `@* *@` 等价，可以出现在顶层、元素内容中、控制流块里和代码块的语句位置；`<!-->`、`<!--->` 是空注释，`--!>` 也结束注释，注释内的 `--` 无妨，扫描对输入线性。不配对时报 **AW4006**；出现在开始标签内（属性之间）时报 **AW4001** 并跳过；属性值与 C# 字符串字面量里的 `<!--` 照原样保留。格式化器原样保留 HTML 注释（语法树 `CommentSyntax.IsHtml`）；内联 svg 中的注释与 `@* *@` 一样在序列化时丢弃。注意：`@if {}` 与 `else` 之间的注释会切断 else 分支（与那里的任何非空白内容相同）。
- **`draggable`**（第八批）：语言层是取值 `true / false` 的枚举属性（此前误为布尔属性，`"false"` 也生成 `""`），字面量原样输出；单一表达式生成 `Convert.ToString((object)(expr), InvariantCulture).ToLowerInvariant()`，bool 与字符串都可用、只求值一次（HTML 的枚举关键字不区分大小写）。运行时仍未实现拖放，按 AW4012 警告透传。`spellcheck` 同样改为枚举属性（true / false / 空串，空串即 true，`spellcheck="false"` 与不写不同；整合时修正），运行时仍不支持它。
- **`img` 的尺寸**（第七批）：`width` / `height` 设置 `Image.Width / Height`，属于新的数值属性种类（内部的 `RuntimeAttrKind.Number`）：字面量生成 float 常量（钳制到 float 范围），表达式由 C# 检查类型。编译器仍接受负数字面量，运行时把负数、NaN、无穷的提示当作没有给出。
- **内联 `svg`**（第七批）：`svg` 映射到 `global::AwAUI.UI.Svg`。根元素上的宿主属性（`RuntimeElements.IsSvgHostAttribute`：通用属性 `id class style role tabindex title lang dir hidden draggable` 与 `aria-*`、`data-*`）与 `@` 指令落在元素节点上 —— 所以 `class` 与 `style` 是宿主元素的 CSS（例如图标的 `color`），不是文档内部的样式；其余根属性与整个子树在编译期序列化为 SVG 文本。每个内联 svg 生成一个 `private static SvgDocument __svgN` 字段，第一次构建时 `__svgN ?? (__svgN = SvgDocument.Parse("...", global::AwAUI.UI.ImagingSvgImageResolver.DefaultOptions))`，之后共享（第八批起带解析器，`<image>` 的 data: 位图可以显示；编译器本身不引用任何程序集，生成代码只经 UI 访问 Imaging，符合依赖规则）；字段放在 `#nullable disable` 下，可空项目加警告即错误也能编译。解析异常（只可能超出 `SvgParseOptions` 的上限）在 `Build` 时抛出，由错误边界处理。序列化保留空白文本、丢弃注释，属性值转义 `& < > "` 与制表符 / 换行（字符引用）；嵌套受标记解析器的 `MarkupParser.MaxNesting`（200）约束。
  - 内联 svg 中的动态内容报 **AW4012** 错误且不设置文档：表达式、控制流、组件、子元素上的指令、含表达式的属性值；动态文档请改用 `<Svg Document="@doc" />`（组件位于 `AwAUI.*` 命名空间时也解析为 `AwAUI.UI.Svg` 类型，有集成测试）。
  - 校验器接受 svg 内任意小写元素与未知属性，但仍对已知属性做类型检查；序列化出的 SVG 在编译期**不**校验（编译器不引用 `AwAUI.Svg`）。
  - 第八批：svg 子树里允许 `<style>`，它属于 SVG 文档（语法树 `StyleBlockSyntax.IsSvg`，原样序列化，由 `AwAUI.Svg` 处理），不能带 `global` 或指令属性（AW4010），不参与作用域样式；元素名与属性名按 HTML 处理 SVG 外来内容的规则修正大小写（HTML 标准的「adjust SVG attributes」表与 SVG 标签名表，先转 ASCII 小写再查表：`viewbox` / `VIEWBOX` → `viewBox`、`lineargradient` → `linearGradient`、`Fill` → `fill`），根元素上的宿主属性不受影响。
  - 不支持：`<style><![CDATA[…]]></style>` 被序列化为转义文本，CSS 解析器会看到 `<![CDATA[`（HTML 在外来内容中把它当 CDATA）。
- `@bind`：目标是 `State<T>` 时生成 `Binding.Get / Set`（由 Roslyn 推测绑定判断；没有 Roslyn 时只认 `@code` 中 `State<T>` 成员的简单名），否则生成 `x = v`；`input` 上的目标必须是字符串（不像 Blazor 那样转换）；支持 `@bind:event` 与组件的 `@bind-X / XChanged`。组件的 `@onX` 只有在属性类型是 `Action<AwAUI.UI.*Event>` 时才用 `EventHandlers.Wrap<TEvent>` 包装，否则直接赋值。
- 组件的子节点用新建的 `NodeList` 赋给 `Children`（组件的 `Children` 可能为 null），缺少 `Children` 成员时经 `#line` 在标签处报错。模板的 `Context` 必须是合法的 C# 标识符，否则报 AW4004 并使用 `context`。`@foreach` 内的子节点必须有 `@key`（AW4008），对应的纯 C# 写法也应设置 `Key`。
- 泛型组件目前只支持显式的 `<Grid<int>>` 标签写法；`<Grid T="int">` 与类型实参推断、嵌套类型标签（`<Outer.Inner>`）尚未实现。
- **`#line`**：每个源片段之前有 `#line`，前缀单独占一行隐藏行，代码用空格补齐到源列（补齐上限 240 列，保持线性）；其余全部在 `#line hidden` 下。文件名按原样写入，不做转义（C# 不处理 `#line` 文件名里的转义）；路径含 `"` 或换行类字符时不输出 `#line`，诊断落在生成文件上。`#line` 用 `.awaui` 的完整路径，`[ComponentSource]` 用项目相对路径，以保证确定性构建。`#line hidden` 区域（胶水、样式常量）里的诊断会报出不准确的 `.awaui` 行号（Razor 相同）。
- `#nullable restore` 只在项目语言版本 ≥ 8 时输出，`@code` 因此遵循项目自己的可空设置；生成代码本身可按 C# 7.3 编译（有测试以 7.3 解析并完整编译运行）。
- **样式**：`<style>` 内容不进入 C# 逻辑，由 `ScopedCss.Rewrite` 改写后生成常量 `__StyleScope`（`EditorBrowsable(Never)`）、`StyleSheetSource`、`GlobalStyleSheetSource`，以及 `protected override string ScopedStyleSheet => StyleSheetSource;` / `GlobalStyleSheet => GlobalStyleSheetSource;`（返回类型写 `string` 而不是 `string?`，以保持 C# 7.3 兼容，这是合法的可空协变重写）。这些成员包在 `#pragma warning disable 0108` 中：`@inherits` 链上的派生组件有意隐藏基类的同名常量，而编译器无法总是判断基类是否声明了它们（用 `new` 在无可隐藏时会报 CS0109）。运行时如何注册见 [12](12-component-api.md)「作用域样式」。
- 文件名不是合法类名（AW4011）时只报告诊断、不产生源码。
- 规划中的**源映射表**（节点构建点 → 文件、行、列，Debug 构建下嵌入程序集，供 DevTools 跳转）尚未生成。

## 诊断

| 代码 | 含义 |
|---|---|
| AW4001 | 标签未闭合 / 结束标签不匹配；开始标签内的 `<!-- -->`（第八批） |
| AW4002 | 未知的内建元素（提示最接近的名称，或提示组件应大写开头） |
| AW4003 | 内建元素上的未知属性 |
| AW4004 | 属性值无法转换为目标类型 |
| AW4005 | 重复的属性 |
| AW4006 | 表达式或代码块定界符不配对；`<!--` 缺少配对的 `-->`（第八批） |
| AW4007 | 未知指令或指令位置错误 |
| AW4008 | 循环内的元素或组件缺少 `@key`（警告） |
| AW4009 | `@bind` 目标不支持或与显式的值/事件属性冲突 |
| AW4010 | 多个 `<style>` 的 `global` 设置冲突等样式块问题 |
| AW4011 | 文件名不是合法的 C# 类名 |
| AW4012 | 运行时尚未支持的内建元素 / 事件（错误，跳过生成）或属性行为（警告，透传为自定义属性） |

解析器具备错误恢复：遇到错误后跳到下一个可同步的位置（标签边界、`}`）继续，尽量一次报出全部问题。

## 实现中确定的细节

- 隐式表达式之后紧跟的 `@` 总是开始新的表达式（`@a@b` 是两个表达式），「邮箱」判断只看文本中的字母数字。
- `case` 标签按括号平衡扫描到顶层的 `:`（排除 `::`、三元运算符与 `??`），出错时在下一个 `case`/`default` 处恢复，因此属性模式、关系模式、元组/列表模式都可用。
- 嵌套深度按权重计：元素、片段、`@:` 行计 1，代码体计 3，上限 200；超出报诊断而不是栈溢出。
- 不配对的定界符通过一次性的括号匹配缓存处理，整体分析为线性时间。

## 测试

- **快照测试**：每个语法特性一组 `.awaui` 输入与期望的 C# 输出。
- **往返测试**：`awaui format` 的输出重新解析后语法树等价。
- **诊断测试**：每条诊断的触发用例与位置断言。
- **模糊测试**：随机输入不得导致编译器崩溃或死循环。
- **端到端**：示例应用的 `.awaui` 版本与纯 C# 版本生成的元素树一致。
