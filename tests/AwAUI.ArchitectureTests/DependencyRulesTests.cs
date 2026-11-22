using System.Reflection;

namespace AwAUI.ArchitectureTests;

/// <summary>程序集依赖规则（design/01）。违反分层的引用在这里失败，而不是在代码评审中被发现。</summary>
public class DependencyRulesTests
{
    // 每个程序集允许引用的 AwAUI 程序集（不含自身）
    static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["AwAUI.Foundation"] = Array.Empty<string>(),
        ["AwAUI.Graphics"] = new[] { "AwAUI.Foundation" },
        ["AwAUI.Imaging"] = new[] { "AwAUI.Foundation", "AwAUI.Graphics" },
        ["AwAUI.Render"] = new[] { "AwAUI.Foundation", "AwAUI.Graphics" },
        ["AwAUI.Platform"] = new[] { "AwAUI.Foundation", "AwAUI.Graphics" },
        ["AwAUI.Headless"] = new[] { "AwAUI.Foundation", "AwAUI.Graphics", "AwAUI.Platform" },
        ["AwAUI.MacOS"] = new[] { "AwAUI.Foundation", "AwAUI.Graphics", "AwAUI.Platform" },
        ["AwAUI.Win32"] = new[] { "AwAUI.Foundation", "AwAUI.Graphics", "AwAUI.Platform" },
        ["AwAUI.X11"] = new[] { "AwAUI.Foundation", "AwAUI.Graphics", "AwAUI.Platform" },
        ["AwAUI.Diagnostics"] = new[] { "AwAUI.Foundation" },
        ["AwAUI.Unicode"] = Array.Empty<string>(),
        ["AwAUI.Css"] = new[] { "AwAUI.Foundation" },
        ["AwAUI.Text"] = new[] { "AwAUI.Foundation", "AwAUI.Graphics", "AwAUI.Unicode" },
        ["AwAUI.Layout"] = new[] { "AwAUI.Foundation" },
        // design/01：UI 不引用 Render（渲染后端由应用的组合根选择）；Diagnostics 提供 DevTools 的检查接缝
        ["AwAUI.UI"] = new[] { "AwAUI.Foundation", "AwAUI.Graphics", "AwAUI.Platform", "AwAUI.Css", "AwAUI.Layout", "AwAUI.Text", "AwAUI.Diagnostics", "AwAUI.Imaging", "AwAUI.Svg" },
        // 内嵌 DevTools 面板：用 UI 写成，只经 Diagnostics 的协议客户端访问被检查的应用；不引用 Render（后端由组合根传入）
        ["AwAUI.DevTools"] = new[] { "AwAUI.Foundation", "AwAUI.Graphics", "AwAUI.Platform", "AwAUI.Css", "AwAUI.Layout", "AwAUI.Text", "AwAUI.Diagnostics", "AwAUI.UI" },
        ["AwAUI.Markup.Compiler"] = Array.Empty<string>(),
        ["AwAUI.Svg"] = new[] { "AwAUI.Foundation", "AwAUI.Graphics" },
        // design/23 第 3 层（D41）：Tailwind 工具类引擎只输出 CSS 文本，不引用任何 AwAUI 程序集，可被 Roslyn 源生成器（netstandard2.0）引用
        ["AwAUI.Tailwind"] = Array.Empty<string>(),
        // 组件库（design/23，D38）：原语与基础控件只建立在 UI 及其公开的下层之上；不引用 Diagnostics / Imaging / Render。
        // 例外：ProgressRing 用 SVG 描边画圆环（BoxPainter 不画 conic-gradient），因此允许引用 AwAUI.Svg。
        ["AwAUI.Controls"] = new[] { "AwAUI.Foundation", "AwAUI.Graphics", "AwAUI.Platform", "AwAUI.Css", "AwAUI.Layout", "AwAUI.Text", "AwAUI.UI", "AwAUI.Svg" },
        // 数据表格（design/23）：与 Avalonia 相同拆成独立包，只依赖基础控件与 UI 的公开层
        ["AwAUI.Controls.DataGrid"] = new[] { "AwAUI.Foundation", "AwAUI.Platform", "AwAUI.Layout", "AwAUI.UI", "AwAUI.Controls" },
        // 可选 MVVM 适配层（design/23、24）：只桥接 UI 的 Node / State，不引用控件、渲染或平台实现
        ["AwAUI.Mvvm"] = new[] { "AwAUI.UI" },
        // design/15：RHI 抽象与 GPU 后端在 AwAUI.Render（Rhi/、Gpu/），各图形 API 的实现是 src/Rhi 下的独立程序集
        ["AwAUI.Rhi.Metal"] = new[] { "AwAUI.Foundation", "AwAUI.Graphics", "AwAUI.Render" },
        // 桌面组合根（design/01 规则 5、design/15）：按构建目标只引用一个平台后端（AwAUIDesktopTargetOS），
        // 允许列表列出全部可能项，实际依赖图里只有一个。
        ["AwAUI.Desktop"] = new[] { "AwAUI.Foundation", "AwAUI.Graphics", "AwAUI.Platform", "AwAUI.Css", "AwAUI.Text", "AwAUI.Render", "AwAUI.UI", "AwAUI.MacOS", "AwAUI.Win32", "AwAUI.X11" },
    };

    public static IEnumerable<object[]> Assemblies => Allowed.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void Assembly_only_references_allowed_layers(string name)
    {
        var asm = Assembly.Load(name);
        var refs = asm.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(n => n.StartsWith("AwAUI", StringComparison.Ordinal))
            .ToArray();
        var illegal = refs.Except(Allowed[name]).ToArray();
        Assert.True(illegal.Length == 0, $"{name} must not reference: {string.Join(", ", illegal)}");
    }

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void Core_assemblies_have_no_third_party_dependencies(string name)
    {
        var asm = Assembly.Load(name);
        var thirdParty = asm.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(n => !n.StartsWith("AwAUI", StringComparison.Ordinal) && !n.StartsWith("System", StringComparison.Ordinal)
                        && n != "netstandard" && n != "mscorlib"
                        // net10 兼容门面：System.ComponentModel.Win32Exception 由它转出（X11 / Launcher 捕获进程启动失败）
                        && n != "Microsoft.Win32.Primitives")
            .ToArray();
        Assert.True(thirdParty.Length == 0, $"{name} references third-party assemblies: {string.Join(", ", thirdParty)}");
    }

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void Public_extension_points_are_abstract_classes_not_interfaces(string name)
    {
        // design/11 规则 1：可能演进的扩展点用抽象类。允许的接口必须是小而稳定的契约或能力查询。
        var allowedInterfaces = new HashSet<string>
        {
            "AwAUI.Threading.IDispatcher",
            "AwAUI.Graphics.IFramebufferSurface",
            "AwAUI.Graphics.ILockedFramebuffer",
            // design/02 的表面能力接口（平台层实现，Metal 设备查询）
            "AwAUI.Graphics.IMetalLayerSurface",
            // design/24：Android / Wayland 的 EGL 窗口表面能力（GLES RHI 查询，平台层不引用渲染实现）
            "AwAUI.Graphics.IAndroidWindowSurface",
            "AwAUI.Graphics.IWlEglSurface",
            "AwAUI.Platform.IWindowEvents",
            // design/04「IME」：由框架（UI 的可编辑元素）实现、平台只调用的文本输入客户端，与 IWindowEvents 同类
            "AwAUI.Platform.ITextInputClient",
            // design/04「平台偏好」：经 WindowImpl.GetFeature<T>() 查询的只读能力契约，与 IWindowEvents 同类
            "AwAUI.Platform.IReducedMotionPreference",
            // design/04「平台偏好」：系统浅色 / 深色外观，同样经 GetFeature<T>() 查询
            "AwAUI.Platform.IAppearancePreference",
            // design/04「窗口装饰」：自绘标题栏的窗口动作（拖动 / 最小化 / 最大化），经 GetFeature<T>() 查询
            "AwAUI.Platform.IWindowChrome",
            // design/04「生命周期」：平台 → 框架的可选回调，用 Events as 查询；不能改 IWindowEvents，
            // 否则既有实现（示例、测试宿主）会因 netstandard2.0 无默认接口成员而无法编译
            "AwAUI.Platform.IWindowLifecycleEvents",
            "AwAUI.Platform.IWindowStateEvents",
            // design/24：移动端的内存压力回调，同样用 Events as 查询
            "AwAUI.Platform.IWindowMemoryPressureEvents",
            // design/23、24：MVVM 绑定目标与值转换器是小而稳定的能力契约，与 WPF 的 IValueConverter 对应
            "AwAUI.Mvvm.IBindingTarget",
            "AwAUI.Mvvm.IValueConverter",
        };
        var asm = Assembly.Load(name);
        var unexpected = asm.GetExportedTypes()
            .Where(t => t.IsInterface && !allowedInterfaces.Contains(t.FullName!))
            .Select(t => t.FullName)
            .ToArray();
        Assert.True(unexpected.Length == 0,
            $"New public interfaces need review (prefer abstract classes): {string.Join(", ", unexpected)}");
    }
}
