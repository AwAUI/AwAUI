# 19 文本栈 API（接口级）

[03](03-renderer.md) 定义了文本栈的分层与分期，本文定义对外 API 与整形器的内部接口。全部类型在 `AwAUI.Graphics`（抽象）与 `AwAUI.Text`（实现）中。

> 实现现状（M1 第四批）：
> - `FontFace` 及其值类型（`FontDescriptor`、`FontMetrics`、`FontVariation`、`GlyphImage` 等）与 `GlyphRun` 已下沉到 **`AwAUI.Graphics`，命名空间 `AwAUI.Graphics.Text`**，渲染后端据此读取字形轮廓；`FontManager`、`TextShaper`、`ShapedRun`、段落布局留在 `AwAUI.Text`。在 `AwAUI.Graphics.*` 命名空间内，简单名 `Text` 解析为这个命名空间。
> - Graphics 不引用 Unicode，所以码点一律用 `int`（0..0x10FFFF，超出范围返回 false）；`AwAUI.Text` 自己的 `CodePoint`、`Script` 已删除，改用 `AwAUI.Unicode` 的同名类型。Text 的依赖为 Foundation、Graphics、Unicode。
> - 实现另外增加了 `GlyphFlags.Hidden`（默认可忽略字符：零宽、不绘制，渲染器必须跳过；`ToGlyphRun` 会丢弃它们）。
> - 段落布局 P1 已实现（`src/AwAUI.Text/Layout`），API 与下文一致处不再赘述，差异在各节标注。尚未实现：制表位（tab 画成一个空格）、连字符断词、`text-indent`、泰/老/高棉/缅甸文词典断词、长度会变化的大小写映射（ß→SS 等）。

## 约定

- **文本偏移一律是 UTF-16 码元索引**：与 C# 字符串一致，也与多数平台的输入法接口一致。API 保证返回的位置落在字素簇边界上。
- **坐标**为逻辑像素，原点在布局盒左上角。
- `FontFace`、`ShapedRun`、已完成布局的 `TextLayout` 都是不可变对象，可跨线程读取。

## 字体

```csharp
public abstract class FontManager
{
    // 按 CSS 字体匹配算法选择字型：族名列表 → 字宽 → 字形风格 → 字重
    public abstract FontFace? Match(in FontQuery query);

    // 回退：为某个码点序列在指定语言环境下找到能显示它的字型
    public abstract FontFace? Fallback(int codePoint, in FontQuery baseQuery, string? locale);

    // 注册应用自带或 @font-face 声明的字体（可延迟加载，支持按 unicode-range 分片）
    public abstract FontRegistration Register(FontSource source, in FontDescriptor? overrideDescriptor = null);

    public abstract IReadOnlyList<string> FamilyNames { get; }
}

// AwAUI.Graphics.Text（程序集 AwAUI.Graphics）
public abstract class FontFace
{
    public abstract FontDescriptor Descriptor { get; }          // 族名、字重、字宽、风格
    public abstract FontMetrics Metrics { get; }                // 设计单位下的上升、下降、行距、x 高度等
    public abstract int GlyphCount { get; }
    public abstract OutlineFormat OutlineFormat { get; }
    public abstract ReadOnlyMemory<byte> GetTable(uint tag);    // 原始表数据（供任何后端创建自己的字体对象）
    public abstract bool TryGetGlyph(int codePoint, out ushort glyph);
    public abstract int GetAdvance(ushort glyph);
    public virtual int GetKerning(ushort left, ushort right);
    public virtual bool TryGetGlyphBounds(ushort glyph, out Rect bounds);
    public bool GetOutline(ushort glyph, in FontVariation variation, PathBuilder sink);
    public abstract bool GetOutline(ushort glyph, in FontVariation variation, PathBuilder sink, in Matrix3x2 transform);
    public abstract GlyphImage? GetColorGlyph(ushort glyph, float size);   // 位图或分层矢量的彩色字形
    public abstract IReadOnlyList<VariationAxis> Axes { get; }
}

// AwAUI.Graphics.Text：不可变，可跨线程读取；DrawingContext.DrawGlyphRun 的参数
public sealed class GlyphRun
{
    public GlyphRun(FontFace face, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Point> positions,
                    GlyphRunFlags flags = GlyphRunFlags.None, FontVariation variation = default);
    public FontFace Face { get; }
    public float Size { get; }
    public ReadOnlySpan<ushort> Glyphs { get; }      // 构造时复制
    public ReadOnlySpan<Point> Positions { get; }    // 相对绘制原点
    public GlyphRunFlags Flags { get; }              // SyntheticBold / SyntheticOblique
    public FontVariation Variation { get; }          // 按值比较，参与字形缓存键
    public Rect Bounds { get; }                      // 构造时由 'head' 包围盒 + 合成样式余量算出的保守估计
    public const float SyntheticObliqueSkew = 0.25f; // 约 14°，x' = x − y·0.25
    public static float GetSyntheticBoldExtent(float size);   // 同 Skia：9px 时 1/24 em，36px 及以上 1/32 em，中间线性
}
```

`GlyphRun.Bounds` 依赖字体声明的 'head' 包围盒；墨迹超出声明框的字体会被低估，缓存路径的像素对齐还可能再超出至多 0.5 设备像素（见 [03](03-renderer.md)）。

- 系统字体由平台层的 `SystemFontProvider` 枚举（返回文件路径或数据），`FontManager` 按需解析，只读取用到的表。
- 回退链：元素指定的族名列表 → 按文字与语言环境选择的系统默认字体 → 全量扫描。结果按（码点区块、语言环境）缓存。
- Web 平台没有系统字体：回退链完全由应用注册的字体构成，缺字时触发 `FontManager.MissingGlyphs` 事件供应用按需下载。

## 整形

```csharp
public abstract class TextShaper
{
    public abstract ShapedRun Shape(in ShapeRequest request);
}

public readonly ref struct ShapeRequest
{
    public ReadOnlySpan<char> Text { get; }          // 含上下文；实际整形范围由 Range 指定
    public TextRange Range { get; }
    public FontFace Face { get; }
    public float Size { get; }
    public Script Script { get; init; }              // AwAUI.Unicode.Script
    public string? Language { get; init; }
    public bool RightToLeft { get; init; }
    public ReadOnlySpan<FontFeature> Features { get; init; }
    public FontVariation Variation { get; init; }
}

public sealed class ShapedRun
{
    public ReadOnlySpan<ushort> Glyphs { get; }
    public ReadOnlySpan<int> Clusters { get; }        // 每个字形对应的文本偏移
    public ReadOnlySpan<Vector2> Advances { get; }
    public ReadOnlySpan<Vector2> Offsets { get; }
    public ReadOnlySpan<GlyphFlags> Flags { get; }    // 可否在此处断开而不需重新整形、Hidden 等

    // 累加 advance 与 offset 得到绘制位置，丢弃 Hidden 字形；
    // Size 不是有限非负数时返回空 run（DrawingContext 不录制），不抛异常
    public GlyphRun ToGlyphRun(GlyphRunFlags flags = GlyphRunFlags.None, FontVariation variation = default);
}
```

### 整形器插件

OpenType 布局核心（GSUB / GPOS / GDEF 的查找执行）是通用的；每种文字的特殊处理由一个 `ScriptShaper` 提供，接口对应整形流程中的固定钩子：

```csharp
public abstract class ScriptShaper
{
    public abstract bool Handles(Script script);

    public virtual void CollectFeatures(FeaturePlanner plan) { }          // 本文字需要启用的特性及顺序
    public virtual void Preprocess(GlyphBuffer buffer, ShapeContext ctx) { }   // 分解、音节划分等
    public virtual void SetupMasks(GlyphBuffer buffer, ShapeContext ctx) { }   // 为每个字符指定适用的特性
    public virtual void Reorder(GlyphBuffer buffer, ShapeContext ctx) { }      // 字形重排
    public virtual void Postprocess(GlyphBuffer buffer, ShapeContext ctx) { }  // 回退定位、零宽标记等
    public virtual NormalizationMode Normalization => NormalizationMode.Auto;
}
```

整形器注册在 `TextEngine` 上，可按需裁剪（未注册的文字落到通用整形引擎或默认整形器）。此接口属于 P2，目前只有 P1 的 `BasicTextShaper`。

## 段落布局

```csharp
// AwAUI.Text：持有字体管理器、整形器与全局词级整形缓存
public abstract class TextEngine
{
    protected TextEngine(int shapeCacheEntries = 16384);
    public abstract FontManager Fonts { get; }
    public abstract TextShaper Shaper { get; }
    public virtual IReadOnlyList<string> DefaultFontFamilies { get; }   // 默认 "sans-serif"
    public ParagraphBuilder CreateParagraphBuilder(ParagraphStyle style);
    public TextLayout CreateLayout(string text, ParagraphStyle style, float maxWidth = float.PositiveInfinity);
    public ShapeCacheStatistics ShapeCacheStatistics { get; }
    public void ClearCaches();
}
// DefaultTextEngine(FontManager fonts, TextShaper? shaper = null, ...)：默认整形器为 BasicTextShaper

public sealed class ParagraphBuilder
{
    public ParagraphBuilder(ParagraphStyle style, TextEngine engine);
    public void PushStyle(TextStyle style);          // 字体、字号、颜色、装饰、字距、特性 ...
    public void Pop();
    public void AddText(string text);                // 另有 ReadOnlySpan<char> 重载
    public void AddPlaceholder(in PlaceholderStyle p);   // 行内盒：图片、行内组件；以 U+FFFC 占位，自成一个字素簇
    public TextLayout Build();
}

public sealed class TextLayout
{
    public string Text { get; }
    public ParagraphStyle Style { get; }
    public bool IsRightToLeft { get; }
    public int PlaceholderCount { get; }

    public void Layout(float maxWidth);              // 可用不同宽度反复调用；整形结果复用，只重新断行；同宽度重复调用为空操作
    public float MaxWidth { get; }                   // NaN 或 > MaxFiniteWidth(1e9) 一律视为无界，报告 +∞

    // 度量
    public float Width { get; }                      // 实际占用宽度
    public float Height { get; }
    public float MinIntrinsicWidth { get; }          // 最长的不可断开片段
    public float MaxIntrinsicWidth { get; }          // 不换行时的宽度
    public float FirstBaseline { get; }
    public float LastBaseline { get; }
    public int LineCount { get; }
    public bool DidExceedMaxLines { get; }
    public LineMetrics GetLine(int index);

    // 绘制
    public void Paint(DrawingContext dc, Point origin);

    // 命中与几何
    public TextPosition HitTest(Point point);                       // 偏移 + 亲和性（行尾 / 行首）
    public TextPosition HitTestLine(int line, float x);             // 上下移动时保持列位置
    public Rect GetCaretRect(TextPosition position);                 // 零宽、与行等高
    public void GetSelectionRects(TextRange range, IList<Rect> result);   // 双向文本下可能是多个不相邻的矩形
    public Rect GetPlaceholderRect(int index);

    // 边界与导航
    public TextRange GetGraphemeAt(int offset);
    public TextRange GetWordAt(int offset);
    public TextRange GetLineAt(int offset);
    public TextPosition Move(TextPosition from, CaretMove move);    // 见下
}
```

- `TextStyle` 与 `ParagraphStyle` 是带非零默认值的不可变 `sealed class`（`init` 属性；字号 14、黑色、`FontStretch.Normal`），所以构造与 `PushStyle` 不用 `in`。`ParagraphStyle.TextStyle` 是默认样式，同时充当 strut（CSS 的根行内盒）。
- `TextPosition` = 偏移 + `TextAffinity`（Downstream / Upstream）。Upstream 只在软换行的行尾、或双向文本中两种亲和性显示位置不同时保留。
- `CaretMove`：Left / Right（视觉）、Backward / Forward（逻辑字素）、Up / Down、WordStart / WordEnd、LineStart / LineEnd、TextStart / TextEnd。字素导航使用布局自身的字素单元，与命中测试一致。
- 以硬换行结尾或空文本会得到一个按 strut 度量的空末行，供光标停靠（textarea 行为）。
- 数值上限：`TextStyle.MaxFontSize = 100000`；行高解析后截断到 1e6 逻辑像素；虚线/点线/波浪装饰每段落最多 65 536 段，超出后画成实线。
- `GetWordAt` 与 WordStart/WordEnd 每次调用从段落或文本开头线性扫描，交互场景够用，超大单段落未优化。
- 完成 `Layout()` 后的 `TextLayout` 才可跨线程读取（`Paint` 数据与固有宽度惰性计算，并发首读是无害竞争）；之前调用任何 getter 都会触发布局。

断行的严格度映射到 `AwAUI.Unicode.LineBreakStrictness`（Strict = CSS `strict` 与 UAX #14 默认；Normal = CSS `normal`/`loose`，CJ 类按 ID 处理），由 `TextSegmenter.EnumerateLineBreaks` 使用。泰文、老挝文、高棉文、缅甸文的词典断词尚未实现。

`ParagraphStyle`：默认文本样式（strut）、对齐（Start/End/Left/Right/Center/Justify）、方向、最大行数、省略号文本、行高（Normal / 倍数 / 像素）、断行严格度、`WhiteSpace`、`WordBreak`、`OverflowWrap`。它们与 CSS 文本属性一一对应（[16](16-css-properties.md)）；连字符尚未实现。

### 内部管线

```
Build（一次）
  预处理      text-transform（简单 1:1 大小写映射，改变 UTF-16 长度的映射跳过）
              → white-space 第一阶段（被折叠的字符变成工作副本里的 ZWSP，偏移始终指向原文）
  分段        字素簇 → UAX#14 断点（只保留字素边界上的；软断点在去掉折叠字符的副本上计算再映射回来）
              → word-break / break-spaces 调整 → 双向分析（无 RTL 字符时跳过；每个硬段落各自定方向）
  切 run      按 双向层级 / 文字 / 样式 / 字体 切分；字体按字素簇选择：主字型 → 前一簇的字型 → FontManager.Fallback
  整形        run 在断点处切成词，逐词经全局缓存整形
  单元        每个字素簇一个单元（advance 含字距、字形范围、标志）；再算出断点之间的段宽与悬挂宽
Layout(width)（可反复）
  断行        在段上贪心填行；溢出时按 overflow-wrap 在字素簇间紧急断开（前缀和，线性）
  行内重排    片段级视觉重排（UAX#9 L1/L2）
  对齐        Start/End/Left/Right/Center/Justify（Justify 扩展词间隔或宽 CJK 字符间隙，不作用于末行和硬换行前的行）
  截断        超出行数或宽度时在行的视觉末端插入省略号（无 '…' 时回退字体或 "..."）
```

逐词整形意味着跨断点的字距（kerning）会丢失，词内保留。缓存键为文本 + 字型 + 字号 + 方向 + 文字 + 语言 + 可变轴；超过 96 字符或带字体特性的词不入缓存。文本哈希每进程随机化（.NET 上 Marvin，netstandard2.0 上加种子的 FNV-1a + 末端混合），防止哈希碰撞攻击。

**为布局引擎服务的两点**：

- 固有宽度查询（`MinIntrinsicWidth / MaxIntrinsicWidth`）不触发断行，Flexbox 与 Grid 的尺寸求解会频繁调用。
- `Layout(width)` 在同一对象上重复调用代价很低，布局引擎的多轮测量不会重复整形。

缓存：`TextLayout` 由 `Text` 元素持有，内容与样式不变时跨帧复用；另有全局的词级整形缓存（线程安全 LRU，按条目数与总字符数限界，查找不分配）。

实测（2000 词、12 782 字符、Noto Sans 14px，net10.0，机器有其他负载，±30%）：构建 + 首次布局约 3.3 ms，换宽度重新布局约 0.14 ms；20 万字符不可断单词按 `anywhere` 布局 40–55 ms。

## 可编辑文本

`Input / TextArea` 的编辑模型独立于布局：

```csharp
public sealed class TextEditingState
{
    public string Text { get; }
    public TextRange Selection { get; }              // 锚点 + 焦点，可反向
    public TextRange? Composition { get; }           // 输入法组合中的范围
}

public abstract class TextEditor                     // 实现 ITextInputClient，见 04
{
    public TextEditingState State { get; }
    public void Apply(in TextEdit edit);             // 插入、删除、替换、设置选区；进入撤销栈
    public void Execute(EditCommand command);        // 删除到词首、全选、按行移动 ...（按平台键位映射）
    public bool Undo(); public bool Redo();
}
```

- 光标移动与删除以字素簇为单位；双向文本中左右键按视觉方向移动。
- 大文本（`TextArea`）按段落分别布局并虚拟化：只为可见段落保留 `TextLayout`，其余只记高度。
- 密码框、数字键盘、输入掩码、最大长度通过 `InputFormatter` 链处理每一次编辑。

现状（第七批）：上面的 `TextEditingState / TextEditor` 尚未实现。单行 `Input` 的编辑直接做在 `AwAUI.UI`（`InputElement`、`InputTextClient`、`InputManager.TextEditing.cs`、`TextEditing`），字素簇与词边界不依赖段落布局（编辑命令可能在两帧之间连续到达），没有撤销栈，左右键按逻辑顺序移动；见 [05](05-ui-core.md)「文本输入」。

## 线程

- 字体解析与整形可在任意线程执行（用于后台预热与离屏测量）；缓存内部加锁或按线程分片。
- 布局对象的构建与 `Layout` 调用在 UI 线程；完成后的 `TextLayout` 交给绘制阶段只读使用。
- 字形光栅化与图集管理属于渲染后端，在渲染线程。

## 测试钩子

`TextEngine` 可整体替换为一个**确定性的测试实现**（固定宽度的方块字体、按字符数断行），让布局与控件的单元测试不受真实字体与平台差异影响；真实文本栈有自己的一致性测试（整形数据集、断行与双向的 Unicode 官方测试文件）。

已实现为 `AwAUI.Text.Testing.TestTextEngine` + `BoxFontFace`（Ahem 约定：每字符 1 em 方块，上升 0.8 em、下降 0.2 em，所有通用族名都映射到 "Ahem"）。`BoxFontFace` 可设置 advance 与覆盖哪些码点，用来测试字体回退；整形器也可替换（例如计数调用次数）。没有字体管理器或没有注册任何字体时，布局退回内部的 `NullFontFace`，不会抛异常。
