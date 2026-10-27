namespace Flagstaff.Compiler;

// Layout and universal props (applied by XamlGenerator.CommonAttrs / the grid containers in
// XamlGenerator.Layout.cs; context checks live in LayoutValidator).
public static partial class ElementSchema
{
    /// <summary>Allowed values of <c>align:</c>.</summary>
    internal static readonly string[] Alignments = ["left", "center", "right", "stretch"];

    [SchemaRegistration]
    private static void RegisterLayout()
    {
        // ── Universal: every keyword accepts these ──
        RegisterUniversal("width", Width);       // auto | 240 | * | 2*
        RegisterUniversal("span", Int);          // Grid.ColumnSpan inside row / cards
        RegisterUniversal("fill", Bare);         // takes the remaining space
        RegisterUniversal("align", OneOf(Alignments));
        RegisterUniversal("tooltip", Str);
        RegisterUniversal("name", Ident);        // x:Name

        // ── Keyword-specific ──
        Extend("row", new() { ["wrap"] = Bare });
        Extend("button", new() { ["variant"] = OneOf("primary", "danger"), ["icon"] = Ident });
        Extend("filePicker", new() { ["icon"] = Ident });
        foreach (var keyword in new[] { "textbox", "textarea", "number", "select" })
            Extend(keyword, new() { ["placeholder"] = Str });
        foreach (var keyword in new[] { "textbox", "textarea", "number", "datepicker", "select" })
            Extend(keyword, new() { ["hint"] = Str });
        foreach (var keyword in new[] { "textbox", "textarea" })
            Extend(keyword, new() { ["readonly"] = Bare });
        Extend("number", new() { ["min"] = Float, ["max"] = Float, ["step"] = Float });

        // heading "Title" | heading vm.Title | heading item.Title
        Reshape("heading", s => s with { AllowsBinding = true, AllowsItemBinding = true });
    }
}
