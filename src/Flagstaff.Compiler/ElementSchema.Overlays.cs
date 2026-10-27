namespace Flagstaff.Compiler;

// Overlays: dialog (generic modal), confirm (dialog sugar), toast (auto-dismiss).
// All three are hoisted into the page's FlagstaffOverlay layer (see XamlGenerator.Overlays.cs).
public static partial class ElementSchema
{
    [SchemaRegistration]
    private static void RegisterOverlays()
    {
        // dialog "Title" message: "..." when(vm.Flag) { ...form...; button ... -> ...; }
        Register("dialog", label: true, children: true, props: new() { ["message"] = Str });

        // confirm "Delete?" message: "..." when(vm.Flag) -> vm.DeleteCommand;  (auto Cancel/OK buttons)
        Reshape("confirm", s => s with { AllowsArrow = true });
        Extend("confirm", new() { ["confirmLabel"] = Str });

        // toast "Saved" when(vm.Flag) duration: 3000;  (ms; auto-dismiss)
        Extend("toast", new() { ["duration"] = Int });
    }
}
