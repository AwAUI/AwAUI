using System.Globalization;
using AwAUI.Controls.Primitives;

namespace AwAUI.Controls.UnitTests.Popper;

/// <summary>
/// <see cref="PopperLayout"/> 与 Radix Popper 的差分测试：floating-ui-radix-cases.txt 是 @floating-ui/core 1.7.5 按 Radix 的中间件组合
/// （offset(sideOffset + 箭头高) → shift(limitShift) → flip → size → arrow → transformOrigin → hide，含 Radix 的 transformOrigin 原样移植）
/// 对 800 个随机场景的输出。场景偏重小锚点（比箭头还小）、贴边、RTL、显式碰撞边界、逐边的碰撞内边距与裁剪祖先。
/// </summary>
public class PopperLayoutDifferentialTests
{
    const float Tolerance = 0.01f;

    public static IEnumerable<object[]> Cases()
    {
        using var stream = typeof(PopperLayoutDifferentialTests).Assembly.GetManifestResourceStream("floating-ui-radix-cases.txt")!;
        using var reader = new StreamReader(stream);
        string? line;
        int n = 0;
        var batch = new List<string>();
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            batch.Add(line);
            if (batch.Count == 100)
            {
                yield return new object[] { n, string.Join("\n", batch) };
                n += batch.Count;
                batch.Clear();
            }
        }
        if (batch.Count > 0) yield return new object[] { n, string.Join("\n", batch) };
    }

    [Fact]
    public void Reference_data_is_present()
    {
        int count = Cases().Sum(c => ((string)c[1]).Split('\n').Length);
        Assert.Equal(800, count);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_radix_composition(int first, string lines)
    {
        int i = first;
        foreach (var line in lines.Split('\n'))
        {
            try
            {
                RunCase(line);
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException($"case #{i}: {line}\n{ex.Message}");
            }
            i++;
        }
    }

    static void RunCase(string line)
    {
        var t = line.Split(' ');
        var vp = ParseRect(t[0])!.Value;
        var anchor = ParseRect(t[1])!.Value;
        var c = t[2].Split(':');
        var content = new Size(F(c[0]), F(c[1]));
        SideValues padding;
        var cp = t[12].Split(':');
        padding = cp.Length == 1 ? new SideValues(F(cp[0])) : new SideValues(F(cp[0]), F(cp[1]), F(cp[2]), F(cp[3]));
        Size? arrow = null;
        if (t[11] != "_")
        {
            var a = t[11].Split(':');
            arrow = new Size(F(a[0]), F(a[1]));
        }
        var options = new PopperOptions
        {
            Side = Placement.Parse(t[3]).Side,
            Align = t[4] switch { "start" => PopperAlign.Start, "end" => PopperAlign.End, _ => PopperAlign.Center },
            SideOffset = F(t[5]),
            AlignOffset = F(t[6]),
            AvoidCollisions = t[7] == "1",
            Sticky = t[8] == "p" ? PopperSticky.Partial : PopperSticky.Always,
            HideWhenDetached = t[9] == "1",
            ArrowPadding = F(t[10]),
            ArrowSize = arrow,
            CollisionPadding = padding,
            CollisionBoundary = ParseRect(t[13]),
        };
        var env = new PopperEnvironment(vp) { Rtl = t[14] == "1", FloatingClippingRect = ParseRect(t[15]), ReferenceClippingRect = ParseRect(t[16]) };
        var r = PopperLayout.Compute(anchor, content, env, options);

        Assert.Equal("=>", t[17]);
        Near(F(t[18]), r.X, "x");
        Near(F(t[19]), r.Y, "y");
        Assert.Equal(t[20], r.Placement.ToString());
        var avail = t[21].Split(':');
        Near(F(avail[0]), r.AvailableWidth, "availableWidth");
        Near(F(avail[1]), r.AvailableHeight, "availableHeight");
        if (t[22] == "_")
        {
            Assert.Null(r.ArrowX);
            Assert.Null(r.ArrowY);
        }
        else
        {
            var a = t[22].Split(':');
            NearOpt(a[0], r.ArrowX, "arrow.x");
            NearOpt(a[1], r.ArrowY, "arrow.y");
        }
        Assert.Equal(t[23] == "1", r.CannotCenterArrow);
        if (t[24] != "_") Assert.Equal(t[24] == "1", r.ReferenceHidden);
        var origin = t[25].Split('|');
        var actual = r.TransformOrigin.Split(' ');
        Assert.Equal(2, actual.Length);
        CssLength(origin[0], actual[0], "transformOrigin.x");
        CssLength(origin[1], actual[1], "transformOrigin.y");
        // CSS 变量与结果一致
        var vars = r.CssVariables.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(r.TransformOrigin, vars[PopperLayoutResult.TransformOriginVariable]);
        CssLength(Px(anchor.Width), vars[PopperLayoutResult.AnchorWidthVariable], "anchor-width");
        CssLength(Px(anchor.Height), vars[PopperLayoutResult.AnchorHeightVariable], "anchor-height");
        CssLength(avail[0] + "px", vars[PopperLayoutResult.AvailableWidthVariable], "available-width");
        CssLength(avail[1] + "px", vars[PopperLayoutResult.AvailableHeightVariable], "available-height");
    }

    static string Px(float v) => v.ToString(CultureInfo.InvariantCulture) + "px";

    /// <summary>比较 CSS 长度：百分比逐字相等，px 数值在容差内（JS 用 double 格式化）。</summary>
    static void CssLength(string expected, string actual, string what)
    {
        if (expected.EndsWith("%", StringComparison.Ordinal))
        {
            Assert.True(expected == actual, $"{what}: 期望 {expected}，实际 {actual}");
            return;
        }
        Assert.True(expected.EndsWith("px", StringComparison.Ordinal) && actual.EndsWith("px", StringComparison.Ordinal), $"{what}: 期望 {expected}，实际 {actual}");
        Near(F(expected.Substring(0, expected.Length - 2)), F(actual.Substring(0, actual.Length - 2)), what);
    }

    static void NearOpt(string expected, float? actual, string what)
    {
        if (expected == "_") Assert.True(actual is null, $"{what}: 期望没有值，实际 {actual}");
        else
        {
            Assert.True(actual.HasValue, $"{what}: 期望 {expected}，实际没有值");
            Near(F(expected), actual!.Value, what);
        }
    }

    static void Near(float expected, float actual, string what) =>
        Assert.True(Math.Abs(expected - actual) <= Tolerance, $"{what}: 期望 {expected}，实际 {actual}");

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    static Rect? ParseRect(string s)
    {
        if (s == "_") return null;
        var p = s.Split(':');
        return new Rect(F(p[0]), F(p[1]), F(p[2]), F(p[3]));
    }
}
