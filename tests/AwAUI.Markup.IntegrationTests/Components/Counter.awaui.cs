namespace AwAUI.Markup.IntegrationTests.Components;

// 代码后置文件：同一个 partial 类，不再声明基类（基类与 Props 胶水都由 .awaui 生成器产出）
public partial class Counter
{
    public string Label { get; set; } = "计数";

    public int Limit { get; set; } = 10;
}
