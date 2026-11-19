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
        // design/15：RHI 抽象与 GPU 后端在 AwAUI.Render（Rhi/、Gpu/），各图形 API 的实现是 src/Rhi 下的独立程序集
        ["AwAUI.Rhi.Metal"] = new[] { "AwAUI.Foundation", "AwAUI.Graphics", "AwAUI.Render" },
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
                        && n != "netstandard" && n != "mscorlib")
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
            "AwAUI.Platform.IWindowEvents",
            // design/04「IME」：由框架（UI 的可编辑元素）实现、平台只调用的文本输入客户端，与 IWindowEvents 同类
            "AwAUI.Platform.ITextInputClient",
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
