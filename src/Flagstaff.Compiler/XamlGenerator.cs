using System.Text;

namespace Flagstaff.Compiler;

/// <summary>
/// Walks the AST and emits WinUI 3 XAML. Each element keyword maps to
/// a deterministic XAML subtree via a small set of emission patterns.
/// <para>
/// Split across partial files by area (see the file map in src/Flagstaff.Compiler/README.md):
/// XamlGenerator.cs (state, dispatch, shared helpers), .App.cs, .Layout.cs, .Inputs.cs,
/// .Tables.cs, .Overlays.cs, .Escape.cs.
/// </para>
/// </summary>
public sealed partial class XamlGenerator
{
    private readonly StringBuilder _sb = new();
    private int _indent;
    private string? _namespace;
    private Dictionary<string, InferredMember>? _inferredMembers;
    private ConditionCompiler? _conditions;

    public string? StylesSource { get; set; }

    /// <summary>The file's <c>use</c> declarations; each becomes an <c>xmlns:prefix="using:Assembly"</c> on the root tag.</summary>
    public IReadOnlyList<UseDecl> Uses { get; set; } = [];

    private void EmitElements(List<Element> elements)
    {
        foreach (var el in elements)
            EmitElement(el);
    }

    private void EmitElement(Element el)
    {
        switch (el.Keyword)
        {
            case "toolbar": EmitToolbar(el); break;
            case "heading": EmitHeading(el); break;
            case "spacer": EmitSpacer(); break;
            case "text": EmitText(el); break;
            case "button": EmitButton(el); break;
            case "row": EmitRow(el); break;
            case "column": EmitColumn(el); break;
            case "stack": EmitStack(el); break;
            case "split": EmitSplit(el); break;
            case "group": EmitGroup(el); break;
            case "card": EmitCard(el); break;
            case "cards": EmitCards(el); break;
            case "stat": EmitStat(el); break;
            case "summary": EmitSummary(el); break;
            case "line": EmitLine(el); break;
            case "table": EmitTable(el); break;
            case "alert": EmitAlert(el); break;
            case "badge": EmitBadge(el); break;
            case "separator": EmitSeparator(el); break;
            case "image": EmitImage(el); break;
            case "textbox": EmitTextBox(el); break;
            case "number": EmitNumberBox(el); break;
            case "select": EmitSelect(el); break;
            case "checkbox": EmitCheckBox(el); break;
            case "radio": EmitRadio(el); break;
            case "datepicker": EmitDatePicker(el); break;
            case "filePicker": EmitFilePicker(el); break;
            case "textarea": EmitTextArea(el); break;
            case "toggle": EmitToggle(el); break;
            case "col": break; // table cols handled by EmitTable; standalone is structural noop
            case "link": EmitViewLink(el); break;
            case "progress": EmitProgress(el); break;
            case "spinner": EmitSpinner(el); break;
            case "toast": EmitHoistedMarker(el); break;   // rendered by EmitOverlayLayer
            case "confirm": EmitHoistedMarker(el); break; // rendered by EmitOverlayLayer
            case "dialog": EmitHoistedMarker(el); break;  // rendered by EmitOverlayLayer
            case "xaml": EmitRawXaml(el); break;
            case "list": EmitList(el); break;
            case "empty": break; // emitted by EmitTable/EmitList after the ListView; elsewhere FS2011
            default:
                if (el.IsGeneric) EmitGeneric(el);
                else Line($"<!-- Unknown element: {el.Keyword} -->");
                break;
        }
    }

    /// <summary>
    /// The view's condition compiler. Emitters call <c>Conditions.VisibilityAttr(el)</c> /
    /// <c>Conditions.EnabledAttr(el)</c> and append the result to the element's root tag.
    /// </summary>
    private ConditionCompiler Conditions =>
        _conditions ?? throw new InvalidOperationException("Conditions are only available while generating a view.");

    /// <summary>
    /// Maps a .flag binding path to an x:Bind path: <c>vm.X</c> → <c>ViewModel.X</c>,
    /// <c>item.X</c> → <c>X</c> (inside a DataTemplate); anything else is returned unchanged.
    /// </summary>
    /// <summary>Maps a binding as stored by the parser (<c>X</c> for <c>vm.X</c>, or <c>item.X</c>) to its x:Bind path.</summary>
    private static string Bind(string property) =>
        property.StartsWith("item.", StringComparison.Ordinal) ? property["item.".Length..] : "ViewModel." + property;

    /// <summary>Verbatim C# members appended to the generated code-behind class (event handlers etc.).</summary>
    public List<string> ExtraCodeBehindMembers { get; } = [];

    /// <summary>Verbatim C# members appended to the generated ViewModel partial (helper commands etc.).</summary>
    public List<string> ExtraViewModelMembers { get; } = [];

    public static string BindPath(string path)
    {
        if (path.StartsWith("vm.", StringComparison.Ordinal))
            return "ViewModel." + path["vm.".Length..];
        if (path.StartsWith("item.", StringComparison.Ordinal))
            return path["item.".Length..];
        return path;
    }

    private void WriteXamlNamespaces()
    {
        Line("xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"");
        Line("xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"");
        Line($"xmlns:local=\"using:{_namespace}\"");
        foreach (var use in Uses)
            Line($"xmlns:{use.Prefix}=\"using:{Esc(use.Assembly)}\"");
    }

    private static string GetLabel(Element el) =>
        el.Props.FirstOrDefault(p => p.Key == "_label")?.Value is StringValue sv ? sv.Value : "";

    private static string? GetBindingAttr(Element el, string key)
    {
        var prop = el.Props.FirstOrDefault(p => p.Key == key);
        if (prop?.Value is BindingValue bv)
            return Bind(bv.Property);
        return null;
    }

    private static string? GetFirstBinding(Element el)
    {
        foreach (var p in el.Props)
        {
            if (p.Value is BindingValue bv)
                return bv.Property;
        }
        return null;
    }

    private static string? GetPropValue(Element el, string key)
    {
        var prop = el.Props.FirstOrDefault(p => p.Key == key);
        return prop?.Value switch
        {
            StringValue sv => sv.Value,
            IdentValue iv => iv.Name,
            IntValue iv => iv.Value.ToString(),
            _ => null
        };
    }

    private static int? GetIntProp(Element el, string key)
    {
        var prop = el.Props.FirstOrDefault(p => p.Key == key);
        if (prop?.Value is IntValue iv) return iv.Value;
        return null;
    }

    private static int CountChildren(Element el) =>
        el.Body is BlockBody b ? b.Children.OfType<ChildElement>().Count() : 1;

    private static string? GetIconSymbol(List<PropPair> props)
    {
        var iconProp = props.FirstOrDefault(p => p.Key == "icon");
        if (iconProp is null) return null;
        var iconName = iconProp.Value switch
        {
            StringValue sv => sv.Value,
            IdentValue iv => iv.Name,
            _ => ""
        };
        if (string.IsNullOrEmpty(iconName)) return null;
        return NormalizeIconName(iconName);
    }

    public static string NormalizeIconName(string name)
    {
        var parts = name.Split('-');
        return string.Concat(parts.Select(p =>
            p.Length > 0 ? char.ToUpper(p[0]) + p[1..] : ""));
    }

    /// <summary>The WinUI <c>Symbol</c> enum members (the control manifest's members, plus the fixed list below).</summary>
    public static readonly HashSet<string> ValidSymbols = WithManifestSymbols(new(StringComparer.Ordinal)
    {
        "Previous", "Next", "Play", "Pause", "Edit", "Save", "Clear", "Delete",
        "Remove", "Add", "Cancel", "Accept", "More", "Redo", "Undo", "Home",
        "Up", "Forward", "Back", "Favorite", "Camera", "Setting", "Video",
        "Sync", "Download", "Mail", "Find", "Help", "Upload", "Emoji",
        "TwoPage", "LeaveChat", "MailForward", "Clock", "Send", "Crop",
        "RotateCamera", "People", "OpenPane", "ClosePane", "World", "Flag",
        "PreviewLink", "Globe", "Trim", "AttachCamera", "ZoomIn", "Bookmarks",
        "Document", "ProtectedDocument", "Page", "Bullets", "Comment",
        "MailFilled", "ContactInfo", "HangUp", "ViewAll", "MapPin", "Phone",
        "VideoChat", "Switch", "Contact", "Rename", "Pin", "MusicInfo", "Go",
        "Keyboard", "DockLeft", "DockRight", "DockBottom", "Remote", "Refresh",
        "Rotate", "Shuffle", "List", "Shop", "SelectAll", "Orientation",
        "Import", "ImportAll", "BrowsePhotos", "WebCam", "Pictures", "SaveLocal",
        "Caption", "Stop", "ShowResults", "Volume", "Repair", "Message",
        "Page2", "CalendarDay", "CalendarWeek", "Calendar", "Character",
        "MailReplyAll", "Read", "Link", "Account", "ShowBcc", "HideBcc",
        "Cut", "Attach", "Paste", "Filter", "Copy", "Bold", "Underline",
        "Italic", "AlignRight", "AlignCenter", "AlignLeft", "OpenFile",
        "ClearSelection", "FontDecrease", "FontIncrease", "FontSize", "Library",
        "NewWindow", "OutlineStar", "SolidStar", "Audio", "Permissions",
        "DisableUpdates", "Unfavorite", "OpenLocal", "Mute", "ReportHacked",
        "OtherUser", "Admin", "Street", "Map", "ClearAllNotes"
    });

    private static HashSet<string> WithManifestSymbols(HashSet<string> symbols)
    {
        if (ControlManifest.Default.TryGetValueType("Symbol", out var symbol) && symbol.EnumMembers is { } members)
            symbols.UnionWith(members);
        return symbols;
    }

    private static string Esc(string? s) =>
        (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private void Line(string text) => _sb.AppendLine($"{new string(' ', _indent * 4)}{text}");

    private void Indent() => _indent++;

    private void Dedent() => _indent = Math.Max(0, _indent - 1);
}
