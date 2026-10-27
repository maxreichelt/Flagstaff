namespace Flagstaff.Compiler;

// Data-bound collections: list, empty, table/col/select upgrades (Stream C).
public static partial class ElementSchema
{
    /// <summary>Keywords that take a positional <c>item.X</c> inside a <c>list</c> row template.</summary>
    internal static readonly string[] ItemBindableKeywords =
    [
        "text", "stat", "line", "badge", "image", "progress",
        "textbox", "textarea", "number", "checkbox", "radio", "toggle", "datepicker",
    ];

    [SchemaRegistration]
    private static void RegisterData()
    {
        // list items: vm.X of T [selected: vm.Sel] [-> vm.OpenCommand(item)] { ...row template... }
        Register("list", children: true, arrow: true,
                 props: new() { ["items"] = Bind, ["selected"] = Bind });

        // empty "No rows yet."; — child of table/list, shown while the items collection is empty.
        Register("empty", label: true);

        // table ... -> vm.OpenCommand(item) (row click)
        Reshape("table", s => s with { AllowsArrow = true });

        // col "Total" bind: item.Total format: number(2) width: auto|N|"2*"
        Extend("col", new() { ["width"] = Width });

        // select options: ["Open", "Closed"] or options: 2019..2026 (shape checked by ItemTypeRegistry.Collect);
        // select items: vm.X of T display: item.Name
        Extend("select", new() { ["options"] = AnyValue, ["display"] = ItemBind });

        // Row templates reuse the ordinary elements with positional item.X bindings.
        foreach (var keyword in ItemBindableKeywords)
            Reshape(keyword, s => s with { AllowsItemBinding = true });
    }
}
