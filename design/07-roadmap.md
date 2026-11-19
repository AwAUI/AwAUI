# 07 路线图与风险

原则：**尽早端到端打通一条最窄的路径，再横向铺开**。每个里程碑都产出可运行、可测试的东西；DevTools 与黄金图像测试从早期就跟上。

## 里程碑

### M0 地基：能在窗口里画出像素 —— 基本完成

已完成：

- 仓库骨架、共享构建属性、polyfill，库以 `netstandard2.0; net8.0; net10.0` 编译，测试在 .NET 10 / 11 上运行（Windows 上另含 net48）
- `Foundation`、`Graphics`（`DrawingContext` 录制器、DisplayList 二进制格式与读取器、`VectorPath`、画刷、`Bitmap`、后端抽象基类）
- CPU 光栅器：解析式覆盖率、非零/奇偶规则、描边（三种连接与线帽、虚线）、线性/径向/锥形渐变、裁剪蒙版、离屏图层与组透明度、可分离混合模式、盒阴影
- `Imaging`（PNG）、`Platform` 抽象、`Headless`、macOS 窗口（裸 ObjC 互操作 + 软件呈现）
- 黄金图像测试（9 个场景）、光栅精度测试、架构依赖测试、CI 工作流
- 示例 `samples/RenderDemo`

验证结论：

- **macOS 裸 ObjC 互操作在 NativeAOT 下可行**（不确定点 #2 解除）：单文件原生程序 1.8MB，零裁剪警告。
- **软件渲染性能基线**（Apple Silicon，标量内核，1280×880 设备像素）：示例场景约 32ms/帧；纯色铺满 3ms，全屏渐变 5ms，σ=32px 的大阴影 7ms。要达到 60fps 需要 SIMD 内核与合成器层缓存（M2）。
- 曲线展平容差定为 0.05 设备像素：0.2 时小圆面积系统性偏小 1.6%，0.05 时约 0.5%，肉眼不可见。
- 高斯模糊用三次盒式模糊近似，σ ≥ 4 时先降采样；大阴影从 19ms 降到 7ms。

第二轮（并行完成）：

- **SIMD 内核**：标量 / `Vector<T>` / `Vector128` 三档，同一运行时内逐字节一致（`tests/AwAUI.RenderTests/SimdTierTests.cs`，档位开关 `Raster.Kernels.ForcedTier`）。示例场景 19.4 → 6.1 ms，纯色铺满 3.2 → 0.32 ms，全屏渐变 5.4 → 1.7 ms，大阴影 6.6 → 1.5 ms（1280×880 设备像素，Apple Silicon）。光栅器的逐行累加刻意保持标量顺序以保证跨档一致，只跳过全零单元。
- **macOS 帧节拍**：macOS 14+ 用 NSView 的 CADisplayLink，更早版本用 CVDisplayLink；无请求时暂停、窗口最小化或被完全遮挡时不出帧。轻负载下精确 60fps（间隔标准差 0.04ms），空闲 3 秒 CPU 约 0.4ms。
- **Win32 窗口**：DIB section + BitBlt 软件呈现、Per-Monitor V2 DPI、按扫描码映射按键、DwmFlush 节拍。**尚未在 Windows 上运行过**，CI 的 Windows 作业会做第一次真实冒烟测试。
- **WASM 技术验证**（不确定点 #1）：见下。

WASM 结论（.NET 10 解释器 + jiterpreter + SIMD，Node/V8）：示例场景 640×440@1 为 38ms/帧，1280×880@2 为 117ms/帧，约为原生的 19 倍；关闭 jiterpreter 会再慢 3–10 倍。修剪后的包体 Brotli 1.51MB，其中运行时约占 97%，AwAUI 只有 38KB。全屏渐变是最大的单项热点（2x 下 62ms）。判断：**软件后端在 Web 上只适合作为低分辨率或静态内容的兜底，常规路径必须是 WebGL2**；AOT（需要 wasm-tools 工作负载，尚未测）可能把差距缩小一截，值得在装上工作负载后补测。另外，部分 `Vector128` API 在 Mono 解释器上比替代写法慢 8–60 倍（标量乘法、`Narrow`、`*All/*Any`），WASM 上需要专门的写法。

并行完成的 M1 核心工作（第四批）：

各项均经过独立评审并修复了评审发现的问题；下列测试数为评审后的数字，net10.0 与 net11.0 各自全部通过（net48 只做了编译检查，本机无法运行）。性能数字都是在同时跑着其他并行构建的机器上测得的，只能当上界或量级参考，尚未在空闲机器上用 BenchmarkDotNet 复测。

- **CSS 值与级联**（`AwAUI.Css`，879 项测试）：
  - `Properties.json` 属性清单（70 个第 1 期长属性、19 个简写，另列计划与不支持的属性）+ `CssPropertyGen` 生成器，生成文件入库并由测试检查是否最新。
  - 强类型值（全部长度单位、calc/min/max/clamp、颜色、渐变、阴影、字体值）；不可变的分组 `ComputedStyle`（组按引用共享、`Diff` 返回受影响的阶段）。
  - `StyleEngine`：CSS Cascade 5 的来源与重要性层级（含 DevTools 来源、预留的动画/过渡槽）、跨表 `@layer`、继承与 CSS 全局关键字、`var()` / `env()` 替换（环检测与防展开上限）、`::before / ::after`、供 DevTools 用的 `StyleMatchRecord`。仍只依赖 Foundation。
  - 实测 2000 元素 × 500 规则：每次全树计算 13–19 ms（约 6.6 µs/元素，约 1.36 KB/元素分配），编译样式表约 23–25 ms。
  - 评审修复：点分层名的二次方内存（40 KB 样式表可分配约 770 MB）、不匹配 `@media` 内的层参与排序、DevTools 记录的特异性取错、`EnvironmentChanged()` 漏报，以及若干规范细节。
- **布局引擎**（新程序集 `AwAUI.Layout`，只依赖 Foundation，169 项测试）：
  - 自有输入 `LayoutStyle` + `LayoutNode` + 叶子 `LayoutMeasure`；Flexbox L1 §9 完整算法、简化 Block、relative/absolute/fixed、固有尺寸、`display: none / contents`、aspect-ratio、像素对齐。
  - 每节点 6 槽缓存，缓存键规范化 + 只求宽度的查询，保证增量重排只重算真正变化的部分。
  - 实测 10 101 节点：全量重排约 10 ms（约 13 字节分配），干净重排 ≈0，一个叶子变脏约 0.15 ms。
  - 评审修复：嵌套 flex 容器的指数级耗时（可由 CSS 触发）、超大有限值溢出为 Infinity/NaN、display:none/contents 深链导致的栈溢出崩溃，以及 4 处与规范不符的尺寸计算。
- **字形绘制**（Graphics / Render / Text；Graphics 31 项、RenderTests 77 项测试）：
  - `FontFace` / `GlyphRun` 下沉到 `AwAUI.Graphics.Text`；`DrawGlyphRun`（操作码 36）进入 DisplayList。
  - 软件后端：A8 字形掩码 LRU 缓存（每渲染目标 8192 项 / 8 MB）、合成粗体与斜体、复杂变换下回退为路径填充；三档 SIMD 逐字节一致；新增 7 个字形黄金图像。
  - Text 改用 `AwAUI.Unicode`（删除自带的 `CodePoint` / `Script`），新增 `ShapedRun.ToGlyphRun`。
  - 实测约 2000 个 14px 字形的 1280×880 帧：优化构建冷帧 4.5 ms、热帧 3.4 ms（路径填充 14.4 ms）；2x 下冷 8.7 / 热 7.1 ms。
  - 评审修复：合成粗体粗细随设备像素比变化、`GlyphRun.Bounds` 惰性计算的并发撕裂读、非法字号导致绘制抛异常。
- **段落布局 P1**（`AwAUI.Text/Layout`，Text 共 202 项测试）：
  - `TextEngine`（全局词级整形缓存）、`ParagraphBuilder`、`TextLayout`；white-space 第一阶段、字素簇、UAX #14 断点、UAX #9 双向、按字素簇的字体回退、贪心断行、片段级视觉重排、六种对齐、省略号与最大行数、半行距行高、占位符。
  - 装饰线、命中测试、光标与选区几何、光标移动；确定性测试引擎 `TestTextEngine` + `BoxFontFace`；3 个段落黄金图像。
  - 实测 2000 词段落：构建 + 首次布局约 3.3 ms，换宽度重新布局约 0.14 ms。
  - 评审修复：折叠空白产生多余断点、整形缓存可被哈希碰撞攻击（改为每进程随机化哈希）、占位符被前置字符并入字素簇、行高与超大宽度导致坐标溢出、装饰线段数无上限。
- **组件运行时与源生成器**（新程序集 `AwAUI.UI`，只依赖 Foundation、Platform，96 项测试；`AwAUI.Generators`）：
  - Node 层与内建元素 `Div / Span / Text / Image / Button / Input`（简易）；`State / Computed / Reactive`（推拉结合、无毛刺）；`Component` 生命周期、`UseEffect`、上下文、`ErrorBoundary`。
  - 协调器（类型 + Key 匹配、最长递增子序列最少移动）；保留的 `Element` 树（成员名与 `StyleElement` 对齐、脏标记）；`UIRoot` 帧驱动。
  - DOM 式事件分发、指针捕获与悬停、焦点与 Tab 顺序、快捷键。
  - Roslyn 增量生成器产出 Props 胶水，发射逻辑放在 `src/Shared/CodeGen` 供 `.awaui` 编译器共用；新增诊断 AW1006–AW1009。运行时类型的命名空间定为 `AwAUI.UI`（见 [12](12-component-api.md)）。
  - 实测 10 000 个带 Key 子组件：挂载约 21 ms，整体反转约 4–6.5 ms。
  - 评审修复：深树导致进程栈溢出、`file` 局部组件让生成器崩溃（全部组件丢失胶水）、重新挂载的实例收不到 `OnMount`、后备界面内的错误被吞掉、只手写部分胶水时 Props 不再更新、事件分发与焦点的若干 DOM 不一致。

并行完成的 M1 前置工作（第三批）：

- **DevTools 协议核心**（`AwAUI.Diagnostics`）：探针总线、对象注册表、自写 UTF-8 JSON 读写器、JSON-RPC 2.0、Agent 与 App/Console 两个域、进程内与自写 WebSocket 传输。284 项测试，含 .NET 自带 WebSocket 客户端的端到端测试，以及在测试进程中加载 netstandard2.0 产物的测试。
- **Unicode**（`AwAUI.Unicode`）：Unicode 18.0 属性表（150KB 静态数据）、字素/单词/句子分段、断行、双向算法；官方一致性测试文件全部通过，零偏差。
- **Linux X11 窗口**：XPutImage 软件呈现（可选 MIT-SHM）、XIM 文本输入、Xft.dpi 缩放。结构体布局已用 Homebrew 的真实 X11 头文件编译比对；**尚未连接过真实 X 服务器**，CI 的 Linux 作业会在 Xvfb 上做第一次冒烟测试。

并行完成的 M1 前置工作（第二批）：

- **CSS 第一层**（`AwAUI.Css`）：符合 CSS Syntax 3 的分词与解析（含嵌套、错误恢复、源位置）、媒体查询、选择器解析与特异性、自右向左匹配与规则索引。451 项测试；约 6–8 MB/s 的完整解析速度。级联与属性值计算尚未开始。
- **字体层**（`AwAUI.Text`）：sfnt/TTC、head/hhea/maxp/hmtx/cmap/glyf/name/OS/2/post/kern，字形轮廓、CSS 式字体匹配、P1 基础整形。测试字体为 OFL 授权的 Noto Sans（随附许可证）。CFF 轮廓、GSUB/GPOS 待 P2。
- **`.awaui` 前端**（`AwAUI.Markup.Compiler`）：完整文法的全保真语法树、C# 边界扫描、错误恢复与诊断、格式化往返、线性时间保证。338 项测试（含模糊与规模测试）。代码生成只是快照级占位，等组件运行时落地后接上。
- **JPEG 解码**（`AwAUI.Imaging`）：baseline 与 progressive、任意采样、CMYK/YCCK、EXIF 方向；与 libjpeg 输出逐像素比对。1280×880 解码 baseline 约 5.7ms（libjpeg-turbo 的 3.5 倍）、progressive 约 9ms（1.8 倍）。顺带加固了 PNG 对损坏输入的处理。

第四批之后的整合（样式 → 布局 → 文本 → 绘制 → 命中测试 → 宿主帧循环、`.awaui` 代码生成、DevTools 检查域）已完成，见下文 M1 一节；各部分仍未解决的问题统一列在文末「后续事项」。

M0 剩余：

- Win32、X11 在真实系统上的验证（CI：Windows 托管运行器、Linux + Xvfb）
- 装上 wasm-tools 工作负载后补测 WASM AOT
- 针对 Mono/WASM 的 SIMD 写法调整

### M1 最小声明式 UI：计数器跑起来 —— 已验收（2026-11）

- CSS 引擎第一步：解析器、选择器、级联、自定义属性、常用视觉属性
- 布局：Flexbox + 简化 Block + 绝对定位
- 元素树、组件模型、State、协调器、源生成器（先以纯 C# API 打通）
- `.awaui` 编译器第一版：标记、`@code`、`<style>`、控制流、事件、`#line` 映射；语法高亮
- 事件（Pointer、键盘、焦点）、帧调度与脏标记
- 文本 P1：TrueType 解析、基础整形、断行、字体回退、字形缓存
- PNG/JPEG 解码；内建元素 `Div / Text / Image`；`Button`
- DevTools 第 1 步：元素树 + 匹配的 CSS 规则 + 高亮（内嵌面板）
- **验收**：计数器/表单示例分别用 `.awaui` 与纯 C# 写成且行为一致；F12 可检查元素并实时改 CSS

整合工作（第四批各部分接成端到端路径；每项独立评审并修复，之后由一个整合任务接通，最后终审）：

- **UI 管线**（`AwAUI.UI` 的 `RenderPipeline`、`UIHost`、`AwApp`）：元素参与 CSS 匹配（每元素一个惰性创建的适配对象，`Element` 的 API 不变；内联样式按文本去重解析，错误报诊断）；内建默认样式表 `DefaultStyleSheet`（UserAgent 来源）；只重算 class / id / 属性 / 伪状态 / 内联样式变化的元素，祖先与兄弟位置的选择器按需扩大到子树或兄弟；`ComputedStyle → LayoutStyle` 映射（`calc()` 值相等，只改颜色不重排）；文本测量复用段落对象；绘制背景、线性/径向/重复渐变、边框（分边颜色、圆角、统一的 dashed/dotted）、内外阴影、opacity、overflow 裁剪、visibility、文本、焦点轮廓与 z-index；视口外子树剔除；按绘制顺序命中测试；CSS `cursor` 转发到窗口；只在有变化时请求帧。CSS 新增 outline 属性（见 [16](16-css-properties.md)）。组合根 API 见 [01](01-architecture.md)，绘制与层叠规则见 [05](05-ui-core.md)。
- **`.awaui` 代码生成**：`CSharpEmitter` 生成 `global::AwAUI.UI.*` 代码（形状见 [17](17-awaui-grammar.md)）；新增增量源生成器 `src/Markup/AwAUI.Markup.Generator`（NuGet 包 `AwAUI.Markup`，见 [14](14-awaui-format.md)）与 `tests/AwAUI.Markup.IntegrationTests`；作用域样式由编译器改写选择器、运行时在组件类型首次挂载时自动注册（见 [12](12-component-api.md)）；`.awaui` 类的 Props 胶水只由标记生成器产出，C# 胶水生成器跳过 `*.awaui.cs`；运行时缺的元素与事件报 AW4012。
- **DevTools 检查域**：`AwAUI.Diagnostics` 新增 Tree / Css / Layout / Overlay 四个域，以及 UI 层要实现的抽象类 `InspectableTree / StyleInspector / OverlayHost`（Diagnostics 仍只依赖 Foundation）；`AwAUI.UI/DevTools` 实现它们，`UIHost.EnableDevTools` 接上 F12 拾取、覆盖层与可选的 WebSocket（见 [06](06-devtools.md)、[20](20-devtools-protocol.md)）。协调器改为先报移除、再从后往前报移动与插入，使前端的树镜像可以增量维护。
- **示例与验收测试**：`samples/Counter`（纯 C# 版与 `Markup/*.awaui` 版，参数 `--markup`、`--devtools [port]`、`--headless`、`--bench`、`--stress`）与 `tests/AwAUI.Samples.Tests`。

评审修复（摘要）：

- 终审：恶意 CSS 可让渲染器死循环或耗尽内存 —— 接近 1e9 px 布局上限的虚线边框在 `Stroker.Dash` 中 float 累加不再前进（改为 double 弧长，并以每次描边 2^20 段兜底）；1e6 px 的点线边框生成整圈约 400 万段（6.6 s / 828 MB），改为在局部空间按裁剪区剔除后为 17 ms / 0.2 MB；`box-shadow` 的巨大 blur 溢出 int 或分配数百 MB，现 σ 上限 4096 设备像素，工作区超过 4M 像素时以 1/k 分辨率光栅化并模糊（内存与 σ 无关，与全分辨率结果差 ≤ 2/255）。另外：fixed、sticky 改为总是建立层叠上下文；z-index 对 grid 项生效；DevTools 的覆盖记录持有已卸载元素。
- 标记：`#line` 路径按 C# 字符串转义写入（C# 不处理那里的转义，Windows 上所有诊断与断点都指向不存在的文件）；链接进来的同名文件 hint name 冲突导致整个生成器失败；无 Roslyn 信息时 `@code` 成员扫描在恶意输入下为二次方；带 `<style>` 的组件继承另一个带 `<style>` 的组件时报 CS0108（警告即错误）；非法类名的文件仍产生源码；`RootNamespace` 未清洗为合法标识符；组件上的 `@ref / @attributes` 生成不可编译的代码；模板 `Context` 未校验。
- DevTools：事件可能先于启用它的 `enable` 响应送达；排队到 UI 线程的高亮 / 拾取 / 强制伪状态请求可能在会话停用后残留；先报插入的跨父移动破坏会话镜像；移除目标时排队的 `getDocument` 泄漏镜像。

实测（Apple Silicon，机器同时有其他构建，只能当上界；中位数）：

- Counter（720×460 逻辑像素 @2x，20 个元素、45 条绘制命令，优化构建）：冷挂载帧 41 ms（含 JIT；构建 4.8、样式 10.6、布局 18.8、绘制 6.6 ms），首次光栅另需 72–92 ms；点击 +1 一帧 7.4 ms，其中样式 + 布局 + 绘制合计不到 0.07 ms，光栅 7.3 ms（主要是全窗渐变背景与卡片阴影）。
- 1000 行列表（6002 个元素、3000 个段落，800×600 @2x）：冷挂载 138 ms（布局 83 ms）；全部重算样式 66 ms；选中一行 4.3 ms；行间悬停 2.6 ms（视口剔除把命令数从 7001 降到 103，此前约 6 ms）。
- 标记生成器：300 个互相引用的 `.awaui` 组件冷启动 351 ms，无关的 C# 编辑后 69 ms（输出全部不变）。
- DevTools：10 万层深链 + 10 万个子节点的整套操作约 2 s（未优化构建）。macOS 窗口里经 WebSocket `Css.setStyleText` 后的一帧：样式 1.6 ms（20 个元素）、布局 1.2 ms、绘制 0.09 ms、光栅 76 ms（未优化构建，2x 软件光栅）。

**验收结论**（终审对照代码与输出核实，不只看报告）：

| 验收项 | 结论 |
|---|---|
| 计数器/表单分别用 `.awaui` 与纯 C# 写成且行为一致 | **达成**。`tests/AwAUI.Samples.Tests` 在 2x 下无头挂载两个版本，执行相同的 13 步输入（点击、步长按钮、键入 "Ada"、Enter、Tab、Space、悬停、按下/抬起），挂载后与每一步之后要求元素树（标签、id、class、属性、文本、伪状态、布局矩形）与帧缓冲 PNG 字节完全一致；手工比较两版 PNG 也逐字节相同 |
| F12 可检查元素并实时改 CSS | **经 F12 拾取 + 协议达成，内嵌面板未做**。F12 进入拾取（悬停高亮、点击选中且应用收不到这次点击、Esc 取消），示例把选中元素的匹配规则打印到控制台；完整的检查与编辑经 DevTools 协议（进程内或 WebSocket）。测试经进程内传输驱动真实 Agent：getDocument、getMatchedRules、setStyleText 改变下一帧像素、clearOverrides 还原 |
| 真实 macOS 窗口 | 整合任务用 WebSocket 客户端在真实窗口上跑通过一次（App.hello → getDocument → getMatchedRules → setStyleText → getComputedStyle → getBoxModel），**终审没有复测**；无法向真实窗口注入 F12 与点击，也无法截屏，拾取与高亮只由无头测试和 PNG 验证 |

未满足或打折扣的目标项：

- ~~DevTools 第 1 步的内嵌面板未做~~ —— 第七批以独立窗口形态完成（`AwAUI.DevTools`，见下文第七批）；应用内停靠形态仍未做。元素没有源码位置（未生成源映射表）。
- `.awaui` 语法高亮（`tooling/vscode-awaui`）与 `awaui` 命令行未做。
- ~~任何变化都整帧重录、整窗光栅化~~ —— 第六批已有元素级 DisplayList 缓存与损伤矩形，软件后端只光栅化损伤区域。
- ~~`Image` 仍不画位图像素~~ —— 第七批完成（`DrawImage`，见下文第七批）。
- ~~`Input` 仍是桩~~ —— 第七批有了光标、选区、输入法与剪贴板（单行；多行 `TextArea`、撤销 / 重做仍缺）；行内排版只在 `Text` 内，`::before / ::after` 不渲染；grid、`order`、`sticky` 按 [08](08-css.md) 所述回退。

### 第五批（与 M1 并行）

四项各自经独立评审并修复；下列测试数为评审后的数字，net10.0 与 net11.0 全部通过（net48 只做编译检查）。性能数字同样在有并行负载的机器上测得。

- **Metal GPU 后端与 RHI**（113 项测试；只在 Apple M5 / macOS 27.2 上运行过）：
  - RHI `AwAUI.Rhi`、GPU 后端 `AwAUI.Render.Gpu`、Metal 实现 `AwAUI.Rhi.Metal`，示例 `samples/GpuDemo`，测试 `tests/AwAUI.Rhi.Metal.Tests`。前两者以临时项目放在 `src/Rhi/` 下完成，第六批已按 [15](15-repo-structure.md) 合入 `AwAUI.Render` 的 `Rhi/`（命名空间改为 `AwAUI.Render.Rhi`）与 `Gpu/`，并删去 GPU 后端里复制的 CPU 光栅器文件；`AwAUI.Rhi.Metal` 保持独立程序集。实际形态见 [03](03-renderer.md)。
  - 与软件后端对照：已有的 21 个黄金场景与 10 个 GPU 专用场景都在阈值内；已有场景中差值超过 8/255 的像素至多 0.057%、最大差 12/255，唯一例外是 transforms 场景的 4 个像素（至多 54/255，来自软件光栅器逐像素截断绕数造成的描边内角过填，GPU 的结果几何上正确）。路径、字形、裁剪蒙版用同一份 CPU 光栅器生成，文字逐字节一致。
  - 实测（优化构建，RenderDemo 场景 1280×880 设备像素，离屏，中位数）：动画帧软件后端 6.21 ms；GPU 路径 CPU 侧 0.27 ms（录制 + 编码 + 提交，其中编码 0.02 ms），GPU 执行 0.37 ms（空闲机器；有负载时约 1.6 ms，强烈依赖 GPU 频率状态），25 个实例、5 次 draw、1 个 render pass；静态帧 CPU 侧 0.013 ms。GpuDemo 自带的 CAMetalLayer 窗口在 vsync 下 60 fps、每帧 CPU 0.3–0.4 ms。NativeAOT 发布 4.8 MB、零警告。
  - 评审修复：大于 4 MB 的上传使上传缓冲池无界增长；含 NaN 的缓存键永不相等（渐变表与路径掩码缓存无界增长）；缓存键哈希可被碰撞攻击（改为每进程随机种子）；编码中途失败的帧让缓存指向未上传的内容。
  - 模糊测试发现的软件后端问题（光栅器遇 NaN 越界、单个 NaN 色标的渐变抛异常、巨大 blur、虚线段数无界、DisplayList 嵌套深度无界）在 GPU 后端的副本里已修，`AwAUI.Render` 本身的修复在第六批完成（两个后端改为共用一份光栅器）。
- **Web 平台 `AwAUI.Browser`**（只编译 `net10.0-browser`；209 项单元测试 + Node 冒烟 57 项 + 真实无头浏览器冒烟 46 项）：canvas 软件呈现、帧时钟、Pointer Events、键盘、隐藏 textarea 的 IME、剪贴板，细节见 [04](04-platform.md)；示例 `samples/BrowserDemo`（不需要 wasm-tools 工作负载即可发布）。
  - 浏览器冒烟只在 Chromium 153（chrome-headless-shell）与 Edge 157 的无头模式下运行，输入经 CDP 注入；**Firefox、Safari、移动浏览器与真实的系统输入法都没有跑过**，真实的 DPR 变化事件也未验证（只验证了接线）。
  - 实测（1280×880 设备像素 = 640×440 CSS 像素 @2x，解释器 + jiterpreter，空闲机器）：`putImageData` 直接用 WASM 内存上缓存的 `ImageData` 为 0.07 ms（先拷出再 put 为 0.40–0.55 ms）；演示帧内的呈现 0.13–0.25 ms；整帧 Chromium/Edge 约 104 ms、Node 约 118 ms（约 9 fps），其中录制 0.3 ms，其余都是光栅 —— 再次确认 Web 上需要 WebGL2。启动（导航到 Main 返回）100–600 ms；Brotli 包体 1.51 MB（AwAUI.Browser.wasm 20 KB、胶水 JS 5 KB）。
  - 评审修复：Windows 上 AltGr 字符无法输入（浏览器把 AltGr 报告为 Ctrl+Alt，被当成快捷键调用了 preventDefault）；派发队列中一个动作抛异常后其余动作被搁置；页面已有 alpha 上下文时按不透明附着导致颜色错误；同一 canvas 可重复附着；页面隐藏时附着的初始可见性错误。
- **SVG `AwAUI.Svg`**（只依赖 Foundation、Graphics；230 项测试）：自写 XML 子集解析器（不用 System.Xml，不展开 DOCTYPE 实体，长度、深度、元素数、属性数有上限）、完整路径文法（弧转三次曲线）、`svg / g / a / switch / defs / symbol / use` 与全部基本形状、线性/径向渐变（href 继承、spreadMethod；焦点 fx/fy/fr 回退到圆心）、clipPath、全部描边属性、paint-order、表现属性 + `style=""` + 最小的 `<style>`（类型、class、id、后代与子代选择器、!important）、由调用方提供的 currentColor（图标着色）；文本、滤镜、蒙版、图案、标记、图像不支持，列入 `Diagnostics`。解析时一次建好渲染树，`Draw` / `ToDisplayList` 只做遍历；所有开销对输入线性，并受渲染节点、use 深度、路径段、选择器工作量、渲染工作量等预算约束。
  - 实测：图标解析 + 构建 10–35 µs、录制 1–5 µs；1–2 KB 的插画解析 40–94 µs；2 万条路径的 1.5 MB 文档 88 ms。
  - 模糊测试（160 万次属性值变异 + 40 万次结构变异）发现 `AwAUI.Render` 的 3 个问题：`Stroker.Dash` 在远离原点的坐标下死循环、`Rasterizer.DrawLine` 越界、展平器没有总点数预算（5 万条巨大三次曲线分配约 1 GB）。当时只在 Svg 内规避，Render 的修复在第六批完成（`FuzzReproTests` 原样重放这些输入）。
  - 评审修复：clipPath / 渐变引用、CSS 类匹配与声明应用中的若干二次方开销；预算耗尽后 use 扇出仍然无界；objectBoundingBox 下 clipPath 变换的顺序反了；无效的 CSS 声明覆盖了表现属性。
  - 第六批已接入 UI：`Svg` 元素与 `Icon` 组件（见 [05](05-ui-core.md)、[22](22-controls.md)）。焦点 fx/fy/fr 第七批起经 `RadialGradientBrush` 的焦点构造函数实现（此前回退到圆心）；`<image>` 也在第七批支持。
- **图片格式**（`AwAUI.Imaging` 新增 GIF、WebP、BMP、ICO/CUR；Imaging 共 267 项测试）：
  - 与参考解码器逐像素一致、无容差：WebP 37 个文件（静态图与每个动画帧，含有损 VP8）对 libwebp 1.6；GIF 对 giflib 解出的索引按浏览器规则合成；BMP、ICO 对 Pillow；两张 1280×880 WebP 与一张 24 帧 GIF 对参考输出的 SHA-256。动画模型 `AnimatedImage` 与解码上限见 [03](03-renderer.md)。
  - 实测（优化构建，负载 5–6）：1280×880 无损 WebP 8.6–9.6 ms（约为 libwebp 的 2 倍）、有损 12 ms（约 3.2 倍，没有 SIMD）；480×320 的 24 帧 GIF 解出全部帧 1.7–4.5 ms。
  - 模糊测试：默认每次运行约 15 万次变异解码，加大规模约 120 万次，没有出现非预期的异常类型。
  - 评审修复：BMP/ICO 在截断与大小检查之前就分配完整输出（几十字节的文件可分配约 2 GB）；GIF 画布没有像浏览器那样扩大到容纳第一帧。

### 第六批（与 M2 并行）

四项（Render 合并与加固、Metal 窗口呈现、UI 的图片 / SVG / 增量绘制、负载敏感的测试）各自经独立评审并修复，之后由主任务整合。测试数为整合后的数字，见下文「当前测试数」。**验证范围**：只在 Apple M5 / macOS 27.2（arm64）上运行；net48 只编译、未运行；性能数字都在高负载机器上测得（负载 5–280，常有其他 agent 同时构建），只能当上界或量级参考，需要在空闲机器上复测；窗口数字还受其他进程的窗口遮挡影响。

- **Render：GPU 合入、共用光栅器、加固**（细节见 [03](03-renderer.md)、[13](13-displaylist.md)）：
  - 结构：RHI 移入 `src/AwAUI.Render/Rhi`（命名空间 `AwAUI.Render.Rhi`），GPU 后端移入 `src/AwAUI.Render/Gpu`（`AwAUI.Render.Gpu`），`AwAUI.Rhi`、`AwAUI.Render.Gpu` 两个项目删除，`src/Rhi/AwAUI.Rhi.Metal` 改为引用 `AwAUI.Render`。GPU 后端里复制的光栅器文件删除、修复并入原件；字形光栅化、CPU 阴影回退、渐变表及其键、内容键哈希、图层隔离判定都由两个后端共用。`IMetalLayerSurface` 移入 `AwAUI.Graphics`（`Backends/Backends.cs`）。模糊测试生成器移到 `tests/AwAUI.RenderTests/DisplayListFuzzer.cs` 并链接进 Metal 测试，新增软件后端的模糊测试（此前只测 GPU）。
  - 加固：第五批模糊测试发现的全部问题（`DrawLine` 的 NaN、非有限路径点、单个 / NaN / ±∞ 色标、虚线段数、展平点数、圆弧点数、嵌套深度、命令数、图层深度、σ、非有限阴影参数），每项有回归测试；实现者临时撤销了其中四项修复，确认对应测试会失败。虚线超过 2^18 段改画实线并上报（此前静默截断）。大 σ 阴影的工作网格按 k ≥ σ/8 缩小：巨大旋转模糊从 5 s 以上（未优化构建）降到 42 ms，σ = 120 时与全分辨率差 ≤ 2/255。
  - 双点锥形渐变：`RadialGradientBrush` 增加 `FocalPoint / FocalRadius / HasFocus`，软件与 Metal 着色器同参数实现，GPU 与软件最大差 1/255；画刷是资源对象，`FormatVersion` 仍为 1。
  - 图层隔离：不透明度 1 的 SrcOver 图层只在组内读取目标时才离屏。实测（1280×880）逐层判定的开销测不出；一个隔离的全屏图层多 3–6 ms，4 层嵌套 32–38 ms（跳过时约 14.5 ms），因此保留逐层判定。
  - 黄金图像：全部核心场景与 RenderDemo 帧与原始构建逐字节相同（用链接原始 DLL 的探针核对）；新增 `gradients-focal`、`layer-isolation` 两个黄金图像并人工查看。存储的 strokes、gradients、box-shadows 黄金图与渲染器差 1–2/255、glyphs-synthetic 差 1/255，原始构建同样如此，是既有差异，在测试容差 2 之内。
  - 帧时间：RenderDemo 软件后端（优化构建，1280×880，负载 12–50）动画帧中位数 15.26 → 13.54 ms、静态帧 14.02 → 14.70 ms，在噪声内，无回归；评审在负载 88–200 下复测同样看不出差别。GpuDemo `--bench 200`（负载下）：动画帧 GPU CPU 侧 0.50 ms、GPU 执行 0.90 ms，软件 14.1 ms（第五批在空闲机器上 CPU 侧为 0.27 ms）。
  - 评审修复：**每帧工作量上限**（高）—— 2^22 的命令数上限挡不住嵌套扇出的昂贵命令，几百字节的输入可重放 400 万次全屏绘制（2^14 次 640×440 半透明全屏填充在未优化构建中已超过 80 s CPU，GPU 上实例缓冲约 700 MB）；新增软件后端 `RasterContext.MaxWorkPerFrame` = 2^30，GPU 另有实例像素 2^34、实例数 2^20。降级信息按帧去重（每帧最多 1024 种）。新增 `FuzzReproTests`，把第五批报告里的原始复现输入（cx=1e38 的虚线圆、线宽 1e30 的圆描边、5 万条巨大三次曲线、160×120 目标上的 blur 2000 等）与焦点渐变的恶意参数经公开 API 端到端重放。`HardeningTests` 中只用来发现「不结束」的计时断言放宽到 180 s。
- **Metal 窗口呈现**（细节见 [04](04-platform.md)、[03](03-renderer.md)）：
  - `AwAUI.MacOS` 窗口表面同时提供帧缓冲与 `IMetalLayerSurface`，由后端第一次用的能力决定模式；CAMetalLayer 子层、sRGB 标记、节拍直接出帧、尺寸变化同步出帧、拖动缩放随 CA 事务呈现。
  - `MetalRenderer.Create(preferGpu)` 组合根助手；`MetalRenderBackend` 在设备丢失或渲染出错后永久回退软件，当前帧立即用软件重画，窗口切回帧缓冲呈现。
  - 示例：Counter 与 RenderDemo 默认用 GPU，新增 `--gpu`、`--software`、`--self-check`、`--simulate-device-loss N`；RenderDemo 另有 `--exit-after-ms N` 与 `--bench [N] --gpu`，Counter 另有 `--auto-click`，`--bench` 在有 Metal 时自动加 GPU 对比；无头模式仍是软件。自检以软件渲染同一帧为参照，比较实际呈现的可绘制纹理（读回）与窗口服务器合成后的窗口截图：RenderDemo 可绘制纹理最大差 10/255、窗口截图 11/255（GPU）与 13/255（软件）；Counter 两种比较都是 7/255。
  - 实测（优化构建，负载 5–85）：RenderDemo 窗口 1280×880 vsync 下 GPU 58.8 fps、`Render` 调用 0.33–0.35 ms（录制 + 编码 0.28–0.30 ms），软件 54–57 fps、每帧 12.7 ms。Counter 窗口 1440×920 每帧自动点击：GPU 宿主总计 0.26 ms，软件（局部光栅化）1.9–2.2 ms。评审复测的离屏基准 RenderDemo `--bench 300 --gpu`：动画帧软件 6.30 ms、GPU CPU 侧 0.28 ms、GPU 执行 0.37 ms；静态帧 GPU CPU 侧 0.024 ms。
  - NativeAOT：Counter 单文件 8.6 MB、0 个裁剪 / AOT 警告；`--headless`、GPU 窗口加自检、`--software`、模拟设备丢失都正常运行。
  - 评审修复：窗口重新可见时整帧重呈现（不可见期间 `nextDrawable` 超时返回 nil 的帧被静默丢弃，静态界面会一直显示旧内容）；交换链改变尺寸时丢弃「取得后没有呈现」的可绘制对象（否则新尺寸的内容画进旧尺寸的纹理）；每帧一个自动释放池；尺寸通知的重入保护；`TryCreate` 在构造 GPU 后端抛 `GpuException` 时退回软件而不是崩溃。
  - **未验证 / 已知限制**：拖动缩放只用程序化缩放加模拟通知验证，没有人工拖过；设备丢失只做过模拟，「本帧中途丢失」没有直接测试；被其他进程窗口遮挡时 `nextDrawable` 可能阻塞主线程 1 秒（未修）；没有 x86_64 与 macOS 14 以前的测试。
- **UI：图片、SVG、增量绘制**（细节见 [05](05-ui-core.md)、[03](03-renderer.md)「图片」、[12](12-component-api.md)）：
  - `Image`：`ImageSource`（位图、字节、流、文件、资源、URI、SvgDocument，字符串隐式转换）、每个根一份的引用计数缓存、线程池解码、按自然尺寸与宽高比布局、object-fit / object-position（暂为节点属性，CSS 仍是第 2 期）、GIF / WebP 动画（只在可见时播放，≤ 10 ms 的延迟按 100 ms）。新增诊断 AW1010。**仍不画位图像素**（DisplayList 没有 `DrawImage`），通过 `Image` 显示的 SVG 可以画出。
  - `Svg` 元素与 `Icon` 组件；`AwAUI.UI` 因此引用 `AwAUI.Imaging`、`AwAUI.Svg`（决策 D32）。
  - 增量绘制：元素级局部坐标 DisplayList、只重录变化的元素、每帧一个损伤矩形、软件后端局部光栅化；11 步固定场景在 1x / 1.5x / 2x 下与整帧重画逐字节一致（大 blur 盒阴影除外，渲染器问题）。
  - 修复 M1 终审遗留的裁剪偏差：opacity 层叠上下文里的 absolute 元素不再被位于它与包含块之间的 overflow:hidden 祖先裁掉（`ClipEscape`），命中测试同规则。
  - 实测（1000 行列表，2x，800×600，无头 + 软件 + 测试文本引擎，优化构建，负载 30–120，在第 1、3 行之间来回悬停，160 帧中位数）：

    | | 样式 | 布局 | 绘制 | 光栅 | 总计 |
    |---|---|---|---|---|---|
    | 之前（关闭增量，整帧光栅） | 0.04–0.11 ms | 0.01–0.05 ms | 0.25–0.81 ms | 4.3–5.4 ms | 4.6–6.6 ms |
    | 之后（增量 + 局部光栅） | 0.013–0.028 ms | 0.004–0.007 ms | 0.048–0.137 ms | 0.87–1.14 ms | 0.98–1.32 ms |

    之后每帧重录 3 个元素（两行与列表）、复用 20 个、4 条顶层命令、重画 1570×216 设备像素。Counter 示例用真实字体（`--stress`，只有增量模式）：悬停 1.34 ms、选中一行 4.58 ms（组件重建 3.26 ms）、改一行文字 5.03 ms。评审修复之后**没有复测**（负载 70–150），悬停测试仍只看到 3 个重录元素与一行大小的损伤。
  - 评审修复：**持久失效**（高）—— 一帧里没被录制的元素（被剔除、`opacity: 0` 之下、`ClipEscape` 上下文里、超出嵌套上限）会重用变化前的旧缓存，改为沿绘制脏路径打 `RenderBox.DirtySerial`；文本墨迹加入字形溢出估计（随机差分测试修复前 24 个种子中 10 个报「损伤太小」）；`PaintGeometry` 缓存键补全对齐值；动画图片的定时器提前触发时会永久停播；`GlobalDispatcherTests` 的测试隔离。新增随机差分测试 `IncrementalDifferentialTests`（默认 24 个种子；加大到 300 种子 × 60 步与 200 种子各跑过一次，全部通过；`AWAUI_UI_DIFF_SEEDS=n`）。
- **负载敏感的测试**：
  - Markup `ScalingTests.Pathological_inputs_take_linear_time` 改用线程 CPU 时间（macOS / Linux 用 `clock_gettime(CLOCK_THREAD_CPUTIME_ID)`，Windows 用 `GetThreadTimes`）+ n 与 8n 交错多轮取最小值（上限 24；线性 7–13、二次方约 64）+ net10+ 上的分配字节数之比（确定性，线性 2.4–9.2，上限 16）。墙钟在超额订阅下把长测量拉长得远多于短测量，取最小值也无效（负载 45–63 下线性代码的比值升到 24.5–50）。`Thread_cpu_clock_counts_running_time_only` 保证不会悄悄退回墙钟。临时注入的二次方代码在 7 个模式中 6 个被抓到；敏感度下限：在 n=5000 时只占约 25–30% 以下的二次方项可能漏过。
  - Diagnostics 的保活修复了真实缺陷：线程池饥饿、调试断点或系统休眠让保活循环迟醒时，按「距最后收到字节」判定会断开一直有应答的对端。现在超时从 Ping 入队起算、每轮最多记入 max(2×间隔, 50 ms)、判定前检查套接字里未读的字节；`KeepAliveStarvationTests` 阻塞线程池 1 s 复现（原代码 3/3 失败）。
  - 两个项目在 20 个 CPU 烧机进程（10 核，负载 35–102）下连跑 5 轮、两个运行时全部通过。**未验证**：Windows 的 `GetThreadTimes` 路径与 Linux 的时钟 id 都没有在对应系统上运行过，只在 macOS 上验证。
- **整合（主任务）**：
  - 删除只剩 bin/obj 的 `src/Rhi/AwAUI.Rhi`、`src/Rhi/AwAUI.Render.Gpu` 目录。
  - `ArchitectureTests` 允许 `AwAUI.UI → AwAUI.Imaging、AwAUI.Svg`（决策 D32，见 [README](README.md)）。
  - `ThreadCpuClock.cs` 从 `tests/AwAUI.Markup.UnitTests` 移到 `tests/Shared/`（命名空间 `AwAUI.Tests.Shared`），由 Markup 与 Imaging 测试项目链接。`WebPRobustnessTests.MaximumHuffmanGroupCountStaysFast` 改用线程 CPU 时间：负载 280 时它曾以 13–40 s 墙钟时间失败，Debug 下实测约 3 s CPU，上限 10 s。
  - `PixelRect.RoundOut` 两侧都钳制到 ±int.MaxValue/2（原来坐标 ≥ 2^31 的有限矩形在 net8 x64 / netfx 上转成 `int.MinValue`，宽度溢出）；测试 `GeometryTests.PixelRect_round_out_clamps_huge_finite_coordinates_on_both_sides`。
  - `UIHost.PartialRaster` 默认每帧按后端当前能力判断，GPU 回退到软件后重新打开（此前一直关闭，Counter 自动点击在未优化构建下从约 8.7 ms 变成约 71 ms/帧）；显式赋值后固定。`UIHost.BackendName` 反映后端当前的 `Name`。测试 `PartialRasterFallbackTests`。
  - `dotnet build AwAUI.slnx`（Debug，加与不加 `-p:IncludeNetFx=true`）0 警告 0 错误；`dotnet test AwAUI.slnx` 全部通过，每个运行时 3430 项。Counter 无头模式下 C# 版与 `--markup` 版的 PNG 逐字节相同；GPU 窗口自检最大差 7/255。

### 第七批（与 M2、M3 并行）

四项（Render：`DrawImage` 与裁剪无关的光栅等；媒体：CSS 第 1 期媒体属性、标记映射与 UI 接入；输入：平台文本输入与 `Input` 编辑；DevTools 面板）各自经独立评审并修复，之后由主任务整合。测试数为整合后的数字，见下文「当前测试数」。**验证范围**：只在 Apple M5 / macOS 27.2（arm64）上运行；net48 只编译、未运行；**Win32 与 X11 的输入代码只验证了编译与纯逻辑测试**；**macOS 输入法只用探针部分验证**；**UI 图片的 GPU 对照只在 scratchpad 的一次性程序里做过**；性能数字在负载 5–20 的机器上测得，只能当上界。

- **Render：`DrawImage`、与裁剪无关、丢帧、哈希**（细节见 [03](03-renderer.md)、[13](13-displaylist.md)、[02](02-graphics-abstraction.md)）：
  - `DrawImage` 端到端：Graphics 的 `DrawImage`、`ImageSampling { Nearest, Linear }`、操作码 34 的 `DrawImageCmd`（44 字节，`FormatVersion` 仍为 1），`Bitmap.Version / Invalidate() / IsDisposed` 与终结器；软件后端最近 / 双线性采样，大比例缩小时用盒式预滤波的 mip（L = round(log2 ρ)，ρ ≥ √2）；GPU 按（位图，版本，区域，级别）缓存纹理，Metal 着色器新增 `QuadPaint.Image = 5`。GPU 与软件对照：image-sampling / image-downscale 最大差 1/255，image-transforms 7/255。新增 4 个 DrawImage 黄金图（人工查看过），**暂存在 `tests/AwAUI.RenderTests/Golden`** 而不是 `tests/TestFiles/Golden`。
  - SVG：`<image>` 经新的抽象类 `SvgImageResolver`（`SvgParseOptions.ImageResolver`、`MaxImagePixels` 默认 64M）；径向渐变用上了焦点 fx/fy/fr（`Svg11FocalPoint` 默认 false = SVG 2 行为），不带焦点的径向渐变逐字节不变。
  - 软件光栅与裁剪无关（决策 D36）：光栅器坐标系与剔除、渐变列、盒阴影网格三处改为按根目标的绝对坐标；`ClipIndependenceTests`（默认 40 个种子 + 4 个阴影用例，`AWAUI_CLIP_SEEDS=600` 跑过），撤掉任一修复都失败。存储的黄金图只在容差内变化（box-shadows 1x 最大差 1 / 2x 最大差 2，gradients、layer-isolation@2x 最大差 1）。
  - 丢帧契约 `RenderTarget.LastFramePresented`（决策 D37）。
  - `HashCodeCompat` 非线性：net8+ 用 `System.HashCode`，netstandard2.0 用 fmix32 链；`KeyHash.Hash(Color)` 的符号翻转碰撞从 2000/2000 降到 0/2000。
  - 实测（未优化，负载 16–20，噪声大）：RenderDemo 软件 A/B 看不出回归；GPU 动画帧（含新的 DrawImage 头像）CPU 侧中位数 1.14 ms。没有做优化构建的软件 DrawImage 吞吐量基准（`ImageShader` 是逐像素标量代码）。
  - 评审修复：软件 mip 缓存一帧内没有字节上限（2048² 位图的 100 个源矩形缩小可在一帧分配 344 MB、耗时 3.2 s），新增 `RasterContext.MaxLevelBytesPerFrame` = 256 MB，超出后退回第 0 级采样并上报；旋转图片在与裁剪求交之前就构造 mip；SVG 的 `MaxImagePixels` 只在解码之后检查（预算耗尽后不再调用解析器）；`ContentHash` 与终结器的注释补充。
- **媒体：CSS 属性、标记映射、UI 接入**（细节见 [05](05-ui-core.md)、[16](16-css-properties.md)、[17](17-awaui-grammar.md)、[03](03-renderer.md)「图片」）：
  - CSS：`object-fit`、`object-position`、`image-rendering`、`aspect-ratio` 移入第 1 期（`PositionValue`、`AspectRatioValue`，`CssPropertyGen --check` 最新）。CSS 成为这些值的唯一来源，`Image.Fit / Position / Rendering` 与 UI 类型 `ObjectFit / ImageRendering / ObjectPosition` 删除（决策 D35）。`LayoutMapper` 对所有元素映射 `aspect-ratio`；`Svg` 元素也应用 object-fit / object-position；新增 `ImageElement.AccessibleName`。
  - 标记：`img` 的 `width / height` 为数值属性；`svg` 映射到 `AwAUI.UI.Svg`，内联子树在编译期序列化、每个字面子树一个惰性解析的静态 `SvgDocument` 字段，动态内容报 AW4012。
  - 加载：`ImageSource.FromStream` 保留至多 16 MiB（`MaxRetainedStreamBytes`）的编码字节，可重复加载；`ImageCache.Loaded` 与 AW1010 推迟到绘制之后或投递的回调中触发。
  - UI 画位图：`ImagePainter.DrawBitmap` 调用 `DrawImage`（pixelated / crisp-edges → Nearest，其余 → Linear）；`ObjectFitting.Crop` 只画内容盒内的部分（cover / none 不发裁剪命令）；`border-radius` 时内容裁成圆角；动画换帧调用 `Bitmap.Invalidate()`；`ImagingSvgImageResolver` 让 `<img src=svg>` 里的 data: PNG/JPEG 能显示。`UIHost` 接入丢帧契约（`UIHost.DroppedFrames`）。GPU 对照**只在 scratchpad 的一次性程序**里做过（Metal 离屏，2x，3 个动画帧，最大差 9/255，差值超过 2 的通道 0.008%），`tests/AwAUI.UI.UnitTests` 不引用 Metal。
  - 评审修复：**块级替换元素被拉伸到包含块宽度**（高，第六批就存在；`img { display:block; max-width:100% }` 全部走样）—— `MapLayoutStyle` 把替换元素的 Block / Flex 映射为不拉伸的 `InlineFlex`；**测量把可用宽度当成 min/max 钳制后的宽度**（高）—— 按 CSS 2.1 §10.4 约束表重写；max/min-height 不经比例传到宽度；非法尺寸提示被当作 0；位图目标矩形没有对齐设备像素（`ObjectFitting.SnapToDevice`）。实现者担心的「`<Svg Document>` 在 `AwAUI.*` 命名空间里误解析」经集成测试证实不存在。
- **输入：平台文本输入与 `Input` 编辑**（细节见 [04](04-platform.md)、[05](05-ui-core.md)「文本输入」）：
  - `AwAUI.Platform`：`TextInputMethod`（抽象类）、`ITextInputClient`（框架实现的接口，登记在 `ArchitectureTests` 的允许列表）、`TextInputRange / TextInputOptions / TextInputContentType`、全异步的 `Clipboard`、`RawKeyEvent.LogicalKey / Location`、`KeyModifiers.AltGraph`、追加的 `PhysicalKey`（小键盘、Intl 键、F13–F24 等）与 `PhysicalKeys` / `KeyValues`（决策 D34）。
  - 五个平台实现：Headless（可脚本化的输入法、内存剪贴板、US / 德语模拟布局）、macOS（`NSTextInputClient`、`NSPasteboard`）、Browser（派生自新抽象，`resetComposition`、客户端路径的移动端退格 / 回车）、Win32（IMM32、`ToUnicodeEx`、`CF_UNICODETEXT`）、X11（XIM on-the-spot、CLIPBOARD 选择含 INCR）。
  - UI：`InputElement` 实现选区、预编辑、水平滚动；按字素簇 / 按词编辑、逻辑键快捷键、指针选择、异步粘贴。`KeyGesture` 的字母键按逻辑键匹配。
  - 验证：无头端到端 45 项 + 评审 6 项；平台纯逻辑测试在 `tests/AwAUI.Browser.Tests/Platforms/`（X11 选择协议用内存假 X 服务器端到端）；macOS 真实 AppKit 探针 25 项（scratchpad，**没有切换真实拼音 / 日语输入法**）；真实无头 Chromium 冒烟 48 项、Node 冒烟 57 项。**Win32 与 X11 的原生部分从未在对应系统上运行**。
  - 评审修复：X11 只改高亮的 PreeditDraw 被当成删除；Browser 上 Ctrl+Alt+字母 / 数字快捷键全部失效（本批 AltGr 判定引入的回归）；剪切在剪贴板写入失败时丢失文本、剪贴板同步异常穿出按键分派；NumLock 关闭时小键盘不能编辑；组合中的输入框被移出树后残留预编辑；Win32 32 位进程上 `WM_IME_SETCONTEXT` 清位溢出；macOS discard 期间的同步回调（防御性）。
- **DevTools 面板**（细节见 [06](06-devtools.md)、[20](20-devtools-protocol.md)）：新程序集 `AwAUI.DevTools`（决策 D33），独立窗口；Diagnostics 新增 `DevToolsClient` 与 `WebSocketClientTransport`。功能：虚拟化增量元素树、悬停高亮与拾取、Styles（匹配 / 继承 / 删除线 / 无效原因）与编辑、Computed、Events、盒模型。Counter 新增 `--devtools-panel`。实测（优化构建，负载 7–19）：约 1 万元素的应用打开面板 166 ms 墙钟，滚轮后面板一帧中位数 3.9 ms。**只在真实 macOS 窗口里冒烟运行过**（10 帧无崩溃），没有真实窗口截图或输入注入。
  - 评审修复：增量树更新为「事件数 × 行数」（一帧 400 条事件摊平 400 次）；编辑 `!important` 声明时丢失 `!important`；编辑期间推迟的刷新在无改动提交时丢失；`DevToolsClient` 拒绝请求时在锁内同步回调（可死锁）。
- **整合（主任务）**：
  - 部分光栅的容差收紧为 0：`IncrementalPaintTests` 的阴影用例在 1x、1.5x、2x 下容差都为 0，`IncrementalDifferentialTests` 不再容忍「raster depends on clip」，全部通过。
  - 修复 `ElementStyleInspector.ValidateDeclarationText`：以完整 `/* … */` 注释结尾的声明文本现在被接受，末尾的 '/' 分隔记号仍被拒（在记号层判断）；测试 `StyleTextValidationTests`。面板里额外补 ';' 的绕过无害，仍保留。
  - `ArchitectureTests`：`AwAUI.DevTools` 允许引用 Foundation、Graphics、Platform、Css、Layout、Text、Diagnostics、UI（实际不引用 Layout）；允许的公共接口加入 `AwAUI.Platform.ITextInputClient`。
  - 删除 `AwAUI.slnx` 中各任务临时用的 B7* 构建配置。
  - `dotnet build AwAUI.slnx`（Debug，加与不加 `-p:IncludeNetFx=true`）0 警告 0 错误；`dotnet test AwAUI.slnx` 全部通过，每个运行时（net10.0、net11.0）3985 项。

### 第八批（与 M2、M3、M4 并行）

四项（滚动系统；布局：Grid、`order`、aspect-ratio 后续与 CSS 小项；文本：CFF/CFF2 与 OpenType 整形；整理：第七批的后续事项）各自经独立评审并修复，之后由主任务整合。测试数见下文「当前测试数」。**验证范围**：只在 Apple M5 / macOS 27.2（arm64）上运行；net48 只编译、未运行；**Win32 与 X11 的滚轮代码只验证了编译、纯逻辑测试与逐行阅读**；macOS 的普通 / 精确滚轮经真实 `NSEvent`（`eventWithCGEvent` 后直接调用视图的 `scrollWheel:`）验证过，**触控板的手势相位与惯性相位只有单元测试**；性能数字在负载 13–65 的机器上测得，只能当上界。

- **滚动系统**（细节见 [21](21-gesture-scroll-api.md)、[05](05-ui-core.md)、[04](04-platform.md)、[18](18-scene-compositor.md)）：
  - 平台：`RawPointerEvent.CreateWheel` 带增量单位（`WheelDeltaMode { Pixel, Line, Page }`）、手势相位与系统惯性相位（`ScrollPhase`），旧构造函数保留并填默认值；`WheelDeltas` 集中换算（默认每格 3 行 = 100 px，Chromium 约定；NaN、无穷与超大值钳制）；虚属性 `WindowImpl.PreferredScrollbarStyle`。macOS 读精确增量与两种相位（行模式每行 40 px，WebKit 约定），跟随系统「显示滚动条」设置；Win32 用系统每格行数并在 `WM_SETTINGCHANGE` 后重读，小数增量（高精度滚轮、精确式触控板）按精确处理，「一次一屏」累加满一格才翻页；X11 按钮 4–7（XInput2 平滑滚动未做）；Browser 的 deltaMode 映射为三种单位（每行仍为 40 px）。Headless 新增 `WheelNotches`、`TrackpadScroll`、`ScrollbarStyle`。
  - UI：overflow 为 auto / scroll / hidden 的元素是滚动容器（hidden 只能程序滚动），根元素即视口（决策 D43）；`ScrollController`（`Offset / ContentSize / ViewportSize / MaxOffset / IsScrolling`、`JumpTo`、返回 Task 的 `ScrollTo` / `ScrollIntoView`、`Stop`、每帧至多一次的 `Scrolled` 与 `ScrollEnd`），`Element.ScrollController`，`ElementNode.OnScroll / OnWheel / ScrollController`。滚动位置跨重建保留；内容变小时钳制并发出 scroll 事件。偏移对齐设备像素、在绝对几何遍历中施加，命中测试与元素矩形自动包含滚动；只滚动的帧不重新布局（决策 D44）。物理在 UI 线程上按帧时钟推进，`Simulation` 保持纯函数（决策 D45）。
  - 输入：可取消的 `wheel` 事件先发给元素，之后由最内层「在该方向上还能滚」的容器滚动，不拆分剩余增量，沿祖先链传递，`OverscrollBehavior.Contain / None` 截断；Shift+滚轮横向；普通滚轮套一段可叠加的短动画（140 ms）；触控板直接跟随增量、锁定在手势开始时选中的容器直到惯性结束、不叠加自己的惯性，没有惯性相位的手势在最后一个事件 100 ms 后结束；Page 增量按目标视口的 87.5% 换算（同 Chrome）。方向键、PageUp/PageDown、Home/End、空格在按键没被消费且不在输入框中时滚动；键盘或程序聚焦以最小移动把元素滚到可见。
  - 触摸：`VelocityTracker`（约 100 ms 的加权最小二乘）；移动超过 8 px 容差后锁定主轴、向按下的元素发 `pointercancel` 并抑制 click，松手交给 `ScrollPhysics.CreateFling`；`BouncingPhysics` 在边缘橡皮筋并回弹。内建在滚动管理器里，**没有通用的手势竞技场**。
  - 滚动条：UI 自绘。经典样式常驻、滑块可拖动、点击轨道翻页；覆盖样式在滚动或悬停时出现、1 s 后淡出；按平台选择，触摸总是覆盖式；可命中，按圆角裁剪（决策 D46）。改变了已有应用的外观：Win32 / X11 上内容超出窗口的应用现在在窗口边缘显示经典滚动条。
  - DevTools 面板删除 `PanelWindowEvents`：树与样式区用 `OnWheel`（`PreventDefault` 后由模型自己滚动虚拟化的行），快捷键挂在面板根元素（`TabIndex = -1`，点击面板任何位置焦点落在面板内）。macOS 删除「重新可见时 `OnResized`」变通：在真实 Metal 窗口中用 `MacWindow.CaptureContent` 验证最小化后恢复、被其他窗口遮挡后移开都显示最新内容（三轮）；`DroppedFrames` 始终为 0，所以丢帧重画路径仍只有无头测试；负载约 50 时 macOS 的「重新可见」通知迟到 1.5 s 以上，这发生在任何出帧之前，与删掉的变通无关。
  - 测试与实测：随机滚动差分（局部重画与整帧非增量渲染逐字节比较，外加命中测试；默认 12 个种子，`AWAUI_SCROLL_DIFF_SEEDS=300` 跑过）；评审新增的几何一致性测试（只平移子树 vs 强制完整布局，逐元素比较矩形与像素，跑过 80 个种子）。2000 行列表、2x、每帧滚 3 px（优化构建，负载 21–54）：无布局，管线中位数 4.2 ms，光栅 36 ms —— 容器视口（1560×1120 设备像素）整块重画，[18](18-scene-compositor.md) 的「搬移已有像素 + 重画新条带」未做；每帧重录 6.8 个、复用 15 个单元。
  - 评审修复：更换控制器后几何与控制器状态不同步（内容停在旧偏移、新控制器没有度量，滚轮滚不动）—— 已施加的偏移与度量改为元素的几何状态，由新控制器接过；**没有时钟时读墙上时间**（违反 `UIRoot.Clock` 为 null 时确定性渲染的约定，是 `IncrementalDifferentialTests` 偶发失败的根因）—— 改为每帧推进 1/60 s；平滑滚动中内容变小时偏移越过末端；另一轴的输入打断 `ScrollTo` 后程序动画仍在推进；`ClampedSimulation` 从边界朝内运动时在 t=0 判为结束（惯性丢失）；自定义物理返回永不结束的模拟（`MaxSimulationSeconds` = 30）；`overflow-x: clip` 加 `overflow-y: visible` 被当成滚动容器。实现阶段已有的 `IncrementalDifferentialTests` 抓到可见滑块移动未计入损伤，实现者的随机测试抓到滑块按未对齐的偏移绘制（缓存可能保留旧滑块）与滚动条被定位内容盖住。
- **布局：Grid、`order`、aspect-ratio 后续、CSS 小项**（细节见 [08](08-css.md)、[16](16-css-properties.md)）：
  - CSS：`grid-template-columns / rows / areas`、`grid-auto-columns / rows / flow`、`grid-row / column-start / end`、`justify-items`、`justify-self` 与 `order` 进入第 1 期，简写 `grid-row`、`grid-column`、`grid-area`、`grid-template`（两种形式）、`grid`、`place-items / self / content`。轨道列表完整解析（线名、`fr`、`minmax()`、`fit-content()`、计数或 auto-fill / auto-fit 的 `repeat()` 及其固定尺寸规则），区域字符串按规范校验，解析对输入线性，序列化与 Chromium 一致。`CssPropertyGen` 增加新的值类型与关键字别名表（`--check` 最新）。
  - 布局（`LayoutEngine.Grid.cs`）：显式与隐式网格、按线号 / 线名 / 区域放置、自动放置（行 / 列、稀疏 / 稠密）、完整的轨道尺寸算法（§11.5）、gap、对齐、rtl、百分比轨道、min/max、以网格区域为包含块的绝对定位子元素、基本的容器基线。`LayoutMapper` 映射 grid / inline-grid（按计算值实例缓存、按值比较）。上限见 [08](08-css.md)「Grid 的上限」（决策 D48）。实测（优化构建）：1 万个自动放置的项目 13–16 ms，改宽度后重排 6–10 ms；稠密放置原为二次方（1000 项 76 ms），跳过已满的行后接近线性。
  - `order`：整数，第 1 期；flex 与 grid 项目按（order，文档顺序）稳定排序后布局。**绘制与命中测试尚未按 `order`**（`LayoutMapper.PaintOrder` 已提供，见后续事项）。
  - aspect-ratio：新增 `LayoutStyle.IsReplaced`；`ComputeLeaf` 在宽高都为 auto 时用自身 min/max 与跨比例传来的 min/max-height 钳制宽度，再由宽度推出高度（第七批的 `min-width:10%` 案例现为 100×50）；非替换元素由比例推出高度时不小于内容高度（CSS Sizing 4，`width:100px; aspect-ratio:4` 内含 100px 子元素现为 100×100）；替换元素的叶子不再把固有尺寸报告为可滚动溢出。
  - `image-rendering` 接受 `optimizeSpeed` / `optimizeQuality`（解析时转为 `crisp-edges` / `smooth`）；42 个逻辑属性在解析时映射为左到右横排下的物理属性（决策 D49）。
  - 对照：实现者 32 个布局场景、评审 19 个场景与约 120 个解析值在 headless Chromium 中逐一核对。
  - 评审修复：**网格中带宽高比的非替换元素 normal 对齐按 start**（高；`.card{aspect-ratio:16/9}` 缩成内容宽，空卡片宽 0）—— 改按 Grid §6.2，只有替换元素按 start；**文本叶子忽略 aspect-ratio**（高；用测量比值代替样式比值）—— 非替换叶子按样式比值与 box-sizing，并有基于内容的自动最小高度；§6.6「只跨固定最大尺寸轨道时，自动最小尺寸钳制到这些轨道之和」；**轨道尺寸算法的 DoS**（`repeat(9999, auto)` 加 1 万个铺满的项目约 18 s CPU）—— 起止相同的项目合并分配（`DistributeRuns`），降到 0.28–0.45 s；`<grid-line>` 末尾的 `span`（`2 span`、`foo 2 span`）被拒。保留规范行为、与 Chrome 不同：spanning 项目跨 fit-content 轨道时的超上限分配。
- **文本：CFF/CFF2 与 OpenType 整形**（细节见 [19](19-text-api.md)、[03](03-renderer.md)「文本栈」）：
  - CFF / CFF2 轮廓（`OpenType/CffTable.cs`、`CffCharString.cs`）：全部 Type 2 运算符、hint 计数与 hintmask、带偏置的子程序、seac、CID 字体的 FDArray / FDSelect（格式 0/3/4）、CFF2 的 blend / vsindex（默认实例）；`FontMatrix` 忽略。上限：栈 48（CFF）/ 513（CFF2）、子程序深度 10、每字形 2^17 次操作、65 536 个点，失败的字形不向路径写任何东西。与 fontTools 对照约 10.5 万个系统字形（Hiragino Sans / Mincho / Sans GB、Apple SD Gothic Neo、STIX、SFIndia 等）0 差异。新增 CC0 测试字体（`tests/TestFiles/Fonts` 下五个 `AwATest-*.otf` 与 `AwATest-Layout.ttf`，由自写的 `build_test_fonts.py` 生成）。
  - `OpenTypeShaper`（公开 sealed 类）成为 `DefaultTextEngine` 的默认整形器（决策 D47）：GSUB 类型 1–8、GPOS 类型 1–9、全部查找标志、默认特性与 `TextStyle` 的特性（开 / 关 / 替代值 / 范围），没有 GPOS kern 时退回旧 'kern' 表；阿拉伯联接状态机（字体没有 init/medi/fina/isol 时退回 Unicode 表现形式与 lam-alef 连字，如 Geeza Pro）；希伯来标记在 GPOS 之前置零宽；`AwAUI.Unicode` 新增生成的 `Canonical_Combining_Class` 与公开的 `UnicodeData.GetCombiningClass`，按 HarfBuzz 的修正组合类重排标记。`BasicTextShaper` 保留（`TestTextEngine` 的默认、非 `OpenTypeFace` 的回退，已有文本黄金图因此不变）。对照：22 个 HarfBuzz 15.1 的期望输出入库；一次 686 例、18 个系统字体的 hb-shape 对比除分解 / 组合、New York 的 'trak'、Abel 的 .notdef 标记与一处隐藏 ZWNJ 外完全一致。
  - 已知问题：跨词字距 —— 保留逐词缓存，在词边界加 `TextShaper.GetBoundaryKerning`（只做 LTR）；连字的 advance 平分到各字素，连字内部（ffi、RTL 的 lam-alef）可放光标与命中；整形缓存键含字体特性；按 span 传特性的二次方开销在两个整形器中都改为线性。`GlyphRun.Bounds` 未改（属于 Graphics）。
  - 实测（优化构建，负载 26–47，±20%）：热缓存的构建 + 首次布局，拉丁 5.57 → 6.63 ms（+19%），CJK（Hiragino CFF）5.29 → 5.66 ms，阿拉伯持平；关掉缓存时拉丁 9.55 → 23.26 ms；整段一次整形比 `BasicTextShaper` 慢 5 倍（拉丁）到 14 倍（SF Arabic）；换宽度重排不变（约 0.14 ms）。
  - 评审修复：**恶意 GSUB/GPOS 解析无界**（共享子表与覆盖表，180 KB 的表整形 10 个字符 53 s）—— 按表长的解析预算、覆盖摘要按偏移缓存；**应用阶段只在子表成功时计数** —— 每次尝试都计数；**操作预算对真实字体太小**（DecoType Nastaleeq Urdu 每字符约 1200–1600 次，超过约 860 字符的整形静默截断）—— 改为每字符 8192、下限 2^16；**CFF2 的 Private DICT 重复解析**（4000 个 FD 共享 200 KB，载入 20 s）—— 按（偏移，长度）缓存并限总量；样式拆开的阿拉伯词丢失联接（传入至多 5 个码点的上下文，不跨硬换行等被替换为 ZWSP 的字符）；letter-spacing ≠ 0 时关闭可选连字（CSS Text 3 §8.2）；词边界字距的子表扫描上限 4096。评审另对 CFF 解释器做了 60 s 模糊测试（14 万个变异表、460 万次字形解码），只出现 `InvalidFontException`。
  - 三张段落黄金图（paragraph-align / styles / truncation）因 Noto Sans 有了 GPOS 字距与连字而变化（只有小的水平位移），整合时重新生成。
- **整理：第七批的后续事项**（细节见 [05](05-ui-core.md)、[03](03-renderer.md)「图片」、[17](17-awaui-grammar.md)、[11](11-compatibility.md)、[15](15-repo-structure.md)）：
  - 拆出 `tests/AwAUI.Platform.UnitTests`：以源码链接方式编译 Win32 与 X11 的 `Input/` 目录和 macOS 的 `KeyMap.cs`，原 `Browser.Tests/Platforms/` 下的测试移入（拆分时 130 + 250 = 原来的 380）；新的平台纯逻辑（如滚动这批的 `WheelInputTests`）也放在这里。
  - 4 张 DrawImage 黄金图移到 `tests/TestFiles/Golden`，`RenderTests/Golden` 与 `Golden.LocalGoldenDir` 删除。
  - UI 图片的 GPU 对照进仓库：`Rhi.Metal.Tests` 的 `UiImageParityTests`（该项目改为引用 `AwAUI.UI`；用独立的 `GpuRenderBackend`，共享后端的图集状态会改变 `GpuBackendTests` 的绘制调用数）。场景：object-fit 五种取值加 object-position、最近邻与双线性、512² 条纹大比例缩小、圆角头像、作为图片的 SVG、内联 SVG 图标、透明度、失败占位；1x / 2x 最大差 19/255 与 14/255，差值超过 8 的像素不到 0.005%。没有 Metal 时跳过，另有一项不需要 Metal 的场景自检。
  - 图片：加载失败时画「图片损坏」图标与替代文本（`alt=""` 不画，失败的图片由 0×0 变为 16×16）；`ImageCache.NotifyChanged(ImageSource)`；PNG 头部也在分配之前检查尺寸，新增 `ImageDecoder.Decode(data, maxPixels)`，SVG 嵌入位图每张至多 64M 像素（细节见 [05](05-ui-core.md)、[03](03-renderer.md)「图片」）。
  - 标记：`draggable` 改为 true / false 的枚举属性；支持 HTML 注释 `<!-- -->`；内联 svg 用 `ImagingSvgImageResolver.DefaultOptions` 解析、允许 `<style>`、按 HTML 外来内容规则修正名称大小写（见 [17](17-awaui-grammar.md)）。
  - **没做**：`<img>` 的 `width / height` 作为 CSS 表现提示 —— Css 的级联没有表现提示层级，在 UI 层模拟会让常见的 `img { max-width:100%; height:auto }` 变形。
  - 评审：补上 `NotifyChanged` 异步路径的测试与 22 个手工构造的恶意文件头测试（`HostileHeaderTests`，GIF / JPEG / VP8 / VP8L / VP8X / BMP / PNG / ICO，每项分配 < 256 KB；变异检查确认测试有效）；`UiImageParityTests` 删去 UI 并不支持的 `transform: rotate`（它原本声称覆盖了旋转）。
- **整合（主任务）**（本节只记已在代码中确认的部分）：
  - 根视口的可滚动溢出不再计入 `position: fixed` 后代（`LayoutEngine.Absolute.cs` 的 `LayoutOutOfFlow`；absolute 仍计入）。此前 300 px 窗口里 `top:250px; height:100px` 的 fixed 元素会让根视口能滚 50 px 却永远露不出它。
  - `RenderPipeline.MapLayoutStyle` 对替换元素调用 `LayoutMapper.ForReplaced`（设置 `IsReplaced` 与使用的宽高比），删除「display 映射为 InlineFlex」的规避；`ReplacedMeasure.Measure` 只保留 CSS 2.1 §10.4 的约束表，不再猜「确定宽度是可用宽度还是钳制结果」；引擎里的过渡判断（`LooksNatural`、网格 normal 对齐）只看 `IsReplaced`。
  - 三张段落黄金图重新生成。`ScrollInputTests` 不再用 `^1` 索引语法（此前 UI 测试项目的 net48 编译失败）。

### M2 GPU 渲染

- RHI + Metal + D3D11；GPU 管线（SDF 快速路径、图集、批处理、层缓存）
- 渲染线程、合成器动画；`overflow` 滚动
- CSS：transition / animation、transform、filter、阴影、Grid 布局
- 软件后端与 GPU 后端作为 `RenderBackend` 的两个实现互相对照
- DevTools：组件面板、盒模型、绘制/性能面板
- **验收**：软件与 GPU 输出差异在阈值内；长列表滚动满帧

**进展**：GPU 后端早于计划落地 —— 第五批已完成 RHI + Metal + GPU 后端（解析式快速路径、图集、批处理、与软件后端按阈值对照）；第六批把它合入 `AwAUI.Render` 并与软件后端共用光栅器，`AwAUI.MacOS` 增加 CAMetalLayer 呈现，`MetalRenderer` 组合根助手与设备丢失回退，Counter 与 RenderDemo 默认用 GPU（窗口自检与软件渲染最大差 7–11/255），见上文。UI 侧已有元素级 DisplayList 缓存与损伤矩形，但没有层树。第七批完成图片（`DrawImage`，两个后端，见上文）与 GPU 丢帧上报。第八批完成 `overflow` 滚动与 Grid 布局（见上文）：滚动在 UI 线程上进行，只滚动的帧不重新布局、只重画容器视口，但没有层缓存，视口整块重新光栅化（软件 2x 下 2000 行列表每帧约 36 ms，离「长列表滚动满帧」还差合成器与像素搬移）。D3D11、层缓存（`RetainedLayers`）、渲染线程与合成器、滤镜尚未开始。

### M3 全平台与触摸

- GLES3 RHI → Linux（X11，随后 Wayland）、Android；WebGL2 + `AwAUI.Browser`；iOS（复用 Metal）
- 触摸管线：重采样、手势竞技场、惯性滚动与平台化物理、嵌套滚动、`touch-action`
- 各平台 IME、软键盘、安全区、剪贴板、生命周期、surface 丢失恢复
- DevTools：WebSocket 传输、远程形态、触摸模拟与手势日志
- **验收**：同一示例应用在六个平台运行；手机上滚动与手势手感达标；可被桌面 DevTools 远程检查

**进展**：`AwAUI.Browser` 已有 canvas 软件呈现、输入、IME 与剪贴板（第五批，只在 Chromium 系无头浏览器中验证）；WebGL2 / GLES 未开始。DevTools 的 WebSocket 服务端传输已在 M1 中使用，第七批有了 WebSocket 客户端传输与可连接远程应用的面板（独立的远程 DevTools 应用仍未做）。「各平台 IME、剪贴板」第七批**已实现**（Win32、macOS、X11、Browser、Headless，见上文），但只在 macOS（探针，部分）、无头 Chromium 与无头平台上验证过，Win32 与 X11 从未在真实系统上运行。第八批有了触摸拖动滚动与惯性（内建在滚动管理器里，`BouncingPhysics` / `ClampingPhysics` 可选）和各桌面平台的滚轮 / 触控板输入；通用手势竞技场、重采样、`touch-action`、嵌套滚动连接仍未做。

### M4 文本完整化

- CFF/CFF2、完整 GSUB/GPOS/GDEF、Bidi、彩色 Emoji
- 各书写系统整形器：阿拉伯、希伯来、印度系、东南亚、通用整形引擎（USE）等
- 可变字体、AAT（Apple 系统字体需要）
- `Input / TextArea` 完整编辑体验、移动端选择手柄
- **验收**：通过公开的整形一致性测试集

**进展**：第八批完成 CFF / CFF2 轮廓与 `OpenTypeShaper`（GSUB / GPOS / GDEF、阿拉伯联接、希伯来、标记重排），并作为默认整形器（见上文）；Bidi 在 M1 已有。印度系 / USE / 东南亚整形器、Unicode 分解与组合、AAT、可变字体的非默认实例、彩色 Emoji 未做；还没有跑公开的整形一致性测试集（只有 22 个入库的 HarfBuzz 期望输出与一次 686 例的 hb-shape 对比）。

### M5 走向可用

- 组件库：行为原语 + Avalonia 风格基础控件（Fluent 主题）+ Tailwind 兼容工具类 + shadcn 组件库，见下文「组件库路线」与 [23](23-component-library.md)
- 无障碍：语义树、ARIA、各平台桥（Windows / macOS / Web → Android / iOS → Linux）
- 主题、热重载、进出场动画
- `.awaui` 语言服务器（补全、诊断、跳转）
- DevTools 其余面板、自动化测试 API、面向 AI 的检查接口
- Vulkan / WebGPU、3D transform、`:has()`、`@container` 等后置特性视需求

### 组件库路线（决策 D38–D42，2026-11 确定）

按 [23](23-component-library.md)「实施顺序」分五步，预计各占一到两批：

| 步 | 内容 | 验收 |
|---|---|---|
| 1 引擎前置 | `oklch() / oklab() / color-mix()`、`@property`、过渡与 `@keyframes` 动画引擎（含退出动画所需的结束通知）、逻辑属性（第八批若未完成）、容器查询、`::before / ::after / ::placeholder` 渲染、独立变换属性、`backdrop-filter`；`.awaui` 属性展开（`@attributes`）与 `AsChild` 所需的属性合并；多行 `TextArea`、撤销 / 重做、光标闪烁 | shadcn v4 的主题 CSS（`:root` 与 `.dark` 两套 oklch 令牌）原样加载并计算出正确颜色；`transition` 与 `animate-in` 在无头测试中逐帧可验证 |
| 2 原语与 Tailwind | `AwAUI.Controls.Primitives`（Portal / Popper / DismissableLayer / FocusScope / RovingFocusGroup / Presence / Collection + TypeAhead / SelectionModel / Virtualizer / Slot）；`AwAUI.Tailwind`（候选提取、工具与变体注册表、`@theme / @utility / @custom-variant / @apply`、构建期接入标记生成器、运行时补全、`Cn` + 冲突消解、`Cva`、诊断） | 原语的状态机测试；Tailwind 与 v4 官方输出的快照比对（覆盖 shadcn S1 用到的全部类名） |
| 3 第一批控件 | Controls A 期 + `AwAUI.Themes.Fluent` + `samples/ControlCatalog`；Shadcn S1 + `AwAUI.Icons.Lucide` + `samples/ShadcnGallery` | 每个控件满足 [22](22-controls.md) 质量门槛的键盘 / 指针 / 触摸测试与明暗黄金图；Shadcn 与官网示例并排截图 |
| 4 第二批控件 | Controls B 期、`AwAUI.Controls.DataGrid`；Shadcn S2；`awaui init / add / diff / build` 与官方注册表；系统集成（明暗跟随、系统强调色、平台字体、减少动效、平台变体） | `awaui add` 复制的组件在空项目中编译运行；第三方注册表条目可安装 |
| 5 扩展 | `AwAUI.Shadcn.Extensions`、Blocks、`AwAUI.Charts`；用 Controls 重写 DevTools 面板界面 | DevTools 面板自举 |

## 主要风险

| 风险 | 说明 | 应对 |
|---|---|---|
| **文本栈工作量** | 「全部书写系统 + 纯 C#」意味着重写相当于 HarfBuzz 的整形引擎，是全项目最大的单项 | 架构上按「OpenType 布局核心 + 各文字整形器」分层；用公开的整形测试集逐个文字达标；先做覆盖面最广的通用整形引擎 |
| **CSS 引擎范围蔓延** | 「直接支持 CSS」容易滑向实现半个浏览器 | 明确的支持清单与不做清单（08）；不支持的特性给出诊断而非静默；用 WPT 子集度量 |
| **声明式的性能** | 每次重建产生描述对象分配；大列表协调成本 | 依赖追踪使重建粒度到组件；Props 相等跳过；虚拟化列表；分配基准守护 |
| **IME 与触摸手感** | 六个平台各不相同，是自绘框架最常见的质量短板 | 接口按最复杂平台设计；每平台尽早做中文输入与滚动手感的实机验证 |
| **GPU 后端数量** | 每多一个图形 API 都是持续维护成本 | 首批严格 3 个；RHI 保持极小；软件渲染永远可用 |
| **WASM 性能与包体** | 单线程、JS 互操作、AOT 体积、Unicode 与整形数据表、无系统字体 | M0 即做技术验证；数据表与文字整形器可按特性裁剪 |
| **AOT/裁剪回归** | 任何一处反射都会在 iOS/WASM 上出问题 | CI 从第一天起 AOT 分析器警告即错误，保留 NativeAOT 冒烟测试 |
| **抽象只有一家实现** | 不做第三方后端，接口容易贴合自研实现 | 软件与 GPU 两个后端各自独立实现 `RenderBackend`；`NullBackend`；接口评审时以「假想的 Skia/Canvas2D 适配」做纸面推演 |
| **无参照的光栅正确性** | 没有 Skia 对照 | 软件/GPU 互相对照；公开的路径渲染测试集；黄金图像人工审核入库 |
| **多运行时的维护成本** | netstandard2.0 缺少大量 API 与运行时特性，快慢两条路径容易分叉 | 条件编译收敛在少数内核文件；各运行时黄金图像逐像素一致；公共 API 规则由分析器强制 |
| **自定义格式的工具成本** | `.awaui` 的编译器不难，难的是编辑器体验（Razor 工具链的教训） | 语法贴近 Razor 降低学习成本；工具分期；始终保留纯 C# 写法作为不依赖工具的退路 |
| **范围整体过大** | 渲染器 + 文本 + CSS + 六平台 + 控件库 + DevTools | 严格按窄路径推进；每阶段写明不做什么 |

## 当前测试数（第八批整合之前，待主任务整合后确认）

下表取各项评审报告中最新的数字（评审的数字优先于实现者的）；`dotnet test AwAUI.slnx` 的整体结果与合计在整合后填写。net48 只做编译检查（本机无法运行）。只在 Apple M5 / macOS 27.2 上运行过。

| 测试项目 | 数量 | 测试项目 | 数量 |
|---|---|---|---|
| ArchitectureTests | 60 | Markup.UnitTests | 440 |
| Browser.Tests | 251 | Markup.IntegrationTests | 7 |
| Controls.UnitTests | 202 | **Platform.UnitTests**（新） | 144 |
| Css.UnitTests | 1089 | RenderTests | 224 |
| DevTools.Tests | 30 | Rhi.Metal.Tests | 147 |
| Diagnostics.UnitTests | 365 | Samples.Tests | 4 |
| Graphics.UnitTests | 49 | Svg.UnitTests | 248 |
| Imaging.UnitTests | 300 | Tailwind.UnitTests | 未统计 |
| Layout.UnitTests | 231 | Text.UnitTests | 310 |
| UI.UnitTests | 472 | Unicode.UnitTests | 78 |

第七批之后每个运行时 3985 项。变化：Platform.UnitTests 是拆出的新项目（130 项来自原 Browser.Tests，另有滚动这批的 `WheelInputTests` 14 项），Browser.Tests 因此从 380 变为 251（含 `WheelModeTests` 1 项）；Imaging 加 `DecodePixelLimitTests` 与 `HostileHeaderTests`；Markup 加 `HtmlCommentAndEnumTests`；Rhi.Metal 加 `UiImageParityTests` 3 项；RenderTests 的项数不变（三张段落黄金图已重新生成）。Controls.UnitTests 与 Tailwind.UnitTests 属于组件库路线（不在第八批的报告范围内，Controls 的数字取自文本评审的旁路运行，Tailwind 没有报告）。Diagnostics、Graphics、Svg 本批没有改动，沿用第七批的数字。

另有 `tests/AwAUI.Browser.Tests/node` 下的 Node 冒烟 57 项与真实无头浏览器冒烟 48 项（不在 `dotnet test` 内），以及只在 scratchpad 里手动运行的 macOS AppKit 输入、遮挡与滚轮探针（不在仓库内）。`Rhi.Metal.Tests` 中的 Metal 测试（含 UI 图片对照）与 `Samples.Tests` 中的 `GpuCounterTests` 只在有 Metal 设备的 macOS 上运行，其他系统跳过；`Browser.Tests` 直接编译库的 `Translation/` 源码，`Platform.UnitTests` 以源码链接方式测试 Win32 / X11 / macOS 输入的纯逻辑（见 [11](11-compatibility.md)）。`Text.UnitTests` 中依赖系统字体的测试在字体缺失时跳过。较长的模糊与差分测试可加大规模：`AWAUI_FUZZ_SEEDS=N`（RenderTests 的 `SoftwareFuzz`、Metal 的 `GpuFuzzTests`）、`AWAUI_CLIP_SEEDS=N`（`ClipIndependenceTests`）、`AWAUI_UI_DIFF_SEEDS=N`（`IncrementalDifferentialTests`）、`AWAUI_SCROLL_DIFF_SEEDS=N`（`ScrollDifferentialTests` 与评审的几何一致性测试）。

## 后续事项

第五批之后「进行中」的各项已在第六批完成（见上文第六批）：

- ~~增量绘制~~：元素级 DisplayList 缓存与损伤矩形、软件后端局部光栅化。
- ~~Render 加固 + GPU 合入 + Metal 窗口呈现~~、~~`RadialGradientBrush` 的焦点~~。
- ~~`Image` 画位图~~：**只完成了 UI 侧**，`DrawImage` 本身未做，移到下面的第六批后续事项。
- ~~负载敏感的计时测试~~：ScalingTests 改用线程 CPU 时间（`clock_gettime(CLOCK_THREAD_CPUTIME_ID)` / `GetThreadTimes`）+ n 与 8n 交错多轮取最小值（上限 24，线性 7–13、二次方约 64）+ 分配字节数之比（确定性）；墙钟在超额订阅下对长测量的拉长远大于短测量，取最小值也无效。保活修复了真实缺陷：超时改为从 Ping 入队起算、每轮最多记入 2×间隔、判定前检查套接字未读字节，线程池饥饿 / 进程挂起不再误断开有响应的对端（`KeepAliveStarvationTests`）。整合时 `WebPRobustnessTests.MaximumHuffmanGroupCountStaysFast` 也改用线程 CPU 时间（`tests/Shared/ThreadCpuClock.cs`）。

第六批留下的后续事项（第七批完成的已划掉，细节见上文第七批）：

- ~~**`DrawImage` 端到端**~~：第七批完成，两个后端与 UI 都已接入（`ImagePainter.DrawBitmap` 调用它，pixelated / crisp-edges 映射为 Nearest）。
- ~~**与裁剪无关的软件光栅**~~：第七批完成（决策 D36）；整合时 `IncrementalPaintTests` 的阴影用例（1x / 1.5x / 2x）与 `IncrementalDifferentialTests` 的容差都已收紧到 0。
- ~~**GPU 丢帧上报**~~：第七批完成（`RenderTarget.LastFramePresented`，决策 D37；`UIHost.DroppedFrames`）。macOS 的「重新可见时 `OnResized`」变通还没有删，见下面的第七批后续事项。
- **`nextDrawable` 阻塞 1 秒**：被其他进程窗口遮挡、而遮挡通知未到时阻塞主线程；需要按 `addPresentedHandler:` 自行限制在途帧数（block 互操作）或把呈现放到渲染线程。
- ~~**`HashCodeCompat`**~~：第七批完成（net8+ 用 `System.HashCode`，netstandard2.0 用带种子的 fmix32 链）。
- ~~**CSS 第 1 期**~~：第七批完成，而且 CSS 是唯一来源（节点属性删除，决策 D35）；`LayoutMapper` 对所有元素映射 `aspect-ratio`。
- ~~**标记的 `img` / `svg` 映射**~~：第七批完成（字段实际是惰性解析的 `private static SvgDocument __svgN`，不是 `static readonly`，见 [17](17-awaui-grammar.md)）。
- ~~**`ImageSource.FromStream` 是一次性的**~~、~~`ImageCache.Loaded` 在布局中触发~~：第七批完成（保留至多 16 MiB 的编码字节；通知推迟到绘制之后）。加载中与失败时的占位图与 alt 文本**仍未做**，移到下面。
- ~~**`IFramebufferSurface` 的契约**（`src/AwAUI.Graphics/Backends/Backends.cs`）~~：第七批集成时已写入文档注释——锁定的缓冲在两次 `Lock()` 之间保留内容（尺寸 / 缩放变化时除外），`UIHost.PartialRaster` 依赖它（Win32、X11、Browser 按代码阅读已满足，未在真机验证）。
- ~~**SVG 焦点**~~：第七批完成（`SvgParseOptions.Svg11FocalPoint`，默认 SVG 2 行为）。
- **Mac 主线程测试工具**：`AwAUI.MacOS` 没有自动化测试（AppKit 要求主线程，xunit 不在主线程上运行）；拖动缩放、重新可见时的重呈现、窗口自检，以及第七批的 `NSTextInputClient` 与 `NSPasteboard`（scratchpad 里的 AppKit 探针，25 项）目前只靠临时探针与示例验证。CI 的 macOS 作业也可以加跑 `tests/AwAUI.Samples.Tests`。
- **在空闲机器上复测基准**：第六批的全部性能数字（RenderDemo / GpuDemo / Counter 的帧时间、图层隔离开销、1000 行悬停、窗口帧率）都在负载 5–280 下测得；UI 评审修复后的悬停基准没有复测；`MaxWorkPerFrame` = 2^30 对应的耗时也没有在空闲机器上标定。
- 其他已知的小问题：展平与圆弧的点数预算用尽时不上报降级；锥形情形（焦点圆不在结束圆内）的边缘没有抗锯齿；图层合成只应用矩形裁剪；文本墨迹的字形溢出是按 em 的估计，准确值需要 `AwAUI.Text` 提供墨迹包围盒；损伤只是一个包围矩形；没有节拍源时 GPU 模式只靠 `nextDrawable` 限速。

第七批留下的后续事项（第八批完成的已划掉，细节见上文第八批）：

- **布局（Layout，替换元素与 aspect-ratio）**：
  - ~~`ComputeLeaf` 在按 min/max 钳制宽度之后按比例推出高度~~（第八批）：min/max 跨比例传递、高度由钳制后的宽度推出，`min-width:10%` 的 50×25 图片在 1000px 容器里得到 100×50；整合时 `ReplacedMeasure.Measure` 去掉了猜测分支。
  - ~~在 `LayoutStyle` 中加「替换元素」标记~~（第八批）：`LayoutStyle.IsReplaced`，整合时 `RenderPipeline` 经 `LayoutMapper.ForReplaced` 设置，InlineFlex 规避删除；块布局与 left + right 的绝对定位都不再拉伸替换元素，网格 normal 对齐按 start。
  - ~~非替换元素的 aspect-ratio 缺少自动最小尺寸~~（第八批）：block / flex / grid 容器与（评审修复后的）文本叶子都有，`width:100px; aspect-ratio:4` 的案例得到 100×100。
- ~~**拆出 `tests/AwAUI.Platform.UnitTests`**~~（第八批）：把 `tests/AwAUI.Browser.Tests/Platforms/`（以源码链接方式测试 Win32 / X11 / macOS 输入的纯逻辑）拆成独立项目并加入 `AwAUI.slnx`。
- **Windows 与 Linux X11 运行器上的真实 IME 与剪贴板冒烟测试**：目前完全没有验证（Windows 作业已有的 net48 运行会覆盖 `Platform.UnitTests` 中 `Win32InputTests` 的纯逻辑）。第八批的 Win32 / X11 滚轮代码同样只验证了纯逻辑，可一并冒烟。
- ~~**DrawImage 黄金图的位置**~~（第八批）：把 `tests/AwAUI.RenderTests/Golden` 下的 4 个图片黄金图（image-sampling、image-sampling@2x、image-transforms、image-downscale）移到 `tests/TestFiles/Golden`，并去掉 `ImageTests` 中的 `directory:` 参数。
- ~~**`UIHost` 的滚轮与滚动系统**~~（第八批）：滚动系统落地，元素有 `OnWheel / OnScroll`；DevTools 面板的 `PanelWindowEvents` 已删除。
- ~~**删除 macOS「重新可见时 `OnResized`」变通**~~（第八批，已在真实 Metal 窗口里验证最小化后恢复与遮挡后移开；丢帧路径在真实窗口里仍未触发过）：`UIHost` 已按 `LastFramePresented` 重画丢掉的帧，这个变通可以删除，但还没有删；删除后在真实窗口里验证一次「最小化再恢复后显示最新内容」（丢帧路径本身也只用模拟的假目标做了无头测试）。
- ~~**UI 图片的 GPU 对照进仓库**~~（第八批，`Rhi.Metal.Tests` 的 `UiImageParityTests`，1x / 2x 最大差 19/255 与 14/255）：目前只用 scratchpad 的一次性程序验证过（最大差 9/255）；需要让 `tests/AwAUI.UI.UnitTests` 引用 `AwAUI.Rhi.Metal`，或把 UI 图片场景加入 `tests/AwAUI.Rhi.Metal.Tests`。也可以把 `ImageDrawTests.Overview_sheet`（`AWAUI_IMAGE_SHEET=路径`）的输出审核后存为黄金图。
- **图片的其余缺口**：~~失败时的占位图与 alt 文本渲染~~（第八批；加载中按设计仍不画）；~~`image-rendering` 的旧别名被拒~~（第八批，`optimizeSpeed` / `optimizeQuality` 为别名，`-webkit-optimize-contrast` 仍拒）；`Image` / `Svg` 的 `Width / Height` 是自然尺寸提示而不是 CSS 表现提示（`<img width=100 height=50 style="width:200px">` 得到高 100，浏览器 50；需要 Css 的表现提示层级，见第八批后续事项）；~~`FromBitmap` 的位图被改写时框架察觉不到~~（第八批，`ImageCache.NotifyChanged`）；~~Markup 内联 svg 不带 `ImagingSvgImageResolver`；内联 svg 不支持 `<style>`、不修正属性名大小写~~（第八批）；~~`ImageDecoder` 对 PNG 头声明的超大尺寸可能 OOM~~（第八批，各格式都在分配之前检查头部尺寸，另有 `Decode(data, maxPixels)`）。
- **Render 图片的性能与缓存**：没有优化构建的软件 DrawImage 吞吐量基准，`ImageShader` 没有 SIMD；mip 级别之间不混合（三线性）；GPU 动画图片每换一个版本新建纹理（不复用、不原地更新）；一帧需要的 mip 超过 64 MB 时每帧淘汰重建（可考虑按窗口面积调整预算）；缓存条目不计被引用的位图本身、最长持有 300 帧；GPU 后端不是与裁剪无关的（掩码仍按裁剪区）。
- **输入的其余缺口**：撤销 / 重做；多行 `TextArea`；光标闪烁；双向文本的视觉光标移动；输入法重新转换；macOS 的 `flagsChanged:` 与 Cocoa Emacs 键绑定、`characterIndexForPoint:`；X11 在没有客户端或密码框时没有 `XUnsetICFocus`（与 Win32 不一致）、没有 PRIMARY 选择；Win32 的假左 Ctrl（AltGr）未过滤、`ToUnicodeEx` flags = 4 需要 Windows 10 1607+；剪切在写入尚未完成时照常删除（之后失败仍丢文本）；没有调度器时异步粘贴被丢弃；Browser 的组合重置保护是否必要未被证明，Firefox / Safari / 真实 Windows 浏览器未测。
- **DevTools 面板的其余缺口**：应用内停靠形态与独立的远程 DevTools 应用；搜索（协议的 `Tree.search` 未实现）、强制伪状态与属性 / class 编辑的界面、样式表编辑、源码跳转、组件页；面板不处理重连；任何结构或文本事件都刷新选中元素的样式（每帧改文本的应用每帧 3 个请求）；事件日志先序列化后截断；超过 1 万节点后的「全部展开」未测；没有真实窗口截图或输入注入；Windows、Linux 未运行。
- ~~**标记编译器的已有问题**~~（第八批完成；评审另发现 `spellcheck` 的同类问题，见第八批后续事项）：`IntrinsicElements` 把 `draggable` 当布尔属性，`draggable="true" / "false"` 都生成 `""`；HTML 注释 `<!-- -->` 报 AW4001（只支持 `@* *@`）。

第八批留下的后续事项：

- **滚动**（见 [21](21-gesture-scroll-api.md)）：
  - CSS 属性 `overscroll-behavior(-x/-y)`、`scrollbar-width`、`scrollbar-color`、`scroll-behavior`、`scrollbar-gutter` 在 Css 中还只是计划中的属性（`Properties.json` 标为第 2 / 3 期），目前只能经 `ScrollController.OverscrollBehaviorX/Y`、`ScrollbarWidth`、`ScrollbarThumbColor / TrackColor` 设置；加入 Css 后 `ScrollManager.BehaviorFor`、`RenderPipeline.ScrollbarGeometryFor / PaintScrollbarsNow` 与 `ScrollController.StartScrollTo` 的默认动画改读计算值，控制器属性作为覆盖。
  - 经典滚动条不占布局空间，盖住内容的内侧边缘：需要在滚动容器的内边距盒里预留粗细（默认 12 px、thin 8 px；`overflow: scroll` 与 `scrollbar-gutter: stable` 总是预留，`auto` 只在真的溢出时，需要第二遍布局），例如由 UI 按 `RenderPipeline.EffectiveScrollbarStyle` 填写 `LayoutStyle` 的滚动条槽输入。
  - 未实现：通用手势竞技场与 Tap / Pan / Pinch 等识别器（只有内建的滚动拖动）、`NestedScrollConnection`、`scroll-snap`、`position: sticky`、滚动锚定、Stretch / Glow 越界效果（声明了，画成钳制）、经典滚动条的箭头按钮与按住轨道连续翻页、macOS 触控板的越界回弹（桌面物理在边缘钳制）。
  - 帧开销：滚动帧把容器视口整块重新光栅化；「搬移已有像素 + 重画新条带」与离线程合成都未做（见 [18](18-scene-compositor.md)）。
  - 行为差异：`wheel` 的目标是 `captured ?? hit`，指针被捕获时发给捕获者（浏览器的 wheel 不受捕获影响）；不是层叠上下文的滚动容器，其 z-index 为正的定位后代画在滚动条之上；`ScrollController.Physics` 只影响触摸拖动与惯性，滚轮总用桌面的平滑动画（`RenderPipeline.SmoothScrolling = false` 关闭）；没有焦点时键盘滚动作用于最近一次按下所在的滚动容器，否则作用于视口。
  - 触控板手势收不到 Ended / Cancelled 时（手势中途失焦、平台丢事件），控制器一直处于 `IsScrolling`：覆盖式滚动条不淡出、`ScrollEnd` 不触发，直到下一次手势；需要用时钟定时器加兜底超时。
  - `UIRoot.NeedsFrame` 不含 `RenderPipeline.NeedsAnimationFrame`：只有 `UIHost.RunFrame` 检查它，自己驱动帧的宿主收不到滚动动画的后续帧请求（经 `FrameRequested`），应把它并入 `NeedsFrame`。
  - 标记：`RuntimeElements` 没有映射 `scroll` / `wheel` 事件（`IntrinsicElements.EventNames` 里已有，`.awaui` 中写 `@onscroll` 报 AW4012），也没有 `ScrollController` 属性。
  - 平台：Win32（`WM_MOUSEWHEEL / WM_MOUSEHWHEEL`、`SystemParametersInfo`、`WM_SETTINGCHANGE`）与 X11（按钮 4–7）的原生滚轮代码从未在对应系统上运行；X11 没有 XInput2 平滑滚动；Windows 精确式触控板以小数 `WM_MOUSEWHEEL` 到达、直接跟随但没有相位，未用 DirectManipulation；Win32 的触摸以模拟鼠标到达，触摸拖动滚动不适用。macOS 触控板的相位与惯性映射只有单元测试；`preferredScrollerStyle` 只在宿主创建时读一次。`MacWheel` 暂放在 `KeyMap.cs` 末尾以借用已有的源码链接，可把 `src/MacOS/AwAUI.MacOS/Input/**` 也链接进 `Platform.UnitTests`。`MacWindow` 的类文档注释仍写着「重新可见时调用一次 `OnResized`」，与已删除的代码不符。
  - 丢帧重画路径（`nextDrawable` 超时后下一个节拍整帧重画）在真实窗口里仍未复现过，只有第七批的无头假目标测试。
- **布局**（见 [08](08-css.md)）：
  - 绘制与命中测试还不按 `order`：`RenderPipeline.Paint` 的非分层子元素循环与 `CollectLayered` 应改用 `LayoutMapper.PaintOrder`（递归安全的临时列表；它只在 flex / grid 容器且有子元素 `order ≠ 0` 时才重排，否则原样返回 `Children`），命中测试按绘制的逆序回放，随之正确；同时把 `ZIndexOf` 中「grid 目前按 flex 布局」的过时注释改掉。
  - Grid 未实现：基线对齐（baseline 按 start，容器基线取网格顺序中的第一个项目）、subgrid、masonry、§11.1 第 3、4 步（按行尺寸重算列尺寸）；`safe / unsafe`、`first / last baseline` 与 `legacy left / right` 组合（`legacy` 接受但按 normal）；`repeat()` 次数与网格线号不接受 `calc()`。
  - Grid 的最坏开销仍是 O(项目数 × 轨道数)：起止各不相同、贡献递增的项目每个做一次 O(span log span) 的分配（5000 个项目约 1.5 s CPU），更好需要线段树之类的结构。
  - 与 Chrome 不同、保留规范行为：spanning 项目跨 fit-content 轨道时按 §11.5.1 分配超上限的空间（两条 `fit-content(50px)` 列、跨两列的 300px 项目：规范与本引擎 250/50，Chrome 0/300）。
  - 逻辑属性只按左到右横排映射（需要 `direction` / `writing-mode`），`getComputedStyle` 中逻辑属性名的文本为空（它们是简写）。
- **文本**（见 [19](19-text-api.md)）：
  - 没有 Unicode 分解 / 组合（a + U+0323 不合成 ạ，字体缺 Ǎ 时不分解）与希伯来表现形式组合；没有 GPOS 时的标记回退只做水平居中（无垂直定位）；这些是剩余 hb-shape 差异的主要来源。
  - 印度系、USE、泰文、高棉、缅甸等走默认路径，没有音节重排；AAT（morx / kerx / trak）忽略（Geeza Pro 靠表现形式回退，New York 的 'trak' 不生效）；FeatureVariations、可变字体的非默认实例与 CFF 的 `FontMatrix` 忽略；竖排不在范围内。
  - 词边界字距只做 LTR；词间的上下文 GSUB（如 calt）仍会丢失；`overflow-wrap: anywhere` 在连字内部紧急断开时整个连字画在第一行。
  - 低优先级差异：词边界字距可能取到词尾的零宽标记（字体的 IgnoreMarks 查找会对基字形）；在加了字距的边界断行时字距留在行尾单元上；RTL 下旧 'kern' 表按视觉顺序取对（HarfBuzz 按逻辑顺序）；`ArabicFallback` 在全部 GSUB 阶段之后执行而不是在 rlig 暂停处；全局关闭 init/medi/fina/isol 时仍走表现形式回退；默认可忽略字符的判断不看 'substituted'；超过 64 的草书链与 HarfBuzz 截断方式不同；词内被替换为 ZWSP 的控制字符不打断阿拉伯联接（行为与以前相同）。
  - 性能：`OpenTypeShaper` 整段一次整形比 `BasicTextShaper` 慢 3–14 倍（布局有缓存，整体只多约 20%）；没有 GPOS 时 `FallbackMarkPositions` 对每个标记调用 `TryGetGlyphBounds`，CFF 字体每次都解码轮廓（有界，但恶意字体可以很贵）；线程静态缓存（CFF 解释器、最近的整形计划）让每个线程最近用过的字体保持存活。
  - 没有在本批复跑 686 例的 hb-shape 对比（评审环境没有 HarfBuzz 工具）；`GlyphRun.Bounds` 未改。
  - Counter 示例的中日韩回退仍只找 Arial Unicode；CFF 字体可用之后应在其后加 `/System/Library/Fonts/Hiragino Sans GB.ttc`（可选 `ヒラギノ角ゴシック W3.ttc`），没有 Arial Unicode 的 Mac 也能显示中文（`CffFallbackLayoutTests` 已验证这两个 .ttc 的注册与回退）。
- **图片与标记**：
  - `<img>` / `<svg>` 的 `width / height` 作为 CSS 表现提示：Css 增加表现提示层级（例如 `StyleElement.PresentationalHints`，位于普通 UA 与普通作者之间、特异性 0），`ReplacedElement` 提供 `width:Wpx; height:Hpx; aspect-ratio:auto W / H`（宽高都给出时才带比例），之后 `ReplacedMeasure.Update` 不再把提示当作固有尺寸。目标：`<img width=100 height=50 style="width:200px">` 得到 200×50，`img { height:auto }` 仍按比例。
  - `ImageCache` 解码普通图片时不传 `maxPixels`，合法的高压缩比图片可以分配到 2 GB 的位图上限；可考虑 `ImageCache.MaxDecodedPixels`。
  - 替代文本只用测试字体 Ahem 验证过；截到 1000 个 UTF-16 单元时可能切开代理对；颜色变化时会重建一次段落。
  - 标记：`IntrinsicElements` 把 `spellcheck` 当布尔属性（HTML 中是 true / false / "" 的枚举，`spellcheck="false"` 被当作没有给出；运行时目前标为不支持，没有可见影响）；内联 svg 的 `<style><![CDATA[…]]></style>` 被序列化为转义文本；`@if {}` 与 `else` 之间的注释会切断 else 分支（与其他非空白内容相同）。
- **验证**：net48 只编译未运行；Metal 只在 Apple M5 上运行；性能数字都在负载 13–65 下测得。

待做（按模块，只列影响后续工作的）：

- **DevTools**：~~独立窗口前端~~（第七批）；应用内停靠与远程独立应用；源映射表与元素源码位置；协议中未实现的 `Tree.search / scrollIntoView / focus`、`Css.getVariables`、`Layout.getLayoutInfo`、`Overlay.setShowOptions`；内联编辑目前在内联层级而不是 DevTools 来源（见 [06](06-devtools.md)）。
- **布局**：~~grid、`order`~~（第八批，`order` 的绘制顺序见上面的第八批后续事项）；`sticky`（目前的回退见 [08](08-css.md)）；布局边界（`MarkDirty` 总是走到根）；网格基线对齐 / subgrid / masonry；按行尺寸重算列尺寸（§11.1 第 3、4 步）；rtl 与竖排下的逻辑属性（需要 `direction` / `writing-mode`）；~~替换元素与 aspect-ratio 的三项~~（第八批）。
- **Platform 补齐**（Browser 提出）：~~`TextInputMethod / ITextInputClient`、逻辑键（含 AltGr）、全异步剪贴板、小键盘的 `PhysicalKey`~~ —— 第七批完成（决策 D34），五个平台都已实现，但 Win32 / X11 只验证了编译与纯逻辑、macOS 只用探针部分验证。仍未做：笔的倾角 / 旋转 / 橡皮擦与预测采样、`IFramebufferSurface` 的脏矩形、生命周期回调。
- **Web 的真实环境验证**：Firefox、Safari、移动浏览器与软键盘、真实系统输入法、真实 DPR 变化。
- **CI**：macOS 作业运行 `tests/AwAUI.Rhi.Metal.Tests` 与 `tests/AwAUI.Samples.Tests`（托管 arm64 macOS 运行器的半虚拟化 Metal 设备是否可用未验证；没有 Metal 设备时测试跳过）；发布 `samples/BrowserDemo` 后运行 `node-smoke.mjs`（可选再在有 Chrome 的运行器上跑 `browser-smoke.mjs`）。
- ~~SVG 与图片接入 UI~~：第六批完成；~~`DrawImage`、CSS 映射与标记映射~~ 第七批完成。
- **`.awaui`**：运行时缺的 `TextArea / Canvas / Path` 与若干事件（AW4012），~~`Svg` 的映射~~（第七批完成）；捕获阶段处理器的写法；`<Grid T="int">` 泛型写法与嵌套类型标签；CLI；热重载与开发期样式推送；编译期 CSS 校验。手写 C# 组件派生自 `.awaui` 组件时拿不到 Props 胶水（生成器互相不可见，Razor 同样如此）。
- **UI**：~~`Input` 的光标、选区、IME、剪贴板~~（第七批完成）；仍缺撤销 / 重做、多行 `TextArea`、光标闪烁、双向文本的视觉光标移动、输入法重新转换；`::before / ::after` 渲染；double / groove / ridge / inset / outset 边框目前画成实线；命中测试只精确处理最内层圆角裁剪；`Computed` 长链刷新仍是递归；没有焦点修正；退出动画、组件 Ref、分析器 AW1002–AW1004。
- **文本**：~~CFF 轮廓~~（第八批，Hiragino 等 CID 字体可以显示；Counter 的回退列表还没加，见上）；彩色字形、hinting / 网格对齐、可变字体；~~逐词整形丢失跨词 kerning~~（第八批，只做 LTR）；~~`BasicTextShaper.IsKerningEnabled` 按 span 传特性时为二次方~~（第八批）；字形缓存按渲染目标独立；`GlyphRun.Bounds` 可能比墨迹小（UI 的损伤跟踪目前绕开它，用按 em 的字形溢出估计；第八批未改，属于 Graphics）；其余整形缺口见上面的第八批后续事项。
- **CSS**：相同样式的元素不共享 `ComputedStyle`（匹配属性缓存未做）；动画 / 过渡没有 API；`@import` 的样式表要由宿主加载；ex / ch 按 0.5em 近似；`DefaultFontSize` 固定为 16。
- **GPU**：D3D11、GLES / WebGL2（`MetalRenderBackend` 的回退包装可泛化后移入 `AwAUI.Render/Gpu`）；~~图片~~（第七批完成）、滤镜与 backdrop、彩色字形；保留层与合成器集成；路径裁剪蒙版缓存；macOS 窗口之外的 GPU 呈现；只在 Apple Silicon 上测过。
- **图片**：WebP 没有 SIMD；没有色彩管理（ICC、WebP 的 EXIF 方向被忽略）；解码上限偏宽松（每输入字节 4096 像素，比 PNG 宽 4–12 倍），是否收紧待定（第八批起调用方可经 `ImageDecoder.Decode(data, maxPixels)` 给出更严的上限，`ImageCache` 尚未使用）。
- **M0 遗留**：见 M0 一节的「M0 剩余」。
