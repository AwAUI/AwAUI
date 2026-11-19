using AwAUI.UI;

namespace AwAUI.Markup.IntegrationTests.Components;

public sealed record TodoEntry(int Id, string Title);

/// <summary>手写的 C# 组件，被 .awaui 引用（Props 胶水来自 C# 胶水生成器）。</summary>
public partial class Badge : Component
{
    public int Count { get; set; }

    protected override Node Build() => new Span { Class = "badge", Children = { Count.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
}

/// <summary>手写的 C# 组件，引用 .awaui 组件（.awaui 组件就是普通的 C# 类）。</summary>
public partial class Shell : Component
{
    protected override Node Build() =>
        new Div { Class = "shell", Children = { new Counter { Step = 5, Label = "外壳", Limit = 7 }, new TodoList() } };
}
