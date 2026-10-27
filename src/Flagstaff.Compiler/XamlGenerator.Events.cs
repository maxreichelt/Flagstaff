using System.Globalization;
using System.Text;

namespace Flagstaff.Compiler;

// `on Event -> target;` clauses (Element.Events): an `Event="OnEvent_n"` attribute on the element's tag and a typed
// code-behind handler that runs the command, navigates, or cancels.
// Validation (FS1034 unknown event, FS1043 duplicate, FS2015 bad target, FS1024 cancel) is GenericValidator.ValidateEvents;
// command inference (and FS2007 for (item)) is TypeInference. This file assumes a valid tree.
public sealed partial class XamlGenerator
{
    private int _eventHandlerCount;

    /// <summary>Elements whose event attributes were already rendered (a second call reuses them: no second handler).</summary>
    private readonly Dictionary<Element, string> _eventAttrs = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The event attributes for an element's root tag: one <c>Event="OnEvent_n"</c> per <c>on</c> clause (n is 1-based
    /// per view, in emission order), with the handler added to <see cref="ExtraCodeBehindMembers"/>
    /// (<see cref="EventHandler"/>). <c>on ItemClick</c> on a ListView/GridView also sets <c>IsItemClickEnabled="True"</c>
    /// unless the element sets it. Appended by <see cref="CommonAttrs"/>; the table/list/alert/overlay emitters, which do
    /// not call it, append it next to <see cref="PassThroughAttrs"/>.
    /// </summary>
    private string EventAttrs(Element el)
    {
        if (el.Events.Count == 0)
            return "";
        if (_eventAttrs.TryGetValue(el, out var cached))
            return cached;

        var type = GenericValidator.ManifestTypeOf(el);
        var sb = new StringBuilder();
        foreach (var evt in el.Events)
        {
            var name = "OnEvent_" + (++_eventHandlerCount).ToString(CultureInfo.InvariantCulture);
            ExtraCodeBehindMembers.Add(EventHandler(name, el, evt, EventArgsType(el, evt.EventName)));
            sb.Append($" {evt.EventName}=\"{name}\"");
            if (evt.EventName == "ItemClick" && NeedsItemClickEnabled(el, type))
                sb.Append(" IsItemClickEnabled=\"True\"");
        }
        var attrs = sb.ToString();
        _eventAttrs[el] = attrs;
        return attrs;
    }

    /// <summary>
    /// The handler's second parameter type: the manifest's args type for the event on the element's type (generic type, or
    /// a keyword's <see cref="KeywordSchema.RootType"/>), already a full name (<c>Microsoft.UI.Xaml.RoutedEventArgs</c>,
    /// <c>Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs</c>, ...). A third-party element, which the manifest does
    /// not know, gets <c>object</c> (a handler with an <c>object</c> parameter binds any event delegate).
    /// </summary>
    internal static string EventArgsType(Element el, string eventName) =>
        GenericValidator.ManifestTypeOf(el) is { } type && Manifest.TryGetEvent(type, eventName, out var evt)
            ? evt.ArgsTypeName
            : "object";

    /// <summary>Where <c>(item)</c> comes from for an event on the element itself (not its row).</summary>
    internal enum EventItemSource { None, Selection, ClickedItem }

    /// <summary>
    /// <c>on SelectionChanged -> vm.XCommand(item)</c> on a Selector with <c>of T</c> (<c>select</c>, <c>list</c>,
    /// <c>table</c>, a generic <c>ListView ... of T</c>) passes the newly selected item (<c>e.AddedItems[0]</c>);
    /// <c>on ItemClick -> ...(item)</c> passes <c>e.ClickedItem</c>. Any other <c>(item)</c> is the row of the enclosing
    /// table/list template (the element's <c>DataContext</c>). Shared with <see cref="TypeInference"/>, which types the
    /// command parameter accordingly.
    /// </summary>
    internal static EventItemSource ItemSourceOf(Element el, EventBinding evt)
    {
        if (evt.Arg != "item" || el.OfType is null)
            return EventItemSource.None;
        return EventArgsType(el, evt.EventName) switch
        {
            "Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs" => EventItemSource.Selection,
            "Microsoft.UI.Xaml.Controls.ItemClickEventArgs" => EventItemSource.ClickedItem,
            _ => EventItemSource.None,
        };
    }

    /// <summary>The event an element's own arrow already wires (FS1043 when an <c>on</c> clause repeats it), or null.</summary>
    internal static string? ArrowEvent(Element el)
    {
        if (el.NavTarget is not { } target)
            return null;
        if (!el.IsGeneric && el.Keyword is "table" or "list")
            return "ItemClick"; // ItemClickAttrs
        var navigates = !target.StartsWith("vm.", StringComparison.Ordinal) && target != CancelTarget;
        return navigates && (el.IsGeneric || el.Keyword is "button" or "link" or "filePicker") ? "Click" : null;
    }

    private static bool NeedsItemClickEnabled(Element el, ManifestType? type) =>
        type is not null && GenericValidator.ResolveProperty(type, "IsItemClickEnabled") is not null
        && el.Props.All(p => p.Key != "IsItemClickEnabled") && ArrowEvent(el) != "ItemClick";

    /// <summary>
    /// One handler per <c>on</c> clause (body by target):
    /// <code>
    /// private void OnEvent_n(object sender, Args e)
    /// {
    ///     if (ViewModel.SaveCommand.CanExecute(null)) ViewModel.SaveCommand.Execute(null);        // -> vm.SaveCommand
    ///     if (sender is FrameworkElement { DataContext: ViewModels.T item } &amp;&amp; ...) ...Execute(item); // (item) in a row
    ///     if (e.AddedItems.Count &gt; 0 &amp;&amp; e.AddedItems[0] is ViewModels.T item &amp;&amp; ...) ...;      // SelectionChanged (item)
    ///     if (e.ClickedItem is ViewModels.T item &amp;&amp; ...) ...;                                    // ItemClick (item)
    ///     Frame.Navigate(typeof(View));                                                      // -> navigate(View)
    ///     ViewModel.CancelIsOpenCommand.Execute(null);                                       // -> cancel (in a dialog)
    /// }
    /// </code>
    /// </summary>
    private string EventHandler(string name, Element el, EventBinding evt, string args)
    {
        var nl = Environment.NewLine;
        return $"    private void {name}(object sender, {args} e){nl}    {{{nl}        {EventHandlerBody(el, evt)}{nl}    }}";
    }

    private string EventHandlerBody(Element el, EventBinding evt)
    {
        var target = evt.Target;
        if (target == CancelTarget)
        {
            return _modalCancelStem is { } stem
                ? $"ViewModel.{stem}Command.Execute(null);"
                : "// -> cancel: no enclosing dialog/confirm with a bare when(vm.Flag) (FS1021 / FS2003 / FS1024)";
        }
        if (!target.StartsWith("vm.", StringComparison.Ordinal))
            return $"Frame.Navigate(typeof({target}));";

        var commandPath = TypeInference.CommandPath(target);
        var command = BindPath(commandPath);
        if (evt.Arg != "item")
            return $"if ({command}.CanExecute(null)) {command}.Execute(null);";

        var parameter = _inferredMembers is not null
                        && _inferredMembers.TryGetValue(commandPath["vm.".Length..], out var member)
                        && member.ParameterType is { } t
            ? $"ViewModels.{t}"
            : "object";
        var test = ItemSourceOf(el, evt) switch
        {
            EventItemSource.Selection => $"e.AddedItems.Count > 0 && e.AddedItems[0] is {parameter} item",
            EventItemSource.ClickedItem => $"e.ClickedItem is {parameter} item",
            _ => $"sender is Microsoft.UI.Xaml.FrameworkElement {{ DataContext: {parameter} item }}",
        };
        return $"if ({test} && {command}.CanExecute(item)) {command}.Execute(item);";
    }

    /// <summary>
    /// FS1033 for <c>on</c> clauses no emitter wrote (a <c>spacer</c> that a toolbar/row/split consumes structurally).
    /// Keywords without a root tag (<c>col</c>, <c>empty</c>) are FS1034 in validation already. Called at the end of
    /// <see cref="GenerateView"/>.
    /// </summary>
    private void ReportUnemittedEvents(ViewDecl view)
    {
        foreach (var p in view.PropertyElements)
            Walk(p.Value);
        foreach (var el in view.Body)
            Visit(el);

        void Walk(PropValue value)
        {
            if (value is ElementValue ev)
                Visit(ev.Element);
            else if (value is ElementsValue evs)
                evs.Elements.ForEach(Visit);
        }

        void Visit(Element el)
        {
            if (el.Events.Count > 0 && !_eventAttrs.ContainsKey(el))
            {
                foreach (var evt in el.Events)
                    Report(new Diagnostic(
                        DiagnosticSeverity.Error, "FS1033",
                        $"'on {evt.EventName}' is not supported on '{el.Keyword}' here (it would be dropped)",
                        evt.Location,
                        "put the handler on an element that is emitted on its own, or wrap this one in a stack"));
            }
            foreach (var p in el.Props)
                Walk(p.Value);
            if (el.Body is BlockBody block)
            {
                foreach (var child in block.Children.OfType<ChildElement>())
                    Visit(child.Element);
            }
        }
    }
}
