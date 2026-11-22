using AwAUI.Svg;

namespace DeclarativeDemo.Components;

// A small presentational component: every input is a plain public property (a Prop in .awaui terms).
public partial class StatCard
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string Delta { get; set; } = "";
    public string Hint { get; set; } = "";
    public bool Positive { get; set; } = true;
    public SvgDocument? Icon { get; set; }
}
