using System.Globalization;
using AwAUI.Controls.Primitives;

namespace AwAUI.Controls.UnitTests.Popper;

/// <summary>
/// 与 Floating UI 本体的差分测试：floating-ui-cases.txt 是 @floating-ui/core 1.7.5 对 1500 个随机场景（视口、锚点、浮层尺寸、方位、RTL、
/// 裁剪祖先、随机组合的 offset / flip / autoPlacement / shift + limitShift / size / arrow / hide 中间件）的输出。
/// 本实现按同样的输入运行，逐项比较坐标、最终方位与各中间件数据。
/// </summary>
public class FloatingUiDifferentialTests
{
    const float Tolerance = 0.01f;

    public static IEnumerable<object[]> Cases()
    {
        using var stream = typeof(FloatingUiDifferentialTests).Assembly.GetManifestResourceStream("floating-ui-cases.txt")!;
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
        Assert.Equal(1500, count);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_floating_ui(int first, string lines)
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
        var reference = ParseRect(t[1])!.Value;
        var fl = t[2].Split(':');
        var floating = new Size(F(fl[0]), F(fl[1]));
        var placement = Placement.Parse(t[3]);
        bool rtl = t[4] == "1";
        var env = new PopperEnvironment(vp) { Rtl = rtl, FloatingClippingRect = ParseRect(t[5]), ReferenceClippingRect = ParseRect(t[6]) };
        SizeApplyArgs? lastSize = null;
        var middleware = new List<PopperMiddleware?>();
        if (t[7] != "_")
        {
            foreach (var spec in t[7].Split(';'))
            {
                var p = spec.Split(',');
                switch (p[0])
                {
                    case "offset":
                        middleware.Add(new OffsetMiddleware(F(p[1])) { CrossAxis = F(p[2]), AlignmentAxis = p[3] == "_" ? null : F(p[3]) });
                        break;
                    case "flip":
                        middleware.Add(new FlipMiddleware
                        {
                            MainAxis = p[1] == "1",
                            CrossAxis = (FlipCrossAxis)int.Parse(p[2], CultureInfo.InvariantCulture),
                            FallbackAxisSideDirection = (FallbackAxisSideDirection)int.Parse(p[3], CultureInfo.InvariantCulture),
                            FallbackStrategy = p[4] == "0" ? FlipFallbackStrategy.BestFit : FlipFallbackStrategy.InitialPlacement,
                            FlipAlignment = p[5] == "1",
                            Overflow = new DetectOverflowOptions { Padding = F(p[6]), AltBoundary = p[7] == "1" },
                            FallbackPlacements = p[8] == "_" ? null : p[8].Split('+').Select(Placement.Parse).ToList(),
                        });
                        break;
                    case "shift":
                        middleware.Add(new ShiftMiddleware
                        {
                            MainAxis = p[1] == "1",
                            CrossAxis = p[2] == "1",
                            Limiter = p[3] == "1" ? new LimitShift { Offset = F(p[4]), CrossAxisOffset = F(p[5]), MainAxis = p[6] == "1", CrossAxis = p[7] == "1" } : null,
                            Overflow = new DetectOverflowOptions { Padding = F(p[8]), Boundary = ParseRect(p[9]) },
                        });
                        break;
                    case "auto":
                        middleware.Add(new AutoPlacementMiddleware
                        {
                            CrossAxis = p[1] == "1",
                            Alignment = p[2] switch { "start" => PopperAlign.Start, "end" => PopperAlign.End, _ => null },
                            AutoAlignment = p[3] == "1",
                            Overflow = new DetectOverflowOptions { Padding = F(p[4]) },
                            AllowedPlacements = p[5] == "_" ? null : p[5].Split('+').Select(Placement.Parse).ToList(),
                        });
                        break;
                    case "size":
                    {
                        bool shrink = p[2] == "1";
                        float origH = floating.Height;
                        middleware.Add(new SizeMiddleware
                        {
                            Overflow = new DetectOverflowOptions { Padding = F(p[1]) },
                            Apply = a =>
                            {
                                lastSize = a;
                                if (!shrink) return null;
                                return new Size(a.State.Rects.Floating.Width, Math.Max(0, Math.Min(origH, MathF.Floor(a.AvailableHeight))));
                            },
                        });
                        break;
                    }
                    case "arrow":
                        middleware.Add(new ArrowMiddleware(new Size(F(p[1]), F(p[2]))) { Padding = F(p[3]) });
                        break;
                    case "hide":
                        middleware.Add(new HideMiddleware(p[1] == "r" ? HideStrategy.ReferenceHidden : HideStrategy.Escaped) { Padding = F(p[2]) });
                        break;
                    default:
                        throw new InvalidOperationException(p[0]);
                }
            }
        }
        var r = PopperEngine.ComputePosition(reference, floating, env, placement, middleware);
        Assert.Equal("=>", t[8]);
        Near(F(t[9]), r.X, "x");
        Near(F(t[10]), r.Y, "y");
        Assert.Equal(t[11], r.Placement.ToString());
        var d = r.Data;
        if (t[12] == "_") Assert.Null(d.Offset);
        else
        {
            var o = t[12].Split(':');
            Assert.NotNull(d.Offset);
            Near(F(o[0]), d.Offset!.X, "offset.x");
            Near(F(o[1]), d.Offset.Y, "offset.y");
            Assert.Equal(o[2], d.Offset.Placement.ToString());
        }
        if (t[13] == "_") Assert.Null(d.Shift);
        else
        {
            var s = t[13].Split(':');
            Assert.NotNull(d.Shift);
            Near(F(s[0]), d.Shift!.X, "shift.x");
            Near(F(s[1]), d.Shift.Y, "shift.y");
            Assert.Equal(s[2] == "1", d.Shift.EnabledX);
            Assert.Equal(s[3] == "1", d.Shift.EnabledY);
        }
        if (t[14] != "_") Assert.Equal(int.Parse(t[14], CultureInfo.InvariantCulture), d.Flip?.Index ?? 0);
        if (t[15] == "_") Assert.Null(d.Arrow);
        else
        {
            var a = t[15].Split(':');
            Assert.NotNull(d.Arrow);
            NearOpt(a[0], d.Arrow!.X, "arrow.x");
            NearOpt(a[1], d.Arrow.Y, "arrow.y");
            Near(F(a[2]), d.Arrow.CenterOffset, "arrow.centerOffset");
            NearOpt(a[3], d.Arrow.AlignmentOffset, "arrow.alignmentOffset");
        }
        if (t[16] == "_") Assert.Null(d.Hide);
        else
        {
            var h = t[16].Split(':');
            Assert.NotNull(d.Hide);
            Assert.Equal(h[0] == "_" ? null : h[0] == "1", d.Hide!.ReferenceHidden);
            Assert.Equal(h[1] == "_" ? null : h[1] == "1", d.Hide.Escaped);
        }
        if (t[17] != "_")
        {
            var s = t[17].Split(':');
            Assert.True(lastSize.HasValue, "size.apply 未被调用");
            Near(F(s[0]), lastSize!.Value.AvailableWidth, "availableWidth");
            Near(F(s[1]), lastSize.Value.AvailableHeight, "availableHeight");
            Near(F(s[0]), d.Size!.AvailableWidth, "data.size.availableWidth");
        }
        var final = t[18].Split(':');
        Near(F(final[0]), r.Rects.Floating.Width, "floating.width");
        Near(F(final[1]), r.Rects.Floating.Height, "floating.height");
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
