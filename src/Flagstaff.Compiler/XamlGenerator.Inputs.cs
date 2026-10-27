namespace Flagstaff.Compiler;

// Input controls and display leaves.
// Every root tag appends CommonAttrs(el): when()/enabled() plus the universal and keyword props (XamlGenerator.Common.cs),
// and is written through EmitRoot so property elements (`Flyout: MenuFlyout { }`) land inside it (XamlGenerator.PassThrough.cs).
public sealed partial class XamlGenerator
{
    /// <summary>The arrow target that closes the enclosing dialog/confirm: <c>button "Cancel" -> cancel;</c>.</summary>
    internal const string CancelTarget = "cancel";

    /// <summary>
    /// <c>button</c> at page level and inside item templates (table row actions, list templates). The arrow target
    /// decides the action (<see cref="ActionAttrs"/>); <c>icon:</c> adds a SymbolIcon before the label.
    /// </summary>
    private void EmitButton(Element el)
    {
        var attrs = ActionAttrs(el) + ButtonVariantStyle(el) + CommonAttrs(el);
        EmitButtonControl("Button", el, GetLabel(el), attrs);
    }

    /// <summary>
    /// The action attributes of a <c>button</c>/<c>filePicker</c>/<c>link</c>:
    /// <list type="bullet">
    /// <item><c>-> vm.XCommand</c> (or <c>vm.X</c>) → <c>Command="{x:Bind ViewModel.XCommand, Mode=OneWay}"</c>; inside an item
    /// template, x:Bind is rooted at the row, so <c>{Binding ElementName=FlagstaffPage, Path=ViewModel.XCommand}</c>, plus
    /// <c>CommandParameter="{x:Bind}"</c> for <c>(item)</c>.</item>
    /// <item><c>-> cancel</c> → the enclosing dialog/confirm's generated <c>Cancel{Flag}Command</c>.</item>
    /// <item><c>-> navigate(View)</c> / <c>-> View</c> → <c>Tag="View" Click="OnNavigateButtonClick"</c>.</item>
    /// </list>
    /// </summary>
    private string ActionAttrs(Element el)
    {
        var target = el.NavTarget;
        if (target is null)
            return "";
        if (target == CancelTarget)
            return _modalCancelStem is { } stem ? $" Command=\"{{x:Bind ViewModel.{stem}Command, Mode=OneWay}}\"" : "";
        if (target.StartsWith("vm.", StringComparison.Ordinal))
        {
            var path = BindPath(TypeInference.CommandPath(target));
            if (_conditions?.InItemTemplate != true)
                return $" Command=\"{{x:Bind {path}, Mode=OneWay}}\"";
            var parameter = el.NavTargetArg == "item" ? " CommandParameter=\"{x:Bind}\"" : "";
            return $" Command=\"{{Binding ElementName=FlagstaffPage, Path={path}}}\"{parameter}";
        }
        return $" Tag=\"{Esc(target)}\" Click=\"OnNavigateButtonClick\"";
    }

    /// <summary>
    /// A button-like control: <c>Content="label"</c>, or with <c>icon:</c> a horizontal SymbolIcon + TextBlock content.
    /// <paramref name="attrs"/> follows the content attribute on the root tag.
    /// </summary>
    /// <param name="propertyElements">False for copies that must not repeat the element's property elements
    /// (the invisible ghost buttons of table row actions).</param>
    private void EmitButtonControl(string tag, Element el, string label, string attrs, bool propertyElements = true)
    {
        var symbol = GetIconSymbol(el.Props);
        Action? body = null;
        if (symbol is null)
        {
            attrs = $" Content=\"{Esc(label)}\"{attrs}";
        }
        else
        {
            body = () =>
            {
                Line("<StackPanel Orientation=\"Horizontal\" Spacing=\"8\">");
                Indent();
                Line($"<SymbolIcon Symbol=\"{symbol}\" />");
                if (label.Length > 0)
                    Line($"<TextBlock Text=\"{Esc(label)}\" />");
                Dedent();
                Line("</StackPanel>");
            };
        }

        if (propertyElements)
            EmitRoot(el, tag, attrs, body);
        else
            EmitTag(tag, attrs, body);
    }

    private void EmitTextBox(Element el)
    {
        var binding = GetFirstBinding(el);
        var label = GetLabel(el);
        var header = !string.IsNullOrEmpty(label) ? $" Header=\"{Esc(label)}\"" : "";
        var attrs = CommonAttrs(el);
        if (binding is not null)
            EmitRoot(el, "TextBox", $"{header} Text=\"{{x:Bind {Bind(binding)}, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}}\"{attrs}", null);
        else
            EmitRoot(el, "TextBox", $"{header}{attrs}", null);
    }

    private void EmitNumberBox(Element el)
    {
        var binding = GetFirstBinding(el);
        var label = GetLabel(el);
        var header = !string.IsNullOrEmpty(label) ? $" Header=\"{Esc(label)}\"" : "";
        var attrs = CommonAttrs(el);
        if (binding is not null)
            EmitRoot(el, "NumberBox", $"{header} Value=\"{{x:Bind {Bind(binding)}, Mode=TwoWay}}\" SpinButtonPlacementMode=\"Compact\"{attrs}", null);
        else
            EmitRoot(el, "NumberBox", $"{header} SpinButtonPlacementMode=\"Compact\"{attrs}", null);
    }

    private void EmitCheckBox(Element el)
    {
        var binding = GetFirstBinding(el);
        var label = GetLabel(el);
        var attrs = CommonAttrs(el);
        if (binding is not null)
            EmitRoot(el, "CheckBox", $" Content=\"{Esc(label)}\" IsChecked=\"{{x:Bind {Bind(binding)}, Mode=TwoWay}}\"{attrs}", null);
        else
            EmitRoot(el, "CheckBox", $" Content=\"{Esc(label)}\"{attrs}", null);
    }

    private void EmitRadio(Element el)
    {
        var label = GetLabel(el);
        var binding = GetFirstBinding(el);
        var attrs = CommonAttrs(el);
        if (binding is not null)
            EmitRoot(el, "RadioButton", $" Content=\"{Esc(label)}\" IsChecked=\"{{x:Bind {Bind(binding)}, Mode=TwoWay}}\"{attrs}", null);
        else
            EmitRoot(el, "RadioButton", $" Content=\"{Esc(label)}\"{attrs}", null);
    }

    private void EmitDatePicker(Element el)
    {
        var binding = GetFirstBinding(el);
        var label = GetLabel(el);
        var header = !string.IsNullOrEmpty(label) ? $" Header=\"{Esc(label)}\"" : "";
        var attrs = CommonAttrs(el);
        if (binding is not null)
            EmitRoot(el, "CalendarDatePicker", $"{header} Date=\"{{x:Bind {Bind(binding)}, Mode=TwoWay}}\"{attrs}", null);
        else
            EmitRoot(el, "CalendarDatePicker", $"{header}{attrs}", null);
    }

    private void EmitFilePicker(Element el)
    {
        var label = GetLabel(el);
        var attrs = " Style=\"{StaticResource FlagstaffFilePickerButton}\"" + ActionAttrs(el) + CommonAttrs(el);
        EmitButtonControl("Button", el, string.IsNullOrEmpty(label) ? "Choose File..." : label, attrs);
    }

    private void EmitTextArea(Element el)
    {
        var binding = GetFirstBinding(el);
        var label = GetLabel(el);
        var header = !string.IsNullOrEmpty(label) ? $" Header=\"{Esc(label)}\"" : "";
        var attrs = CommonAttrs(el);
        if (binding is not null)
            EmitRoot(el, "TextBox", $"{header} Text=\"{{x:Bind {Bind(binding)}, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}}\" AcceptsReturn=\"True\" TextWrapping=\"Wrap\" MinHeight=\"80\"{attrs}", null);
        else
            EmitRoot(el, "TextBox", $"{header} AcceptsReturn=\"True\" TextWrapping=\"Wrap\" MinHeight=\"80\"{attrs}", null);
    }

    private void EmitToggle(Element el)
    {
        var binding = GetFirstBinding(el);
        var label = GetLabel(el);
        var header = !string.IsNullOrEmpty(label) ? $" Header=\"{Esc(label)}\"" : "";
        var vis = CommonAttrs(el);
        if (binding is not null)
            EmitRoot(el, "ToggleSwitch", $"{header} IsOn=\"{{x:Bind {Bind(binding)}, Mode=TwoWay}}\"{vis}", null);
        else
            EmitRoot(el, "ToggleSwitch", $"{header}{vis}", null);
    }

    private void EmitImage(Element el)
    {
        var binding = GetFirstBinding(el);
        var vis = CommonAttrs(el);
        if (binding is not null)
            EmitRoot(el, "Image", $" Source=\"{BindExpr(binding)}\" Stretch=\"Uniform\"{vis}", null);
        else
        {
            var src = GetPropValue(el, "src") ?? "";
            EmitRoot(el, "Image", $" Source=\"{Esc(src)}\" Stretch=\"Uniform\"{vis}", null);
        }
    }

    private void EmitProgress(Element el)
    {
        var binding = GetFirstBinding(el);
        var vis = CommonAttrs(el);
        if (binding is not null)
            EmitRoot(el, "ProgressBar", $" Value=\"{BindExpr(binding)}\"{vis}", null);
        else
            EmitRoot(el, "ProgressBar", $" IsIndeterminate=\"True\"{vis}", null);
    }

    private void EmitSpinner(Element el) => EmitRoot(el, "ProgressRing", $" IsActive=\"True\"{CommonAttrs(el)}", null);

    private void EmitBadge(Element el)
    {
        var binding = GetFirstBinding(el);
        var vis = CommonAttrs(el);
        // A badge is a pill: it hugs its text instead of stretching across a list card (align: overrides).
        var hAlign = Prop(el, "align") is null ? " HorizontalAlignment=\"Left\"" : "";
        if (binding is not null)
        {
            var isInt = _inferredMembers is not null
                        && _inferredMembers.TryGetValue(binding, out var member)
                        && member.CSharpType == "int";

            if (isInt)
            {
                EmitRoot(el, "InfoBadge", $" Value=\"{BindExpr(binding)}\"{vis}", null);
            }
            else
            {
                EmitRoot(el, "Border", $" Background=\"{{ThemeResource SystemAccentColor}}\" CornerRadius=\"10\" Padding=\"6,2\" VerticalAlignment=\"Center\"{hAlign}{vis}",
                    () => Line($"<TextBlock Text=\"{BindExpr(binding)}\" Foreground=\"White\" FontSize=\"12\" FontWeight=\"SemiBold\" />"));
            }
        }
        else
        {
            var label = GetLabel(el);
            EmitRoot(el, "Border", $" Background=\"{{ThemeResource SystemAccentColor}}\" CornerRadius=\"10\" Padding=\"6,2\" VerticalAlignment=\"Center\"{hAlign}{vis}",
                () => Line($"<TextBlock Text=\"{Esc(label)}\" Foreground=\"White\" FontSize=\"12\" FontWeight=\"SemiBold\" />"));
        }
    }

    private void EmitSummary(Element el)
    {
        EmitRoot(el, "StackPanel", $" Spacing=\"4\"{CommonAttrs(el)}", () => EmitBlockChildren(el));
    }

    private void EmitLine(Element el)
    {
        var label = GetLabel(el);
        var binding = GetFirstBinding(el);
        var bold = el.Props.Any(p => p.Key == "bold");
        var weight = bold ? " FontWeight=\"Bold\"" : "";

        EmitRoot(el, "Grid", CommonAttrs(el), () =>
        {
            Line("<Grid.ColumnDefinitions>");
            Indent();
            Line("<ColumnDefinition Width=\"*\" />");
            Line("<ColumnDefinition Width=\"Auto\" />");
            Dedent();
            Line("</Grid.ColumnDefinitions>");
            Line($"<TextBlock Grid.Column=\"0\" Text=\"{Esc(label)}\"{weight} />");
            if (binding is not null)
                Line($"<TextBlock Grid.Column=\"1\" Text=\"{BindExpr(binding, FormatConverter(el.Format))}\" HorizontalAlignment=\"Right\"{weight} />");
        });
    }

    private void EmitStat(Element el)
    {
        var label = GetLabel(el);
        var valueBinding = RawBinding(el, "_binding");
        var whenVis = CommonAttrs(el);

        var styleKey = GetPropValue(el, "variant") switch
        {
            "success" => "FlagstaffStatCardSuccess",
            "warning" => "FlagstaffStatCardWarning",
            "danger" => "FlagstaffStatCardDanger",
            "info" => "FlagstaffStatCardInfo",
            _ => "FlagstaffStatCard",
        };
        EmitRoot(el, "Border", $" Style=\"{{StaticResource {styleKey}}}\"{whenVis}", () =>
        {
            Line("<StackPanel Spacing=\"4\" Padding=\"16\">");
            Indent();
            Line($"<TextBlock Text=\"{Esc(label)}\" Style=\"{{StaticResource CaptionTextBlockStyle}}\" Foreground=\"{{ThemeResource TextFillColorSecondaryBrush}}\" />");
            if (valueBinding is not null)
                Line($"<TextBlock Text=\"{BindExpr(valueBinding, FormatConverter(el.Format))}\" Style=\"{{StaticResource TitleTextBlockStyle}}\" />");
            Dedent();
            Line("</StackPanel>");
        });
    }

    /// <summary><c>link "Docs" -> navigate(View) | -> View | -> vm.XCommand</c>: a HyperlinkButton with the same actions as a button.</summary>
    private void EmitViewLink(Element el)
    {
        EmitButtonControl("HyperlinkButton", el, GetLabel(el), ActionAttrs(el) + CommonAttrs(el));
    }

    /// <summary><c>variant: primary</c> → the WinUI accent button, <c>variant: danger</c> → FlagstaffDangerButton.</summary>
    private static string ButtonVariantStyle(Element el) => GetPropValue(el, "variant") switch
    {
        "primary" => " Style=\"{StaticResource AccentButtonStyle}\"",
        "danger" => " Style=\"{StaticResource FlagstaffDangerButton}\"",
        _ => "",
    };
}
