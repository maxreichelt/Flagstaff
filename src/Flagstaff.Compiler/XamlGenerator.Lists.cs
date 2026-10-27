namespace Flagstaff.Compiler;

// list (free-form row templates) and the helpers shared with table: selection, row click, empty state,
// and buttons inside item templates.
public sealed partial class XamlGenerator
{
    private int _itemClickCount;

    /// <summary>
    /// <c>list items: vm.X of T [selected: vm.Sel] [-> vm.OpenCommand(item)] { ... }</c> → a ListView whose
    /// ItemTemplate is <c>DataTemplate x:DataType="vm:T"</c> holding the children (emitted with
    /// <see cref="ConditionCompiler.InItemTemplate"/> set, so <c>item.X</c> binds to the row).
    /// </summary>
    private void EmitList(Element el)
    {
        EmitCollection(el, listVisibility => EmitListListView(el, listVisibility));
    }

    private void EmitListListView(Element el, string visibilityAttr)
    {
        var itemsBinding = GetBindingAttr(el, "items");
        var attrs = (itemsBinding is not null ? $" ItemsSource=\"{{x:Bind {itemsBinding}, Mode=OneWay}}\"" : "")
                    + SelectionAttrs(el)
                    + ItemClickAttrs(el)
                    + " ItemContainerStyle=\"{StaticResource FlagstaffListItemContainer}\""
                    + visibilityAttr
                    + PassThroughAttrs(el)
                    + EventAttrs(el);
        EmitRoot(el, "ListView", attrs, () =>
        {
            Line("<ListView.ItemTemplate>");
            Indent();
            var dataTypeAttr = el.OfType is not null ? $" x:DataType=\"vm:{el.OfType}\"" : "";
            Line($"<DataTemplate{dataTypeAttr}>");
            Indent();
            Line($"<StackPanel Spacing=\"4\" Padding=\"0,8\"{RowHitTestAttrs(el)}>");
            Indent();
            EmitRowPropertyElements(el, "StackPanel");

            var wasInTemplate = Conditions.InItemTemplate;
            Conditions.InItemTemplate = true;
            try
            {
                if (el.Body is BlockBody block)
                {
                    foreach (var child in block.Children)
                    {
                        if (child is ChildElement { Element.Keyword: "empty" })
                            continue; // emitted after the ListView by EmitCollection
                        if (child is ChildElement ce)
                            EmitElement(ce.Element);
                        else if (child is TextContent tc)
                            Line($"<TextBlock Text=\"{Esc(tc.Text)}\" TextWrapping=\"Wrap\" />");
                    }
                }
            }
            finally
            {
                Conditions.InItemTemplate = wasInTemplate;
            }

            Dedent();
            Line("</StackPanel>");
            Dedent();
            Line("</DataTemplate>");
            Dedent();
            Line("</ListView.ItemTemplate>");
        });
    }

    /// <summary>
    /// Emits a table/list. Without an <c>empty</c> child the ListView carries the element's <c>when()</c>;
    /// with one, a Grid carries it and holds the ListView plus the empty-state text. A <c>fill</c> collection
    /// keeps one <c>*</c> cell (ListView and the text, centered, overlap), so the ListView stays bounded and
    /// virtualizes; otherwise the text sits in an Auto row below the ListView (no overlap with a table header).
    /// </summary>
    private void EmitCollection(Element el, Action<string> emitListView)
    {
        var empty = el.Body is BlockBody block
            ? block.Children.OfType<ChildElement>().Select(c => c.Element).FirstOrDefault(c => c.Keyword == "empty")
            : null;
        var visibility = Conditions.VisibilityAttr(el);
        if (empty is null)
        {
            emitListView(visibility);
            return;
        }

        var fill = IsFill(el);
        _placedByWrapper.Add(el);
        Line($"<Grid{PlacementAttrs(el)}{visibility}>");
        Indent();
        if (!fill)
        {
            Line("<Grid.RowDefinitions>");
            Indent();
            Line("<RowDefinition Height=\"Auto\" />");
            Line("<RowDefinition Height=\"Auto\" />");
            Dedent();
            Line("</Grid.RowDefinitions>");
        }
        emitListView("");
        EmitEmptyState(el, empty, fill ? " VerticalAlignment=\"Center\"" : " Grid.Row=\"1\"");
        Dedent();
        Line("</Grid>");
    }

    /// <summary><c>empty "No rows.";</c> → a TextBlock visible while the items collection has no elements.</summary>
    private void EmitEmptyState(Element collection, Element empty, string placement)
    {
        var items = GetBindingAttr(collection, "items");
        if (items is null)
            return; // FS2011 (reported by ItemTypeRegistry.Collect)

        var when = Conditions.VisibilityAttr(empty);
        var textPlacement = when.Length == 0 ? placement : "";
        var text = $"<TextBlock Text=\"{Esc(GetLabel(empty))}\" Style=\"{{StaticResource FlagstaffEmptyState}}\"{textPlacement}"
                   + $" Visibility=\"{{x:Bind local:Converters.IsZero({items}.Count), Mode=OneWay}}\" />";
        if (when.Length == 0)
        {
            Line(text);
            return;
        }
        // Its own when() cannot share the Visibility attribute; a wrapper carries it.
        Line($"<Border{placement}{when}>");
        Indent();
        Line(text);
        Dedent();
        Line("</Border>");
    }

    /// <summary><c>selected: vm.Sel</c> → single selection bound two-way; otherwise <c>""</c>.</summary>
    private static string SelectionAttrs(Element el)
    {
        var selected = GetBindingAttr(el, "selected");
        return selected is not null
            ? $" SelectionMode=\"Single\" SelectedItem=\"{{x:Bind {selected}, Mode=TwoWay}}\""
            : "";
    }

    /// <summary>
    /// <c>-> vm.OpenCommand(item)</c> / <c>-> navigate(View)</c> on a table/list → <c>IsItemClickEnabled</c> plus an
    /// <c>OnItemClick_n</c> handler in the code-behind (via <see cref="ExtraCodeBehindMembers"/>).
    /// </summary>
    private string ItemClickAttrs(Element el)
    {
        if (el.NavTarget is not { } target)
            return "";

        var name = $"OnItemClick_{++_itemClickCount}";
        var itemType = el.OfType is not null ? $"ViewModels.{el.OfType}" : "object";
        string body;
        if (target.StartsWith("vm.", StringComparison.Ordinal))
        {
            body = el.NavTargetArg == "item"
                ? $"if (e.ClickedItem is {itemType} item) {BindPath(TypeInference.CommandPath(target))}.Execute(item);"
                : $"{BindPath(TypeInference.CommandPath(target))}.Execute(null);";
        }
        else
        {
            body = $"if (e.ClickedItem is {itemType} item) Frame.Navigate(typeof({target}), item);";
        }

        var nl = Environment.NewLine;
        ExtraCodeBehindMembers.Add(
            $"    private void {name}(object sender, ItemClickEventArgs e){nl}    {{{nl}        {body}{nl}    }}");
        return $" IsItemClickEnabled=\"True\" ItemClick=\"{name}\"";
    }
}
