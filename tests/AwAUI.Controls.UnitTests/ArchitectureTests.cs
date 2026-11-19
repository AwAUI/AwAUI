using System.Reflection;
using AwAUI.Controls.Primitives;

namespace AwAUI.Controls.UnitTests;

/// <summary>
/// AwAUI.Controls 的依赖规则（与 tests/AwAUI.ArchitectureTests 的 DependencyRulesTests 相同的检查；那里登记本程序集需要同时引用本项目，
/// 已作为共享文件变更提出）。
/// </summary>
public class ArchitectureTests
{
    static readonly string[] Allowed =
    {
        "AwAUI.Foundation", "AwAUI.Graphics", "AwAUI.Platform", "AwAUI.Css", "AwAUI.Layout", "AwAUI.Text", "AwAUI.UI",
    };

    static Assembly Controls => typeof(SelectionModel<>).Assembly;

    [Fact]
    public void Only_references_allowed_layers()
    {
        var refs = Controls.GetReferencedAssemblies().Select(r => r.Name!).Where(n => n.StartsWith("AwAUI", StringComparison.Ordinal)).ToArray();
        var illegal = refs.Except(Allowed).ToArray();
        Assert.True(illegal.Length == 0, "AwAUI.Controls must not reference: " + string.Join(", ", illegal));
    }

    [Fact]
    public void No_third_party_dependencies()
    {
        var thirdParty = Controls.GetReferencedAssemblies().Select(r => r.Name!)
            .Where(n => !n.StartsWith("AwAUI", StringComparison.Ordinal) && !n.StartsWith("System", StringComparison.Ordinal) && n != "netstandard" && n != "mscorlib")
            .ToArray();
        Assert.True(thirdParty.Length == 0, string.Join(", ", thirdParty));
    }

    [Fact]
    public void No_public_interfaces()
    {
        var interfaces = Controls.GetExportedTypes().Where(t => t.IsInterface).Select(t => t.FullName).ToArray();
        Assert.True(interfaces.Length == 0, string.Join(", ", interfaces));
    }

    [Fact]
    public void Primitives_live_in_their_namespace()
    {
        var stray = Controls.GetExportedTypes().Where(t => t.Namespace != "AwAUI.Controls.Primitives").Select(t => t.FullName).ToArray();
        Assert.True(stray.Length == 0, string.Join(", ", stray));
    }
}
