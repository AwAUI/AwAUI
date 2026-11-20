using AwAUI.Controls.Primitives;
using AwAUI.UI;
using DialogPrimitive = AwAUI.Controls.Primitives.Dialog;

namespace AwAUI.Shadcn;

/// <summary>Places custom dialog parts, such as a styled overlay and content, in a window overlay layer.</summary>
public sealed partial class DialogPortal : Component
{
    public int ZOrder { get; set; }

    public NodeList? Children { get; set; }

    internal readonly struct Placement(OpenableShared owner)
    {
        internal OpenableShared Owner { get; } = owner;
    }

    protected override Node Build() => new DialogPrimitive.Portal
    {
        ZOrder = ZOrder,
        Children = [Provide(new Placement(Use<DialogPrimitive.Context>().Shared), new Fragment(Children ?? new NodeList()))],
    };
}
