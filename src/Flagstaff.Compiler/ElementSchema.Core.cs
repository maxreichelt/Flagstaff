namespace Flagstaff.Compiler;

// Core keywords. Positional "label" and vm.Binding are flags (label:/binding:), not props.
// format:, when(), enabled(), of T and -> are clauses parsed by the Parser, not props.
public static partial class ElementSchema
{
    /// <summary>The severity enum shared by alert, toast, stat and confirm.</summary>
    internal static readonly string[] Severities = ["warning", "danger", "info", "success"];

    private static void RegisterCore()
    {
        // ── Layout ──
        Register("toolbar", children: true);
        Register("spacer");
        Register("separator");
        Register("row", children: true, props: new() { ["cols"] = Int });
        Register("column", children: true);
        Register("stack", children: true);
        Register("split", children: true);
        Register("group", label: true, children: true);
        Register("card", label: true, children: true, props: new() { ["icon"] = Ident });
        Register("cards", children: true, props: new() { ["cols"] = Int });

        // ── Display ──
        Register("heading", label: true, props: new() { ["icon"] = Ident });
        Register("text", label: true, binding: true, props: new() { ["bold"] = Bare });
        Register("stat", label: true, binding: true, props: new() { ["variant"] = OneOf(Severities) });
        Register("summary", children: true);
        Register("line", label: true, binding: true, props: new() { ["bold"] = Bare });
        Register("badge", label: true, binding: true);
        Register("image", binding: true, props: new() { ["src"] = Str });
        Register("progress", binding: true);
        Register("spinner");

        // ── Inputs ──
        Register("button", label: true, arrow: true);
        Register("textbox", label: true, binding: true);
        Register("textarea", label: true, binding: true);
        Register("number", label: true, binding: true);
        Register("select", label: true, binding: true, props: new() { ["items"] = Bind, ["options"] = IntRange });
        Register("checkbox", label: true, binding: true);
        Register("radio", label: true, binding: true);
        Register("toggle", label: true, binding: true);
        Register("datepicker", label: true, binding: true);
        Register("filePicker", label: true, arrow: true);
        Register("link", label: true, arrow: true, props: new() { ["icon"] = Ident });

        // ── Data ──
        Register("table", children: true, props: new() { ["items"] = Bind, ["selected"] = Bind });
        Register("col", label: true, itemBinding: true, props: new() { ["bind"] = ItemBind });

        // ── Overlays ──
        Register("alert", children: true, props: new() { ["variant"] = OneOf(Severities) });
        Register("toast", label: true, props: new() { ["variant"] = OneOf(Severities) });
        Register("confirm", label: true, children: true, props: new() { ["message"] = Str, ["variant"] = OneOf(Severities) });

        // ── Escape hatch (body is raw XAML) ──
        Register("xaml", children: true);

        // ── Universal: x:Uid (localization) and x:Load (deferred loading), applied by XamlGenerator.CommonAttrs ──
        RegisterUniversal("uid", Str);
        // load: load(vm.X) → x:Load="{x:Bind ViewModel.X, Mode=OneWay}" (vm.X infers bool). Any other value is FS1032
        // (GenericValidator.ValidateResources).
        RegisterUniversal("load", AnyValue);

        // ── Universal: x:Key on a resource-dictionary entry (Page.Resources: { Style key: X ... }), written by
        // XamlGenerator.EmitGeneric. Outside a resources block, or on a keyword element, it is FS1044; a key repeated in one
        // dictionary is FS1045 (GenericValidator.ValidateResources). ──
        RegisterUniversal("key", Ident);
    }
}
