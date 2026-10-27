namespace Flagstaff.Compiler;

// Data-bound collections: table, select. Shared collection helpers (row click, empty state) live in XamlGenerator.Lists.cs.
public sealed partial class XamlGenerator
{
    private void EmitTable(Element el)
    {
        EmitCollection(el, listVisibility => EmitTableListView(el, listVisibility));
    }

    private void EmitTableListView(Element el, string visibilityAttr)
    {
        var itemsBinding = GetBindingAttr(el, "items");
        var attrs = $" ItemsSource=\"{{x:Bind {itemsBinding ?? "Items"}, Mode=OneWay}}\""
                    + SelectionAttrs(el)
                    + ItemClickAttrs(el)
                    + " ItemContainerStyle=\"{StaticResource FlagstaffTableItemContainer}\""
                    + visibilityAttr
                    + PassThroughAttrs(el)
                    + EventAttrs(el);
        EmitRoot(el, "ListView", attrs, () =>
        {
            if (el.Body is BlockBody block)
            {
                var columns = block.Children.OfType<ChildElement>()
                    .Select(c => c.Element)
                    .Where(c => c.Keyword is "col" or "line")
                    .ToList();
                // Row actions: button children render in a trailing Auto column of every row.
                var actions = block.Children.OfType<ChildElement>()
                    .Select(c => c.Element)
                    .Where(c => c.Keyword == "button")
                    .ToList();
                var widths = columns.Select(c => WidthToXaml(c.Props.FirstOrDefault(p => p.Key == "width")?.Value)).ToList();
                if (actions.Count > 0)
                    widths.Add("Auto");

                Line("<ListView.HeaderTemplate>");
                Indent();
                Line("<DataTemplate>");
                Indent();
                Line("<Grid Style=\"{StaticResource FlagstaffTableHeader}\">");
                Indent();
                EmitColumnDefinitions(widths);
                for (int ci = 0; ci < columns.Count; ci++)
                    Line($"<TextBlock Grid.Column=\"{ci}\" Text=\"{Esc(GetLabel(columns[ci]))}\" FontWeight=\"SemiBold\" />");
                if (actions.Count > 0)
                    EmitActionPlaceholders(actions, columns.Count);
                Dedent();
                Line("</Grid>");
                Dedent();
                Line("</DataTemplate>");
                Dedent();
                Line("</ListView.HeaderTemplate>");

                Line("<ListView.ItemTemplate>");
                Indent();
                var dataTypeAttr = el.OfType is not null ? $" x:DataType=\"vm:{el.OfType}\"" : "";
                Line($"<DataTemplate{dataTypeAttr}>");
                Indent();
                Line($"<Grid Style=\"{{StaticResource FlagstaffTableRow}}\"{RowHitTestAttrs(el)}>");
                Indent();
                EmitRowPropertyElements(el, "Grid");
                EmitColumnDefinitions(widths);
                for (int ci = 0; ci < columns.Count; ci++)
                {
                    var col = columns[ci];
                    var bound = col.Props.FirstOrDefault(p => p.Key == "bind")?.Value as BindingValue
                                ?? col.Props.FirstOrDefault(p => p.Key == "_binding")?.Value as BindingValue;
                    if (bound is not null)
                        Line($"<TextBlock Grid.Column=\"{ci}\" Text=\"{{x:Bind {BindPath(bound.Property)}{FormatConverter(col.Format)}}}\" />");
                    else
                        Line($"<TextBlock Grid.Column=\"{ci}\" />");
                }
                if (actions.Count > 0)
                    EmitRowActions(actions, columns.Count);
                Dedent();
                Line("</Grid>");
                Dedent();
                Line("</DataTemplate>");
                Dedent();
                Line("</ListView.ItemTemplate>");
            }
        });
    }

    /// <summary>Identical column definitions for the header grid and every row grid, so they line up.</summary>
    private void EmitColumnDefinitions(List<string> widths)
    {
        Line("<Grid.ColumnDefinitions>");
        Indent();
        foreach (var width in widths)
            Line($"<ColumnDefinition Width=\"{width}\" />");
        Dedent();
        Line("</Grid.ColumnDefinitions>");
    }

    /// <summary>
    /// The row-action buttons of a table, in the row's last (Auto) column. A button with <c>when()</c> sits in a
    /// one-cell Grid over an invisible copy of itself (<see cref="EmitGhostButton"/>), so hiding it keeps its width:
    /// every row's Auto column, and the header's (<see cref="EmitActionPlaceholders"/>), is the same width and the
    /// star/auto columns of all rows line up.
    /// </summary>
    private void EmitRowActions(List<Element> actions, int column)
    {
        Line($"<StackPanel Grid.Column=\"{column}\" Orientation=\"Horizontal\" Spacing=\"4\">");
        Indent();
        var wasInTemplate = Conditions.InItemTemplate;
        Conditions.InItemTemplate = true;
        try
        {
            foreach (var action in actions)
            {
                if (action.When is null)
                {
                    EmitElement(action);
                    continue;
                }
                Line("<Grid>");
                Indent();
                EmitGhostButton(action);
                EmitElement(action);
                Dedent();
                Line("</Grid>");
            }
        }
        finally
        {
            Conditions.InItemTemplate = wasInTemplate;
        }
        Dedent();
        Line("</StackPanel>");
    }

    /// <summary>The header's action column: invisible copies of the row actions, so it is as wide as the rows' column.</summary>
    private void EmitActionPlaceholders(List<Element> actions, int column)
    {
        Line($"<StackPanel Grid.Column=\"{column}\" Orientation=\"Horizontal\" Spacing=\"4\">");
        Indent();
        foreach (var action in actions)
            EmitGhostButton(action);
        Dedent();
        Line("</StackPanel>");
    }

    /// <summary>A transparent, non-interactive copy of a button (same label, icon, style and pixel width) that only reserves space.</summary>
    private void EmitGhostButton(Element el)
    {
        var width = Prop(el, "width") is { } w && ParseWidth(w) is { IsPixel: true } px ? $" Width=\"{px.Xaml}\"" : "";
        var attrs = ButtonVariantStyle(el) + width
                    + " Opacity=\"0\" IsHitTestVisible=\"False\" IsTabStop=\"False\" AutomationProperties.AccessibilityView=\"Raw\"";
        EmitButtonControl("Button", el, GetLabel(el), attrs, propertyElements: false);
    }

    private void EmitSelect(Element el)
    {
        var binding = GetFirstBinding(el);
        var label = GetLabel(el);
        var header = !string.IsNullOrEmpty(label) ? $" Header=\"{Esc(label)}\"" : "";
        var options = el.Props.FirstOrDefault(p => p.Key == "options")?.Value;
        var itemsBinding = GetBindingAttr(el, "items");
        var display = el.Props.FirstOrDefault(p => p.Key == "display")?.Value as BindingValue;
        var attrs = header + CommonAttrs(el);
        if (binding is not null)
            attrs += $" SelectedItem=\"{{x:Bind {Bind(binding)}, Mode=TwoWay}}\"";
        if (itemsBinding is not null)
            attrs += $" ItemsSource=\"{{x:Bind {itemsBinding}, Mode=OneWay}}\"";

        if (itemsBinding is null && options is RangeValue range)
        {
            EmitRoot(el, "ComboBox", attrs, () =>
            {
                for (int i = range.Start; i <= range.End; i++)
                    Line($"<x:Int32>{i}</x:Int32>");
            });
        }
        else if (itemsBinding is null && options is ListValue list)
        {
            EmitRoot(el, "ComboBox", attrs, () =>
            {
                foreach (var item in list.Items.OfType<StringValue>())
                    Line($"<x:String>{Esc(item.Value)}</x:String>");
            });
        }
        else if (itemsBinding is not null && display is not null)
        {
            // display: item.Name — the ComboBox shows that member; no ToString() template.
            EmitRoot(el, "ComboBox", $"{attrs} DisplayMemberPath=\"{Esc(BindPath(display.Property))}\"", null);
        }
        else if (el.OfType is not null && itemsBinding is not null)
        {
            EmitRoot(el, "ComboBox", attrs, () =>
            {
                Line("<ComboBox.ItemTemplate>");
                Indent();
                Line($"<DataTemplate x:DataType=\"vm:{el.OfType}\">");
                Indent();
                Line($"<TextBlock Text=\"{{x:Bind}}\" />");
                Dedent();
                Line("</DataTemplate>");
                Dedent();
                Line("</ComboBox.ItemTemplate>");
            });
        }
        else
        {
            EmitRoot(el, "ComboBox", attrs, null);
        }
    }
}
