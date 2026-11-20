# AwAUI.Shadcn

New York v4 components for AwAUI. The library provides the S1 component families, including text editors, selection, navigation, overlays, tables, and layout. See the [component list](../../design/23-component-library.md#组件清单). Add a project or package reference to `AwAUI.Shadcn` and use the types from C# or `.awaui`. The library registers its generated CSS when loaded; consumers do not need to scan the library for utility classes. Call `ShadcnTheme.Register()` before mounting the root if the application also declares cascade layers. This makes the package stylesheet available before the first component stylesheet. To use additional Tailwind utilities in a caller `Class`, generate those utilities in the application.

```razor
@using AwAUI.Shadcn

<AwAUI.Shadcn.Dialog>
    <AwAUI.Shadcn.DialogTrigger AsChild="true">
        <AwAUI.Shadcn.Button Variant="ButtonVariant.Outline">Edit profile</AwAUI.Shadcn.Button>
    </AwAUI.Shadcn.DialogTrigger>
    <AwAUI.Shadcn.DialogContent>
        <AwAUI.Shadcn.DialogHeader>
            <AwAUI.Shadcn.DialogTitle>Edit profile</AwAUI.Shadcn.DialogTitle>
            <AwAUI.Shadcn.DialogDescription>Update your display name.</AwAUI.Shadcn.DialogDescription>
        </AwAUI.Shadcn.DialogHeader>
    </AwAUI.Shadcn.DialogContent>
</AwAUI.Shadcn.Dialog>
```

For single-line fields, bind `Input` with `@bind-Value` or `Value` and `ValueChanged`. It forwards input, change, and submit events to the built-in editor. Set `Label.For` to the input's ID to focus it on click, and set `aria-labelledby` on the input to reference the label's ID:

```razor
<AwAUI.Shadcn.Label Id="workspace-label" For="workspace">Workspace name</AwAUI.Shadcn.Label>
<AwAUI.Shadcn.Input Id="workspace" @bind-Value="workspaceName" aria-labelledby="workspace-label" />
```

When there is no `For`, the label uses the first nested input, checkbox, or switch. Clicking a checkbox or switch label also toggles the associated control. Use `aria-invalid` and `aria-describedby` for validation. `Input` wraps the single-line AwAUI editor. Use `Textarea` with the same value binding for multiline text. `Badge.AsChild` uses the Controls Slot rules for supported children.

Use `@bind-Checked` for a controlled `Checkbox` or `Switch`, or `DefaultChecked` for an uncontrolled one. `Disabled` prevents activation. Set `Indeterminate` on a checkbox to display a mixed state; handle `IndeterminateChanged` to clear it when the user clicks. The switch has default and small sizes. `Separator` is horizontal and decorative by default; set `Decorative="false"` for separator semantics.

For a submenu, compose `DropdownMenuSub`, `DropdownMenuSubTrigger`, and `DropdownMenuSubContent` inside `DropdownMenuContent`. The gallery demonstrates this arrangement.

The default popup motion uses a short fade and scale with an entrance offset based on the placed side. Dialogs fade and scale, sheets slide along their edge, and accordions expand to the measured content height. The gallery can save opening and closing frames for comparison, including with reduced motion.

The default palette is neutral. Call `ShadcnTheme.SetDark(root, true)` for window-wide dark mode, including portaled dialogs. Pass `false` to restore light mode. The `.dark` class also scopes the neutral dark palette to a regular element subtree; it does not follow a portal into a different part of the tree.

See [ShadcnGallery](../../samples/ShadcnGallery) for the interactive example, and `THIRD-PARTY-NOTICES.txt` for the source license.
