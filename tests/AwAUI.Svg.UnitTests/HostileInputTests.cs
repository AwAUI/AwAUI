using System.Diagnostics;
using System.Text;
using AwAUI.Graphics;

namespace AwAUI.Svg.UnitTests;

/// <summary>
/// 不可信输入：解析与绘制必须在有界时间与内存内完成，只允许抛出 <see cref="SvgParseException"/>。
/// 时间上限刻意宽松（机器上可能同时跑着其他构建），用来捕获二次方或指数级退化，而不是做性能测量。
/// </summary>
public class HostileInputTests
{
    const string Ns = "xmlns=\"http://www.w3.org/2000/svg\"";

    /// <summary>解析 + 录制 + 光栅化（64×64）。返回文档；解析失败时返回 null。</summary>
    static SvgDocument? ParseAndRender(string text, SvgParseOptions? options = null, int maxSeconds = 10)
    {
        var sw = Stopwatch.StartNew();
        SvgDocument? doc;
        try
        {
            doc = SvgDocument.Parse(text, options);
        }
        catch (SvgParseException)
        {
            Assert.True(sw.Elapsed.TotalSeconds < maxSeconds, $"parse failure took {sw.Elapsed}");
            return null;
        }
        var list = doc.ToDisplayList(new Rect(0, 0, 64, 64), new SvgRenderOptions { CurrentColor = Color.Parse("#336699") });
        using (var bmp = SvgGolden.Render(list, 64, 64)) { }
        Assert.True(sw.Elapsed.TotalSeconds < maxSeconds, $"took {sw.Elapsed}");
        return doc;
    }

    [Fact]
    public void Billion_laughs_is_not_expanded()
    {
        var sb = new StringBuilder("<?xml version=\"1.0\"?>\n<!DOCTYPE svg [\n  <!ENTITY lol \"lol\">\n");
        for (int i = 1; i <= 9; i++)
        {
            sb.Append($"  <!ENTITY lol{i} \"");
            for (int j = 0; j < 10; j++) sb.Append(i == 1 ? "&lol;" : $"&lol{i - 1};");
            sb.Append("\">\n");
        }
        sb.Append($"]>\n<svg {Ns} viewBox=\"0 0 10 10\"><rect id=\"&lol9;\" width=\"10\" height=\"10\" fill=\"&lol9;\"/><style>&lol9;</style></svg>");
        var doc = ParseAndRender(sb.ToString());
        Assert.NotNull(doc);
        Assert.Contains(doc!.Diagnostics, d => d.Code == SvgDiagnosticCode.DoctypeIgnored);
        Assert.Contains(doc.Diagnostics, d => d.Code == SvgDiagnosticCode.UndefinedEntity);
    }

    [Fact]
    public void External_entities_are_never_resolved()
    {
        var text = $"""
            <!DOCTYPE svg [ <!ENTITY xxe SYSTEM "file:///etc/passwd"> <!ENTITY % p SYSTEM "http://example.invalid/x.dtd"> %p; ]>
            <svg {Ns}><style>&xxe;</style></svg>
            """;
        var doc = SvgDocument.Parse(text);
        Assert.Contains(doc.Diagnostics, d => d.Code == SvgDiagnosticCode.UndefinedEntity);
    }

    [Fact]
    public void Deep_nesting_is_rejected_without_stack_overflow()
    {
        int depth = 200_000;
        var text = $"<svg {Ns}>" + string.Concat(Enumerable.Repeat("<g>", depth)) + string.Concat(Enumerable.Repeat("</g>", depth)) + "</svg>";
        var e = Assert.Throws<SvgParseException>(() => SvgDocument.Parse(text));
        Assert.Contains("deeper", e.Message);
    }

    [Fact]
    public void Nesting_at_the_limit_renders()
    {
        int depth = SvgParseOptions.Default.MaxDepth - 2;
        var text = $"<svg {Ns} viewBox=\"0 0 10 10\">" + string.Concat(Enumerable.Repeat("<g transform=\"translate(0.001)\">", depth)) +
                   "<rect width=\"10\" height=\"10\"/>" + string.Concat(Enumerable.Repeat("</g>", depth)) + "</svg>";
        var doc = ParseAndRender(text);
        Assert.NotNull(doc);
        Assert.False(doc!.IsEmpty);
    }

    [Fact]
    public void Use_nesting_beyond_depth_is_dropped()
    {
        // 每层 use 引用上一层的组：渲染树深度随之增长，必须被截断而不是溢出栈
        var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 10 10\"><defs><rect id=\"l0\" width=\"10\" height=\"10\"/>");
        for (int i = 1; i <= 200; i++) sb.Append($"<g id=\"l{i}\"><g><g><use href=\"#l{i - 1}\"/></g></g></g>");
        sb.Append("</defs><use href=\"#l200\"/></svg>");
        var doc = ParseAndRender(sb.ToString());
        Assert.Contains(doc!.Diagnostics, d => d.Code == SvgDiagnosticCode.LimitExceeded);
    }

    [Fact]
    public void Exponential_use_fan_out_is_bounded()
    {
        var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 10 10\"><defs><rect id=\"l0\" width=\"1\" height=\"1\"/>");
        for (int i = 1; i <= 30; i++)
        {
            sb.Append($"<g id=\"l{i}\">");
            for (int j = 0; j < 10; j++) sb.Append($"<use href=\"#l{i - 1}\"/>");
            sb.Append("</g>");
        }
        sb.Append("</defs><use href=\"#l30\"/></svg>");
        var doc = ParseAndRender(sb.ToString());
        Assert.Contains(doc!.Diagnostics, d => d.Code == SvgDiagnosticCode.LimitExceeded);
    }

    [Theory]
    [InlineData("<path d=\"M0 0 L1e38 1e38 L-1e38 3e38 Z\" stroke=\"red\" stroke-width=\"1e30\"/>")]
    [InlineData("<rect width=\"3e38\" height=\"3e38\" rx=\"1e38\" stroke=\"red\" stroke-width=\"1e38\" stroke-dasharray=\"1e-30\"/>")]
    [InlineData("<circle cx=\"5\" cy=\"5\" r=\"1e38\" transform=\"rotate(1e30) scale(1e30)\"/>")]
    [InlineData("<g transform=\"scale(1e-38)\"><rect width=\"1e38\" height=\"1e38\"/></g>")]
    [InlineData("<g transform=\"matrix(1e38 1e38 1e38 1e38 0 0)\"><path d=\"M0 0 C1e38 0 0 1e38 1 1 A1e38 1 1e30 1 1 3 3\"/></g>")]
    [InlineData("<path d=\"M0 0 A1e-38 1e-38 0 0 1 1e-38 0 A1e38 1e38 0 1 1 1 1\" stroke=\"red\"/>")]
    [InlineData("<polygon points=\"0,0 1e38,0 0,1e38\" stroke=\"red\" stroke-width=\"1e38\" stroke-linejoin=\"miter\" stroke-miterlimit=\"1e38\"/>")]
    [InlineData("<rect width=\"10\" height=\"10\" stroke=\"red\" stroke-dasharray=\"1e38 1e-38\" stroke-dashoffset=\"1e38\"/>")]
    [InlineData("<rect width=\"10\" height=\"10\" style=\"stroke:red;stroke-width:999999999999999999999999999999px\"/>")]
    [InlineData("<svg width=\"1e38\" height=\"1e38\" viewBox=\"0 0 1e-38 1e-38\"><rect width=\"1\" height=\"1\"/></svg>")]
    // 远离原点的虚线：展平点的浮点舍入会让光栅器的虚线遍历无法推进（模糊测试发现）
    [InlineData("<circle cx=\"1e38\" cy=\"135\" r=\"12\" fill=\"none\" stroke=\"red\" stroke-dasharray=\"6 3\"/>")]
    [InlineData("<circle cx=\"1e9\" cy=\"1e9\" r=\"12\" fill=\"none\" stroke=\"red\" stroke-dasharray=\"0.5\"/>")]
    // 巨大描边宽度：光栅器在浮点溢出时越界（模糊测试发现）
    [InlineData("<circle cx=\"210\" cy=\"135\" r=\"2e7\" fill=\"none\" stroke=\"red\" stroke-width=\"1e30\"/>")]
    [InlineData("<circle cx=\"210\" cy=\"135\" r=\"2e5\" fill=\"none\" stroke=\"red\" stroke-width=\"1e38\"/>")]
    // 次正规数坐标：光栅器下标越界（模糊测试发现）
    [InlineData("<line x1=\"-1e-38\" y1=\"4.5\" x2=\"0\" y2=\"0\" stroke=\"black\"/>")]
    public void Huge_and_tiny_numbers_are_harmless(string body)
    {
        ParseAndRender($"<svg {Ns} viewBox=\"0 0 10 10\">{body}</svg>");
    }

    [Fact]
    public void Huge_curves_are_bounded_by_the_flattening_budget()
    {
        // 每条曲线在设备空间都很大：光栅器每条展平 1000 点，没有预算时 5 万条约分配 1 GB
        var d = new StringBuilder("M0 0");
        for (int i = 0; i < 50_000; i++) d.Append(" C100000 0 0 100000 5 5");
        var doc = ParseAndRender($"<svg {Ns} viewBox=\"0 0 10 10\"><path fill=\"red\" d=\"{d}\"/></svg>", maxSeconds: 30);
        Assert.Contains(doc!.Diagnostics, x => x.Code == SvgDiagnosticCode.LimitExceeded && x.Message.Contains("flattening"));
    }

    [Fact]
    public void Flattening_budget_does_not_affect_ordinary_content()
    {
        foreach (var file in Directory.GetFiles(SvgGolden.SvgDir, "*.svg"))
        {
            var doc = SvgDocument.Parse(File.ReadAllBytes(file));
            Assert.DoesNotContain(doc.Diagnostics, x => x.Code == SvgDiagnosticCode.LimitExceeded);
        }
        var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 1000 1000\">");
        var rng = new Random(1);
        for (int i = 0; i < 10_000; i++)
            sb.Append($"<path d=\"M{rng.Next(1000)} {rng.Next(1000)} c10 20 30 40 50 60 s10 10 20 0 a5 5 0 1 1 10 10z\" stroke=\"black\"/>");
        sb.Append("</svg>");
        var big = SvgDocument.Parse(sb.ToString());
        Assert.DoesNotContain(big.Diagnostics, x => x.Code == SvgDiagnosticCode.LimitExceeded);
    }

    [Fact]
    public void Subnormal_coordinates_do_not_reach_the_rasterizer()
    {
        // 模糊测试发现的最小复现：未处理时软件光栅器在 Rasterizer.DrawLine 中下标越界
        var doc = SvgDocument.Parse($"<svg {Ns} width=\"20\" height=\"160\"><line x1=\"-1e-38\" y1=\"45\" x2=\"0\" y2=\"0\" stroke=\"black\"/></svg>");
        using var bmp = SvgGolden.Render(doc, 40, 40);
    }

    [Fact]
    public void Huge_root_size_is_harmless()
    {
        var doc = ParseAndRender($"<svg {Ns} width=\"1e38\" height=\"1e38\"><rect width=\"1e38\" height=\"1e38\"/></svg>");
        Assert.NotNull(doc);
    }

    [Fact]
    public void Many_paths_are_bounded_by_the_render_node_limit()
    {
        var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 100 100\">");
        for (int i = 0; i < 150_000; i++) sb.Append("<path d=\"M1 1h1v1z\"/>");
        sb.Append("</svg>");
        var doc = ParseAndRender(sb.ToString(), maxSeconds: 30);
        Assert.Contains(doc!.Diagnostics, d => d.Code == SvgDiagnosticCode.LimitExceeded);
    }

    [Fact]
    public void Element_count_limit()
    {
        var text = $"<svg {Ns}>" + string.Concat(Enumerable.Repeat("<g/>", 1000)) + "</svg>";
        Assert.Throws<SvgParseException>(() => SvgDocument.Parse(text, new SvgParseOptions { MaxElements = 500 }));
    }

    [Fact]
    public void Huge_path_data_is_bounded_by_the_segment_budget()
    {
        var d = new StringBuilder("M0 0");
        for (int i = 0; i < 500_000; i++) d.Append(" l1 1 a1 1 0 1 1 2 2");
        var text = $"<svg {Ns} viewBox=\"0 0 10 10\"><path d=\"{d}\" stroke=\"red\" fill=\"none\"/></svg>";
        var doc = ParseAndRender(text, new SvgParseOptions { MaxPathSegments = 100_000 }, maxSeconds: 30);
        Assert.Contains(doc!.Diagnostics, d2 => d2.Code == SvgDiagnosticCode.LimitExceeded);
    }

    [Fact]
    public void Input_length_limit()
    {
        var text = $"<svg {Ns}>" + new string(' ', 2000) + "</svg>";
        Assert.Throws<SvgParseException>(() => SvgDocument.Parse(text, new SvgParseOptions { MaxInputLength = 1000 }));
        Assert.Throws<SvgParseException>(() => SvgDocument.Parse(Encoding.UTF8.GetBytes(text), new SvgParseOptions { MaxInputLength = 1000 }));
    }

    [Fact]
    public void Attribute_count_limit()
    {
        var attrs = string.Concat(Enumerable.Range(0, 300).Select(i => $" a{i}=\"1\""));
        Assert.Throws<SvgParseException>(() => SvgDocument.Parse($"<svg {Ns}{attrs}/>"));
    }

    [Fact]
    public void Tiny_dash_period_falls_back_to_solid()
    {
        var doc = ParseAndRender($"<svg {Ns} viewBox=\"0 0 10 10\"><path d=\"M0 0 L100000 0\" stroke=\"red\" stroke-dasharray=\"0.0001\"/></svg>");
        Assert.Contains(doc!.Diagnostics, d => d.Code == SvgDiagnosticCode.LimitExceeded);
    }

    [Fact]
    public void Long_gradient_href_chain_is_cut()
    {
        var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 10 10\"><defs><linearGradient id=\"g0\"><stop stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient>");
        for (int i = 1; i < 5000; i++) sb.Append($"<linearGradient id=\"g{i}\" href=\"#g{i - 1}\"/>");
        sb.Append("</defs>");
        for (int i = 0; i < 2000; i++) sb.Append("<rect width=\"10\" height=\"10\" fill=\"url(#g4999)\"/>");
        sb.Append("</svg>");
        var doc = ParseAndRender(sb.ToString());
        Assert.Contains(doc!.Diagnostics, d => d.Code == SvgDiagnosticCode.LimitExceeded);
    }

    [Fact]
    public void Selector_matching_is_budgeted()
    {
        // 深树 + 长后代选择器 + 大量规则：朴素匹配是 O(规则 × 元素 × 深度^k)
        var css = new StringBuilder();
        for (int i = 0; i < 2000; i++) css.Append("g g g g g g g g g g g g g g g g g g g g x").Append(i).Append(" { fill: red }\n");
        css.Append("g g g g g g g g g g g g g g g g g g g g g g g g g g g g g g * { fill: blue }\n");
        var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 10 10\"><style>{css}</style>");
        for (int i = 0; i < 200; i++) sb.Append("<g>");
        for (int i = 0; i < 3000; i++) sb.Append("<rect width=\"1\" height=\"1\"/>");
        for (int i = 0; i < 200; i++) sb.Append("</g>");
        sb.Append("</svg>");
        ParseAndRender(sb.ToString(), maxSeconds: 30);
    }

    [Fact]
    public void Many_clip_path_references_share_a_budget()
    {
        var d = new StringBuilder("M0 0");
        for (int i = 0; i < 20_000; i++) d.Append(" L1 1");
        var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 10 10\"><defs><clipPath id=\"c\" clipPathUnits=\"objectBoundingBox\"><path d=\"{d}\"/></clipPath></defs>");
        for (int i = 0; i < 20_000; i++) sb.Append("<rect width=\"1\" height=\"1\" clip-path=\"url(#c)\"/>");
        sb.Append("</svg>");
        var doc = ParseAndRender(sb.ToString(), maxSeconds: 30);
        Assert.Contains(doc!.Diagnostics, x => x.Code == SvgDiagnosticCode.LimitExceeded);
    }

    [Fact]
    public void Diagnostics_are_capped()
    {
        var body = string.Concat(Enumerable.Repeat("<rect fill=\"bogus\" width=\"1\" height=\"1\"/>", 10_000));
        var doc = SvgDocument.Parse($"<svg {Ns}>{body}</svg>");
        Assert.True(doc.Diagnostics.Count <= SvgParseOptions.Default.MaxDiagnostics + 1);
        Assert.Equal(SvgDiagnosticCode.DiagnosticsTruncated, doc.Diagnostics[doc.Diagnostics.Count - 1].Code);
    }

    [Fact]
    public void Pathological_text_patterns_are_linear()
    {
        // 未闭合的注释 / 引号 / 实体，以及大量 '&' —— 每种都只扫描一遍
        int n = 2_000_000;
        var amp = $"<svg {Ns}><rect fill=\"" + new string('&', n) + "\"/></svg>";
        var sw = Stopwatch.StartNew();
        SvgDocument.Parse(amp);
        var unclosedQuote = $"<svg {Ns}><rect fill=\"" + new string('x', n);
        Assert.Throws<SvgParseException>(() => SvgDocument.Parse(unclosedQuote));
        var manyLt = $"<svg {Ns}>" + string.Concat(Enumerable.Repeat("<!-- -->", n / 8)) + "</svg>";
        SvgDocument.Parse(manyLt);
        var style = $"<svg {Ns}><style>" + string.Concat(Enumerable.Repeat("a{", n / 2)) + "</style></svg>";
        SvgDocument.Parse(style);
        var css2 = $"<svg {Ns}><style>" + string.Concat(Enumerable.Repeat("\"", n)) + "</style></svg>";
        SvgDocument.Parse(css2);
        var transform = $"<svg {Ns}><g transform=\"" + string.Concat(Enumerable.Repeat("translate(1) ", n / 13)) + "\"/></svg>";
        SvgDocument.Parse(transform);
        Assert.True(sw.Elapsed.TotalSeconds < 20, sw.Elapsed.ToString());
    }

    // ---------------------------------------------------------------- 变异模糊

    static readonly string[] Tokens =
    {
        "<", ">", "/>", "</g>", "<g>", "\"", "'", "&", "&amp;", "&#x10FFFF;", "&#0;", "&#xD800;", "=", " ", "#",
        "url(#", ")", "<use href=\"#a\"/>", "<![CDATA[", "]]>", "<!--", "-->", "<!DOCTYPE x [", "]>", "e999", "-1e-9",
        "NaN", "Infinity", ".", ",", "a", "z", "1", "0", "M", "A", "Z", "rotate(", "matrix(", "%", "inherit", "!important",
        "\u0000", "￿", "\uD800", "{", "}", ";", ":", "*", ">", "clip-path=\"url(#c)\"", "id=\"a\"", "id=\"c\"",
    };

    /// <summary>深度模糊：设置环境变量 AWAUI_SVG_FUZZ=迭代数 放大各模糊测试的轮数（种子随之变化）。</summary>
    static int FuzzIterations(int normal, out int seedOffset)
    {
        var env = Environment.GetEnvironmentVariable("AWAUI_SVG_FUZZ");
        seedOffset = 0;
        if (int.TryParse(env, out int n) && n > 0)
        {
            seedOffset = Environment.TickCount;
            return n;
        }
        return normal;
    }

    static string Mutate(string s, Random rng)
    {
        var sb = new StringBuilder(s);
        int ops = rng.Next(1, 6);
        for (int k = 0; k < ops; k++)
        {
            int pos = sb.Length == 0 ? 0 : rng.Next(sb.Length);
            switch (rng.Next(6))
            {
                case 0: if (sb.Length > 0) sb.Remove(pos, Math.Min(sb.Length - pos, rng.Next(1, 20))); break;
                case 1: sb.Insert(pos, Tokens[rng.Next(Tokens.Length)]); break;
                case 2: if (sb.Length > 0) sb[pos] = (char)rng.Next(32, 127); break;
                case 3:
                {
                    int len = Math.Min(sb.Length - pos, rng.Next(1, 200));
                    var chunk = sb.ToString(pos, len);
                    sb.Insert(rng.Next(sb.Length), chunk);
                    break;
                }
                case 4: sb.Length = pos; break;
                default: if (sb.Length > 0) sb[pos] = char.IsDigit(sb[pos]) ? (char)('0' + rng.Next(10)) : sb[pos]; break;
            }
        }
        return sb.ToString();
    }

    [Fact]
    public void Mutation_fuzz_only_throws_documented_exceptions()
    {
        var seeds = Directory.GetFiles(SvgGolden.SvgDir, "*.svg").Select(File.ReadAllText).ToList();
        Assert.NotEmpty(seeds);
        int iterations = FuzzIterations(1500, out int seedOffset);
        var rng = new Random(20261118 + seedOffset);
        var sw = Stopwatch.StartNew();
        int parsed = 0, rejected = 0;
        for (int i = 0; i < iterations; i++)
        {
            var input = Mutate(seeds[i % seeds.Count], rng);
            try
            {
                var doc = SvgDocument.Parse(input);
                var list = doc.ToDisplayList(new Rect(0, 0, 32, 32), new SvgRenderOptions { CurrentColor = Color.Black, Opacity = 0.75f });
                using (SvgGolden.Render(list, 32, 32)) { }
                parsed++;
            }
            catch (SvgParseException)
            {
                rejected++;
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException($"Iteration {i} threw {e.GetType().Name}: {e.Message}\nInput:\n{input}");
            }
        }
        Assert.True(parsed > 200, $"parsed={parsed} rejected={rejected}");
        Assert.True(sw.Elapsed.TotalSeconds < 120 * Math.Max(1, iterations / 1500), sw.Elapsed.ToString());
    }

    static readonly string[] ValueTokens =
    {
        "0", "1", "-1", ".5", "1e38", "-1e-38", "1e-7", "NaN", "Infinity", "%", "px", "em", ",", " ", "M", "m", "L", "l", "H", "V",
        "C", "S", "Q", "T", "A", "a", "Z", "z", "0 0", "1 1 0 1 1", "00", "11", "url(#", "url(#a)", "url(#c)", "#a", "#tooth", ")", "(",
        "rotate(", "matrix(", "scale(", "translate(", "skewX(", "90", "1e30", "currentColor", "none", "inherit", "evenodd",
        "red", "#fff", "rgb(", "hsl(", "objectBoundingBox", "userSpaceOnUse", "reflect", "repeat", ";", ":", "!important", "{", "}",
        "fill:", "stroke-dasharray:", "0.0001", "999999999", "xMidYMid", "slice", "&amp;", "&#x41;",
    };

    /// <summary>只改写属性值（保持 XML 良构），覆盖值解析器、层叠、引用与渲染树构建。</summary>
    static string MutateValues(string s, Random rng)
    {
        var sb = new StringBuilder(s.Length + 64);
        int i = 0;
        while (i < s.Length)
        {
            int eq = s.IndexOf("=\"", i, StringComparison.Ordinal);
            if (eq < 0) { sb.Append(s, i, s.Length - i); break; }
            int end = s.IndexOf('"', eq + 2);
            if (end < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, eq + 2 - i);
            var value = s.Substring(eq + 2, end - eq - 2);
            if (rng.Next(4) == 0 && !value.StartsWith("http", StringComparison.Ordinal))
            {
                var v = new StringBuilder(value);
                int ops = rng.Next(1, 5);
                for (int k = 0; k < ops; k++)
                {
                    int pos = v.Length == 0 ? 0 : rng.Next(v.Length + 1);
                    switch (rng.Next(4))
                    {
                        case 0: v.Insert(pos, ValueTokens[rng.Next(ValueTokens.Length)]); break;
                        case 1: if (pos < v.Length) v.Remove(pos, Math.Min(v.Length - pos, rng.Next(1, 6))); break;
                        case 2: v.Clear().Append(ValueTokens[rng.Next(ValueTokens.Length)]); break;
                        default: if (v.Length > 0) v.Append(' ').Append(v.ToString(0, Math.Min(v.Length, 40))); break;
                    }
                }
                value = v.ToString().Replace("\"", "").Replace("<", "");
            }
            sb.Append(value).Append('"');
            i = end + 1;
        }
        return sb.ToString();
    }

    [Fact]
    public void Attribute_value_fuzz_only_throws_documented_exceptions()
    {
        var seeds = Directory.GetFiles(SvgGolden.SvgDir, "*.svg").Select(File.ReadAllText).ToList();
        int iterations = FuzzIterations(3000, out int seedOffset);
        var rng = new Random(424242 + seedOffset);
        var sw = Stopwatch.StartNew();
        int parsed = 0;
        for (int i = 0; i < iterations; i++)
        {
            var input = MutateValues(seeds[i % seeds.Count], rng);
            try
            {
                var doc = SvgDocument.Parse(input);
                var list = doc.ToDisplayList(new Rect(0, 0, 40, 40), new SvgRenderOptions { CurrentColor = Color.Black });
                using (SvgGolden.Render(list, 40, 40)) { }
                parsed++;
            }
            catch (SvgParseException)
            {
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException($"Iteration {i} threw {e.GetType().Name}: {e.Message}\n{e.StackTrace}\nInput:\n{input}");
            }
        }
        Assert.True(parsed > iterations * 5 / 6, $"parsed={parsed}");
        Assert.True(sw.Elapsed.TotalSeconds < 120 * Math.Max(1, iterations / 3000), sw.Elapsed.ToString());
    }

    [Fact]
    public void Byte_level_fuzz_only_throws_documented_exceptions()
    {
        var seeds = Directory.GetFiles(SvgGolden.SvgDir, "*.svg").Select(File.ReadAllBytes).ToList();
        int iterations = FuzzIterations(600, out int seedOffset);
        var rng = new Random(7 + seedOffset);
        for (int i = 0; i < iterations; i++)
        {
            var bytes = (byte[])seeds[i % seeds.Count].Clone();
            int flips = rng.Next(1, 8);
            for (int k = 0; k < flips; k++) bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
            try
            {
                var doc = SvgDocument.Parse(bytes);
                doc.ToDisplayList(new Rect(0, 0, 16, 16));
            }
            catch (SvgParseException)
            {
            }
        }
    }
}
