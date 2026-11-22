using System.Globalization;
using System.Text;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration;

/// <summary>
/// Shadcn S1 的确定性语义树快照：每行一个元素或文本节点，包含标签、角色、ARIA 属性、文本、状态与 tabindex。
/// 快照在 <c>tests/AwAUI.Controls.UnitTests/Integration/Snapshots</c>；设置 <c>AWAUI_UPDATE_SNAPSHOTS=1</c> 重写
/// （人工审核后再提交）。生成的 ID 按树中首次出现的顺序归一化为 <c>@1</c>、<c>@2</c> ...，因此测试运行顺序不影响快照。
/// </summary>
public class ShadcnSemanticSnapshotTests
{
    static ShadcnSemanticSnapshotTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static void AssertSnapshot(string name, Func<Node> render)
    {
        var h = Harness(render);
        h.Frame();
        SnapshotFile.AssertMatches(name, SemanticTreeSerializer.Serialize(h.Root.RootElement));
    }

    [Fact]
    public void Button() => AssertSnapshot("button", () => new Button
    {
        Rest = new AttributeDictionary { ["id"] = "save" },
        Variant = ButtonVariant.Secondary,
        Size = ButtonSize.Sm,
        Children = [new UIText("Save")],
    });

    [Fact]
    public void Checkbox() => AssertSnapshot("checkbox", () => new Checkbox
    {
        Id = "updates",
        DefaultChecked = true,
    });

    [Fact]
    public void Switch() => AssertSnapshot("switch", () => new Switch
    {
        Id = "compact",
        DefaultChecked = true,
    });

    [Fact]
    public void Tabs() => AssertSnapshot("tabs", () => new Tabs
    {
        DefaultValue = "one",
        Children =
        [
            new TabsList
            {
                Children =
                [
                    new TabsTrigger { Value = "one", Children = [new UIText("One")] },
                    new TabsTrigger { Value = "two", Children = [new UIText("Two")] },
                ],
            },
            new TabsContent { Value = "one", Children = [new UIText("First panel")] },
            new TabsContent { Value = "two", Children = [new UIText("Second panel")] },
        ],
    });

    [Fact]
    public void Select_closed() => AssertSnapshot("select-closed", () => new Select
    {
        DefaultValue = "projects",
        Children =
        [
            new SelectTrigger { Children = [new SelectValue { Placeholder = "Choose" }] },
            new SelectContent
            {
                Children =
                [
                    new SelectItem { Value = "projects", Children = [new UIText("Projects")] },
                    new SelectItem { Value = "activity", Children = [new UIText("Activity")] },
                ],
            },
        ],
    });

    [Fact]
    public void Select_open() => AssertSnapshot("select-open", () => new Select
    {
        DefaultValue = "projects",
        DefaultOpen = true,
        Children =
        [
            new SelectTrigger { Children = [new SelectValue { Placeholder = "Choose" }] },
            new SelectContent
            {
                Children =
                [
                    new SelectItem { Value = "projects", Children = [new UIText("Projects")] },
                    new SelectItem { Value = "activity", Children = [new UIText("Activity")] },
                ],
            },
        ],
    });

    [Fact]
    public void Dialog_open() => AssertSnapshot("dialog-open", () => new Dialog
    {
        DefaultOpen = true,
        Children =
        [
            new DialogTrigger { Children = [new UIText("Edit profile")] },
            new DialogContent
            {
                Children =
                [
                    new DialogHeader
                    {
                        Children =
                        [
                            new DialogTitle { Children = [new UIText("Edit profile")] },
                            new DialogDescription { Children = [new UIText("Change the name shown on the card.")] },
                        ],
                    },
                    new Input { Id = "dialog-name", Placeholder = "Ada Lovelace" },
                    new DialogFooter
                    {
                        Children =
                        [
                            new DialogClose { Children = [new UIText("Cancel")] },
                            new Button { Children = [new UIText("Save changes")] },
                        ],
                    },
                ],
            },
        ],
    });

    [Fact]
    public void Accordion() => AssertSnapshot("accordion", () => new Accordion
    {
        DefaultValue = "project",
        Collapsible = true,
        Children =
        [
            new AccordionItem
            {
                Value = "project",
                Children =
                [
                    new AccordionTrigger { Children = [new UIText("Project")] },
                    new AccordionContent { Children = [new UIText("Project details")] },
                ],
            },
            new AccordionItem
            {
                Value = "activity",
                Children =
                [
                    new AccordionTrigger { Children = [new UIText("Activity")] },
                    new AccordionContent { Children = [new UIText("Recent changes")] },
                ],
            },
        ],
    });

    [Fact]
    public void Table() => AssertSnapshot("table", () => new Table
    {
        Columns = "3",
        Children =
        [
            new TableHeader
            {
                Children =
                [
                    new TableRow
                    {
                        Children =
                        [
                            new TableHead { Children = [new UIText("Name")] },
                            new TableHead { Children = [new UIText("Role")] },
                            new TableHead { Children = [new UIText("Status")] },
                        ],
                    },
                ],
            },
            new TableBody
            {
                Children =
                [
                    new TableRow
                    {
                        Children =
                        [
                            new TableCell { Children = [new UIText("Ada Lovelace")] },
                            new TableCell { Children = [new UIText("Owner")] },
                            new TableCell { Children = [new UIText("Active")] },
                        ],
                    },
                ],
            },
        ],
    });

    [Fact]
    public void Input_with_invalid_state() => AssertSnapshot("input-invalid", () => new Div
    {
        Children =
        [
            new Label { For = "email", Children = [new UIText("Email")] },
            new Input
            {
                Id = "email",
                Value = "not-an-email",
                Placeholder = "ada@example.com",
                Rest = new AttributeDictionary
                {
                    ["aria-invalid"] = "true",
                    ["aria-describedby"] = "email-error",
                },
            },
            new UIText { Id = "email-error", Role = Role.Alert, Content = "Enter a valid email address." },
        ],
    });
}

/// <summary>
/// 元素树的稳定文本表示。属性按名称排序；只序列化影响语义的属性（id、role、aria-*、data-*、for、tabindex 与表单值），
/// 跳过 class / style。生成 ID 归一化为树内序号，避免测试执行顺序影响快照。
/// </summary>
static class SemanticTreeSerializer
{
    static readonly string[] s_reflectedNames =
    {
        "role", "aria-label", "tabindex", "for", "type", "value", "placeholder", "disabled", "readonly", "required",
        "src", "alt", "href", "name", "min", "max", "step",
    };

    static readonly HashSet<string> s_idReferenceNames = new(StringComparer.Ordinal)
    {
        "for", "aria-controls", "aria-describedby", "aria-labelledby", "aria-owns", "aria-activedescendant",
    };

    public static string Serialize(Element root)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        AssignIds(root, ids);
        var sb = new StringBuilder();
        Append(root, 0, ids, sb);
        return sb.ToString();
    }

    static void AssignIds(Element element, Dictionary<string, string> ids)
    {
        if (element.Id is { Length: > 0 } id && !ids.ContainsKey(id))
            ids[id] = "@" + ids.Count.ToString(CultureInfo.InvariantCulture);
        // 被引用的 ID 可能不在当前树里（例如收起的手风琴面板），也预先分配稳定标记，
        // 否则生成 ID 里的全局计数器会泄漏到快照。
        foreach (var pair in element.Attributes)
        {
            if (!s_idReferenceNames.Contains(pair.Key)) continue;
            foreach (var part in pair.Value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (!ids.ContainsKey(part))
                    ids[part] = "@" + ids.Count.ToString(CultureInfo.InvariantCulture);
        }
        for (int i = 0; i < element.Children.Count; i++) AssignIds(element.Children[i], ids);
    }

    static void Append(Element element, int depth, Dictionary<string, string> ids, StringBuilder sb)
    {
        sb.Append(' ', depth * 2);
        if (element is TextNodeElement textNode)
        {
            sb.Append("#text ").Append(Quote(textNode.Text)).Append('\n');
            return;
        }

        sb.Append(element.Tag);

        var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in element.Attributes)
        {
            if (pair.Key is "class" or "style" || pair.Key.StartsWith("on", StringComparison.Ordinal)) continue;
            attributes[pair.Key] = Normalize(pair.Key, pair.Value, ids);
        }
        foreach (var name in s_reflectedNames)
        {
            if (attributes.ContainsKey(name)) continue;
            var value = name == "tabindex"
                ? element.TabIndex?.ToString(CultureInfo.InvariantCulture)
                : element.GetAttribute(name);
            if (value is not null) attributes[name] = Normalize(name, value, ids);
        }
        if (element.Id is { Length: > 0 } id && !attributes.ContainsKey("id"))
            attributes["id"] = Normalize("id", id, ids);
        if (element.Role != Role.None && !attributes.ContainsKey("role") && element.GetAttribute("role") is { } role)
            attributes["role"] = Normalize("role", role, ids);

        var state = State(element);
        foreach (var pair in attributes) sb.Append(' ').Append(pair.Key).Append('=').Append(Quote(pair.Value));
        if (state.Length > 0) sb.Append(" state=").Append(state);
        if (element is TextElement { Content: { Length: > 0 } content })
            sb.Append(" text=").Append(Quote(content));
        sb.Append('\n');

        for (int i = 0; i < element.Children.Count; i++) Append(element.Children[i], depth + 1, ids, sb);
    }

    static string State(Element element)
    {
        var state = new List<string>(4);
        if (element.IsDisabled || string.Equals(element.GetAttribute("aria-disabled"), "true", StringComparison.Ordinal))
            state.Add("disabled");
        var checkedState = element.GetAttribute("aria-checked");
        if (string.Equals(checkedState, "true", StringComparison.Ordinal)) state.Add("checked");
        else if (string.Equals(checkedState, "mixed", StringComparison.Ordinal)) state.Add("mixed");
        if (string.Equals(element.GetAttribute("aria-expanded"), "true", StringComparison.Ordinal)) state.Add("expanded");
        if (string.Equals(element.GetAttribute("aria-pressed"), "true", StringComparison.Ordinal)) state.Add("pressed");
        if (string.Equals(element.GetAttribute("aria-selected"), "true", StringComparison.Ordinal)) state.Add("selected");
        return string.Join(",", state);
    }

    static string Normalize(string name, string value, Dictionary<string, string> ids)
    {
        if (string.Equals(name, "id", StringComparison.Ordinal))
            return ids.TryGetValue(value, out var token) ? token : value;
        if (!s_idReferenceNames.Contains(name)) return value;
        var parts = value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
            if (ids.TryGetValue(parts[i], out var token)) parts[i] = token;
        return string.Join(" ", parts);
    }

    static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary>语义树快照文件的读写与差异报告。</summary>
static class SnapshotFile
{
    static readonly string s_snapshotDirectory = Path.Combine(
        GoldenImage.RepoRoot, "tests", "AwAUI.Controls.UnitTests", "Integration", "Snapshots");
    static readonly string s_outputDirectory = Path.Combine(AppContext.BaseDirectory, "SnapshotsOutput");

    public static void AssertMatches(string name, string actual)
    {
        Directory.CreateDirectory(s_snapshotDirectory);
        var path = Path.Combine(s_snapshotDirectory, name + ".txt");
        if (Environment.GetEnvironmentVariable("AWAUI_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        Directory.CreateDirectory(s_outputDirectory);
        var actualPath = Path.Combine(s_outputDirectory, name + ".actual.txt");
        if (!File.Exists(path))
        {
            File.WriteAllText(actualPath, actual);
            Assert.Fail(
                $"Semantic snapshot '{name}.txt' is missing. Current tree: {actualPath}. " +
                "Review it and run with AWAUI_UPDATE_SNAPSHOTS=1 to create the snapshot.");
            return;
        }

        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        if (string.Equals(expected, actual, StringComparison.Ordinal)) return;

        File.WriteAllText(actualPath, actual);
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        int line = 0;
        int count = Math.Min(expectedLines.Length, actualLines.Length);
        while (line < count && string.Equals(expectedLines[line], actualLines[line], StringComparison.Ordinal)) line++;
        string expectedLine = line < expectedLines.Length ? expectedLines[line] : "<missing>";
        string actualLine = line < actualLines.Length ? actualLines[line] : "<missing>";
        Assert.Fail(
            $"Semantic snapshot '{name}.txt' differs at line {line + 1}. Expected: {expectedLine} Actual: {actualLine}. " +
            $"Expected file: {path}. Actual file: {actualPath}. Review the change and run with AWAUI_UPDATE_SNAPSHOTS=1.");
    }
}
