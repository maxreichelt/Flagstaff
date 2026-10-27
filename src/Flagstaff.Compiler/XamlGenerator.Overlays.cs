namespace Flagstaff.Compiler;

// Overlays: alert (inline flow content), and the hoisted page-level layer: dialog, confirm, toast.
public sealed partial class XamlGenerator
{
    /// <summary>Keywords that are hoisted out of the flow into the page's <c>FlagstaffOverlay</c> layer.</summary>
    internal static IReadOnlySet<string> HoistedOverlayKeywords { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "toast", "confirm", "dialog" };

    /// <summary>
    /// True for <c>toast</c> / <c>confirm</c> / <c>dialog</c>: rendered in the overlay layer, so containers give
    /// them no row, column, slot or spacing (they only leave a marker comment in place).
    /// </summary>
    internal static bool IsHoisted(Element el) => HoistedOverlayKeywords.Contains(el.Keyword);

    /// <summary>While a dialog/confirm body is emitted: its <c>Cancel{Flag}</c> stem for <c>-> cancel</c> buttons.</summary>
    private string? _modalCancelStem;

    /// <summary>
    /// Diagnostics raised while generating the view (FS1020–FS1022). <see cref="FlagstaffCompiler"/>
    /// adds them to the compilation result.
    /// </summary>
    public List<Diagnostic> Diagnostics { get; } = [];

    /// <summary>One hoisted overlay and whether it was written inside a <c>list</c>/<c>table</c> template.</summary>
    internal sealed record HoistedOverlay(Element Element, bool InItemTemplate);

    /// <summary>
    /// Every <c>toast</c> / <c>confirm</c> / <c>dialog</c> in the view at any depth (including inside
    /// another dialog), in document order. Property-element values are walked too: an overlay is page-level by
    /// definition, so one written inside <c>Flyout: Flyout { toast "Hi" when(vm.T); }</c> or a view-level property
    /// element is hoisted like any other (its place in the flyout keeps only the marker comment). One inside a row
    /// template (a <c>list</c>/<c>table</c> body or row <c>ContextFlyout:</c>, or a generic <c>DataTemplate</c>) is
    /// marked <see cref="HoistedOverlay.InItemTemplate"/> (FS1020).
    /// </summary>
    internal static List<HoistedOverlay> CollectOverlays(ViewDecl view)
    {
        var result = new List<HoistedOverlay>();
        foreach (var prop in view.PropertyElements)
            CollectValue(prop.Value, inTemplate: false);
        foreach (var el in view.Body)
            Collect(el, inTemplate: false);
        return result;

        void Collect(Element el, bool inTemplate)
        {
            if (IsHoisted(el))
                result.Add(new HoistedOverlay(el, inTemplate));
            var isTemplate = el.IsGeneric && GenericValidator.ManifestTypeOf(el) is { } type
                             && ControlManifest.Default.DerivesFrom(type, "FrameworkTemplate");
            foreach (var prop in el.Props)
                CollectValue(prop.Value, inTemplate || isTemplate || IsRowPropertyElement(el, prop));
            if (el.Body is not BlockBody block)
                return;
            var childInTemplate = inTemplate || isTemplate || el.Keyword is "list" or "table" || IsGenericItemTemplate(el);
            foreach (var child in block.Children)
            {
                if (child is ChildElement ce)
                    Collect(ce.Element, childInTemplate);
            }
        }

        void CollectValue(PropValue value, bool inTemplate)
        {
            switch (value)
            {
                case ElementValue ev:
                    Collect(ev.Element, inTemplate);
                    break;
                case ElementsValue evs:
                    foreach (var e in evs.Elements)
                        Collect(e, inTemplate);
                    break;
            }
        }
    }

    /// <summary>
    /// The name of the generated method that hides a confirm dialog: <c>Cancel{Flag}</c>, e.g.
    /// <c>when(vm.IsDeleteVisible)</c> → <c>CancelIsDeleteVisible()</c> / <c>CancelIsDeleteVisibleCommand</c>.
    /// </summary>
    internal static string CancelCommandStem(string flag) => "Cancel" + flag;

    /// <summary>
    /// Page-level overlay layer. Called by <see cref="GenerateView"/> after the scrolling body, inside
    /// the root Grid. Renders every hoisted dialog/confirm (document order) and then the toast stack,
    /// so toasts sit above dialogs. Emits nothing when the view has no overlays.
    /// </summary>
    private void EmitOverlayLayer(ViewDecl view)
    {
        var emitted = new List<Element>();

        foreach (var (el, inTemplate) in CollectOverlays(view))
        {
            if (inTemplate)
            {
                Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1020",
                    $"'{el.Keyword}' cannot be inside a list or table template: overlays are page-level and shown by a view model flag",
                    el.Location,
                    $"move the {el.Keyword} out of the list/table and show it with a vm.X flag set by the row command"));
                continue;
            }

            if (view.Binding is null)
            {
                Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1021",
                    $"'{el.Keyword}' needs a view with bind vm: (its when(vm.Flag) lives on the view model)",
                    el.Location,
                    "add bind vm: MyViewModel; at the top of the view"));
            }
            else if (el.When is null)
            {
                Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error, "FS1021",
                    $"'{el.Keyword}' needs when(vm.Flag): the view model flag shows and hides it",
                    el.Location,
                    $"write {el.Keyword} \"...\" when(vm.Show{char.ToUpperInvariant(el.Keyword[0])}{el.Keyword[1..]}) ..."));
            }

            if (el.Keyword == "confirm")
                CheckConfirmShape(el);

            emitted.Add(el);
        }

        if (emitted.Count == 0)
            return;

        Line("<Grid x:Name=\"FlagstaffOverlay\" Grid.Row=\"0\" Grid.RowSpan=\"2\">");
        Indent();
        foreach (var el in emitted.Where(e => e.Keyword != "toast"))
            EmitModal(el);

        var toasts = emitted.Where(e => e.Keyword == "toast").ToList();
        if (toasts.Count > 0)
        {
            Line("<StackPanel HorizontalAlignment=\"Right\" VerticalAlignment=\"Bottom\" Margin=\"24\" Spacing=\"8\">");
            Indent();
            foreach (var el in toasts)
                EmitToastInfoBar(el);
            Dedent();
            Line("</StackPanel>");
        }
        Dedent();
        Line("</Grid>");
    }

    /// <summary>FS1022: a confirm uses either <c>-> target</c> (generated buttons) or its own buttons, not both or neither.</summary>
    private void CheckConfirmShape(Element el)
    {
        var hasButtons = ModalChildren(el).Any(c => c is ChildElement { Element.Keyword: "button" });
        var confirmLabel = el.Props.FirstOrDefault(p => p.Key == "confirmLabel");
        if (hasButtons && el.NavTarget is not null)
        {
            Diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1022",
                "'confirm' has both '->' and its own buttons; the arrow only applies to the generated Cancel/OK buttons",
                el.Location,
                "remove the '->' target, or remove the buttons from the { } block"));
        }
        else if (!hasButtons && el.NavTarget is null)
        {
            Diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1022",
                "'confirm' needs '-> vm.XCommand' (generated Cancel/OK buttons) or its own buttons in { }",
                el.Location,
                "write confirm \"Delete?\" when(vm.IsDeleteVisible) -> vm.DeleteCommand;"));
        }
        else if (hasButtons && confirmLabel is not null)
        {
            Diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error, "FS1022",
                "'confirmLabel:' only applies to the generated OK button; this confirm has its own buttons",
                confirmLabel.Location,
                "remove confirmLabel:, or replace the buttons with '-> vm.XCommand'"));
        }
    }

    private static IEnumerable<ElementOrText> ModalChildren(Element el) =>
        el.Body is BlockBody block ? block.Children : [];

    /// <summary>In-flow position of a hoisted overlay: a marker comment only (see <see cref="EmitOverlayLayer"/>).</summary>
    private void EmitHoistedMarker(Element el)
    {
        var label = Esc(GetLabel(el)).Replace("--", "- -");
        Line($"<!-- {el.Keyword} \"{label}\": rendered in FlagstaffOverlay -->");
    }

    private void EmitAlert(Element el)
    {
        var text = "";
        if (el.Body is BlockBody block)
        {
            var textContent = block.Children.OfType<TextContent>().FirstOrDefault();
            text = textContent?.Text ?? "";
        }
        var vis = Conditions.VisibilityAttr(el) + PassThroughAttrs(el) + EventAttrs(el);

        EmitRoot(el, "InfoBar", $" IsOpen=\"True\" Severity=\"{SeverityOf(el)}\" Message=\"{Esc(text)}\"{vis}", () =>
        {
            if (el.Body is BlockBody block2)
            {
                foreach (var child in block2.Children.OfType<ChildElement>())
                    EmitElement(child.Element);
            }
        });
    }

    private static string SeverityOf(Element el) => GetPropValue(el, "variant") switch
    {
        "warning" => "Warning",
        "danger" => "Error",
        "success" => "Success",
        _ => "Informational"
    };

    private void EmitToastInfoBar(Element el)
    {
        var label = GetLabel(el);
        // FS2003 (reported by ConditionCompiler) guarantees when() is a bare vm.Bool here.
        var isOpenPath = Conditions.BareBoolPath(el);
        var isOpenBinding = isOpenPath is not null
            ? $" IsOpen=\"{{x:Bind {isOpenPath}, Mode=TwoWay}}\""
            : " IsOpen=\"True\"";
        Line($"<InfoBar{isOpenBinding} Severity=\"{SeverityOf(el)}\" Message=\"{Esc(label)}\" IsClosable=\"True\"{PassThroughAttrs(el)}{EventAttrs(el)} />");
    }

    /// <summary>
    /// A modal for <c>dialog</c> and <c>confirm</c>: full-size Grid bound to the flag, backdrop, centered card
    /// with title, optional message, the non-button children stacked, and the buttons right-aligned.
    /// A confirm without buttons of its own gets a generated Cancel button (<see cref="CancelCommandStem"/>)
    /// and an OK / <c>confirmLabel:</c> button bound to its <c>-&gt;</c> target.
    /// </summary>
    private void EmitModal(Element el)
    {
        var label = GetLabel(el);
        var message = GetPropValue(el, "message") ?? "";
        var children = ModalChildren(el).ToList();
        var formChildren = children.Where(c => c is not ChildElement { Element.Keyword: "button" }).ToList();
        var buttons = children.OfType<ChildElement>().Where(c => c.Element.Keyword == "button").Select(c => c.Element).ToList();
        var generateButtons = el.Keyword == "confirm" && buttons.Count == 0;
        var outerCancelStem = _modalCancelStem;
        _modalCancelStem = UsesCancelTarget(el) ? EnsureCancelCommand(el) : null;

        Line($"<Grid{Conditions.VisibilityAttr(el)}{PassThroughAttrs(el)}{EventAttrs(el)}>");
        Indent();
        Line("<Border Style=\"{StaticResource FlagstaffDialogBackdrop}\" />");
        Line("<Border Style=\"{StaticResource FlagstaffDialogCard}\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\">");
        Indent();
        Line("<StackPanel Spacing=\"16\">");
        Indent();
        if (!string.IsNullOrEmpty(label))
            Line($"<TextBlock Text=\"{Esc(label)}\" Style=\"{{StaticResource SubtitleTextBlockStyle}}\" />");
        if (!string.IsNullOrEmpty(message))
            Line($"<TextBlock Text=\"{Esc(message)}\" TextWrapping=\"Wrap\" />");

        if (formChildren.Count > 0)
        {
            Line("<StackPanel Spacing=\"8\">");
            Indent();
            foreach (var child in formChildren)
            {
                if (child is ChildElement ce)
                    EmitElement(ce.Element);
                else if (child is TextContent tc)
                    Line($"<TextBlock Text=\"{Esc(tc.Text)}\" TextWrapping=\"Wrap\" />");
            }
            Dedent();
            Line("</StackPanel>");
        }

        if (buttons.Count > 0 || generateButtons)
        {
            Line("<StackPanel Orientation=\"Horizontal\" Spacing=\"8\" HorizontalAlignment=\"Right\">");
            Indent();
            if (generateButtons)
                EmitConfirmButtons(el);
            else
                foreach (var button in buttons)
                    EmitElement(button);
            Dedent();
            Line("</StackPanel>");
        }

        Dedent();
        Line("</StackPanel>");
        Dedent();
        Line("</Border>");
        Dedent();
        Line("</Grid>");
        _modalCancelStem = outerCancelStem;
    }

    /// <summary>Does any element in the modal's block (outside nested overlays) use <c>-> cancel</c>?</summary>
    private static bool UsesCancelTarget(Element modal)
    {
        return ModalChildren(modal).OfType<ChildElement>().Any(c => Uses(c.Element));

        static bool Uses(Element el) =>
            !IsHoisted(el) && (el.NavTarget == CancelTarget || el.Events.Any(e => e.Target == CancelTarget)
                               || (el.Body is BlockBody b && b.Children.OfType<ChildElement>().Any(c => Uses(c.Element))));
    }

    /// <summary>
    /// Adds <c>[RelayCommand] private void Cancel{Flag}() => {Flag} = false;</c> to the view model (once per flag;
    /// a user command of that name wins) and returns the stem, or null when the modal has no bare <c>when(vm.Flag)</c>
    /// (FS1021 / FS2003 are reported elsewhere).
    /// </summary>
    private string? EnsureCancelCommand(Element modal)
    {
        var flagPath = Conditions.BareBoolPath(modal);
        if (flagPath is null || !flagPath.StartsWith("ViewModel.", StringComparison.Ordinal))
            return null;
        var flag = flagPath["ViewModel.".Length..];
        var stem = CancelCommandStem(flag);
        // A user command with the same name (-> vm.CancelIsDeleteVisibleCommand elsewhere) wins.
        var userDefined = _inferredMembers?.ContainsKey(stem + "Command") ?? false;
        var member = $"    [RelayCommand] private void {stem}() => {flag} = false;";
        if (!userDefined && !ExtraViewModelMembers.Contains(member))
            ExtraViewModelMembers.Add(member);
        return stem;
    }

    /// <summary>
    /// Generated buttons, in WinUI ContentDialog order: the confirming button (runs the arrow target;
    /// <c>confirmLabel:</c> or "OK") first, then Cancel (hides the confirm via <see cref="CancelCommandStem"/>).
    /// </summary>
    private void EmitConfirmButtons(Element el)
    {
        var okLabel = GetPropValue(el, "confirmLabel") ?? "OK";
        var style = GetPropValue(el, "variant") == "danger" && XamlLinter.IsKnownResource("FlagstaffDangerButton")
            ? "FlagstaffDangerButton"
            : "AccentButtonStyle";
        var styleAttr = $" Style=\"{{StaticResource {style}}}\"";
        var target = el.NavTarget;
        if (target is not null && target.StartsWith("vm.", StringComparison.Ordinal))
            Line($"<Button Content=\"{Esc(okLabel)}\"{styleAttr} Command=\"{{x:Bind {BindPath(TypeInference.CommandPath(target))}, Mode=OneWay}}\" />");
        else if (target is not null)
            Line($"<Button Content=\"{Esc(okLabel)}\"{styleAttr} Tag=\"{Esc(target)}\" Click=\"OnNavigateButtonClick\" />");
        else
            Line($"<Button Content=\"{Esc(okLabel)}\"{styleAttr} />");

        if (EnsureCancelCommand(el) is { } stem)
        {
            Line($"<Button Content=\"Cancel\" Command=\"{{x:Bind ViewModel.{stem}Command, Mode=OneWay}}\" />");
        }
        else
        {
            // when() is missing or not a bare vm.Bool: FS1021 / FS2003 is already reported.
            Line("<Button Content=\"Cancel\" />");
        }
    }
}
