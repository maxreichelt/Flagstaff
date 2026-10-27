using System.Globalization;

namespace Flagstaff.Compiler;

/// <summary>
/// Recursive-descent parser for .flag files. Unrecoverable errors throw
/// <see cref="FlagstaffParseException"/>; recoverable ones (unknown element keyword, bad command
/// argument) are collected in <see cref="Diagnostics"/> and parsing continues.
/// </summary>
public sealed class Parser
{
    /// <summary>Element keywords the parser accepts; derived from <see cref="ElementSchema.All"/>.</summary>
    public static IEnumerable<string> ElementKeywords => ElementSchema.All.Keys;

    private static bool IsElementKeyword(string word) => ElementSchema.All.ContainsKey(word);

    private readonly List<Token> _tokens;
    private int _pos;

    public Parser(List<Token> tokens)
    {
        _tokens = tokens;
    }

    /// <summary>Recoverable errors found while parsing (FS1000, FS1036, FS1052).</summary>
    public List<Diagnostic> Diagnostics { get; } = [];

    /// <summary>Prefixes declared by <c>use "..." as prefix;</c> so far.</summary>
    private readonly HashSet<string> _usePrefixes = new(StringComparer.Ordinal);

    private const string UseHint = "write use \"<assembly>\" as prefix; at the top of the file, before app and view";

    public FlagFile Parse()
    {
        var uses = new List<UseDecl>();
        while (Current.IsKeyword("use"))
            uses.Add(ParseUse());

        var decls = new List<TopLevelDecl>();
        while (!AtEnd())
        {
            if (Current.IsKeyword("app"))
                decls.Add(ParseApp());
            else if (Current.IsKeyword("view"))
                decls.Add(ParseView());
            else if (Current.IsKeyword("use"))
                throw Error("'use' declarations must come before 'app' and 'view'", UseHint);
            else
                throw Error($"Expected 'app' or 'view', got '{Current.Value}'");
        }
        return new FlagFile(decls, uses);
    }

    // ── use ────────────────────────────────────────────────────────

    /// <summary><c>use "LiveChartsCore.SkiaSharpView.WinUI" as lvc;</c></summary>
    private UseDecl ParseUse()
    {
        var loc = Current.Location;
        Expect("use");
        if (!Check(TokenKind.StringLiteral))
            throw Error($"Expected an assembly name string after 'use', got '{Current.Value}'", UseHint);
        var assembly = ConsumeString();
        if (!Current.IsKeyword("as"))
            throw Error($"Expected 'as' after use \"{assembly}\", got '{Current.Value}'", UseHint);
        Advance();
        var prefixToken = Current;
        if (prefixToken.Kind != TokenKind.Identifier || AstExtensions.IsPascalCase(prefixToken.Value)
            || prefixToken.Value is "x" or "local" or "item")
            throw Error(
                $"Invalid namespace prefix '{prefixToken.Value}'",
                "a prefix is a lowercase identifier other than x, local, vm or item, e.g. use \"MyLib\" as lib;");
        Advance();
        if (!_usePrefixes.Add(prefixToken.Value))
            throw new FlagstaffParseException(
                $"Namespace prefix '{prefixToken.Value}' is declared twice", prefixToken.Location, "FS1051",
                "declare each prefix once");
        Consume(TokenKind.Semicolon);
        return new UseDecl(assembly, prefixToken.Value, loc);
    }

    // ── App ────────────────────────────────────────────────────────

    private AppDecl ParseApp()
    {
        var loc = Current.Location;
        Expect("app");
        var name = ConsumeIdentifier();
        Consume(TokenKind.LeftBrace);
        var members = new List<AppMember>();
        while (!Check(TokenKind.RightBrace))
        {
            if (Current.IsKeyword("theme"))
                members.Add(ParseTheme());
            else if (Current.IsKeyword("nav"))
                members.Add(ParseNav());
            else
                throw Error($"Expected 'theme' or 'nav' in app block, got '{Current.Value}'");
        }
        Consume(TokenKind.RightBrace);
        return new AppDecl(name, members, loc);
    }

    private ThemeBlock ParseTheme()
    {
        Expect("theme");
        Consume(TokenKind.LeftBrace);
        var props = new List<PropPair>();
        while (!Check(TokenKind.RightBrace))
        {
            var loc = Current.Location;
            var key = ConsumeIdentifier();
            Consume(TokenKind.Colon);
            var value = ParsePropValue();
            Consume(TokenKind.Semicolon);
            props.Add(new PropPair(key, value, loc));
        }
        Consume(TokenKind.RightBrace);
        return new ThemeBlock(props);
    }

    private NavBlock ParseNav()
    {
        Expect("nav");
        var mode = Current.Value switch
        {
            "top" => NavMode.Top,
            "side" => NavMode.Side,
            _ => throw Error("Expected 'top' or 'side' after 'nav'")
        };
        Advance();
        Consume(TokenKind.LeftBrace);
        var members = new List<NavMember>();
        while (!Check(TokenKind.RightBrace))
        {
            if (Current.IsKeyword("brand"))
                members.Add(ParseBrand());
            else if (Current.IsKeyword("link"))
                members.Add(ParseNavLink());
            else
                throw Error($"Expected 'brand' or 'link' in nav block, got '{Current.Value}'");
        }
        Consume(TokenKind.RightBrace);
        return new NavBlock(mode, members);
    }

    private BrandDecl ParseBrand()
    {
        Expect("brand");
        var text = ConsumeString();
        Consume(TokenKind.Semicolon);
        return new BrandDecl(text);
    }

    private NavLinkDecl ParseNavLink()
    {
        var loc = Current.Location;
        Expect("link");
        var label = ConsumeString();
        var props = ParseInlineProps();
        Consume(TokenKind.Arrow);
        var target = ConsumeIdentifier();
        Consume(TokenKind.Semicolon);
        return new NavLinkDecl(label, props, target, loc);
    }

    // ── View ───────────────────────────────────────────────────────

    private ViewDecl ParseView()
    {
        var loc = Current.Location;
        Expect("view");
        var name = ConsumeIdentifier();
        Consume(TokenKind.LeftBrace);

        BindDecl? binding = null;
        string? onLoadTarget = null;
        SourceLocation? onLoadLocation = null;
        var body = new List<Element>();
        var propertyElements = new List<PropPair>();

        while (!Check(TokenKind.RightBrace))
        {
            if (AtEnd())
                throw Error($"Unexpected end of file inside view '{name}'; missing '}}'");
            if (IsPropertyLine())
            {
                // Page.Resources: { ... } is a property element of the page itself.
                propertyElements.Add(ParsePropertyLine());
            }
            else if (Current.IsKeyword("bind"))
            {
                binding = ParseBind();
            }
            else if (Current.IsKeyword("onload"))
            {
                onLoadLocation = Current.Location;
                onLoadTarget = ParseOnLoad();
            }
            else if (ParseElement() is { } element)
            {
                body.Add(element);
            }
        }
        Consume(TokenKind.RightBrace);
        return new ViewDecl(name, binding, onLoadTarget, body, loc, onLoadLocation)
        {
            PropertyElements = propertyElements,
            NamedElements = CollectNamedElements(body, propertyElements),
        };
    }

    /// <summary>Every <c>name:</c> declaration in the view, in source order (children and property-element values included).</summary>
    private static List<NamedElement> CollectNamedElements(List<Element> body, List<PropPair> propertyElements)
    {
        var result = new List<NamedElement>();
        void VisitProps(List<PropPair> props)
        {
            foreach (var prop in props)
            {
                switch (prop.Value)
                {
                    case ElementValue ev: Visit(ev.Element); break;
                    case ElementsValue evs: evs.Elements.ForEach(Visit); break;
                }
            }
        }
        void Visit(Element el)
        {
            if (el.Props.FirstOrDefault(p => p.Key == "name") is { } nameProp
                && nameProp.Value switch { IdentValue iv => iv.Name, StringValue sv => sv.Value, _ => null } is { Length: > 0 } n)
                result.Add(new NamedElement(n, el, nameProp.Location));
            VisitProps(el.Props);
            if (el.Body is BlockBody block)
                foreach (var child in block.Children.OfType<ChildElement>())
                    Visit(child.Element);
        }
        VisitProps(propertyElements);
        body.ForEach(Visit);
        return result;
    }

    private BindDecl ParseBind()
    {
        Expect("bind");
        Expect("vm");
        Consume(TokenKind.Colon);
        var type = ConsumeIdentifier();
        Consume(TokenKind.Semicolon);
        return new BindDecl(type);
    }

    private string ParseOnLoad()
    {
        Expect("onload");
        Consume(TokenKind.Arrow);
        var target = ConsumeAnyIdentOrBinding();
        Consume(TokenKind.Semicolon);
        return target;
    }

    // ── Elements ───────────────────────────────────────────────────

    /// <summary>
    /// Parses one element; returns null (after recording FS1000) for an unknown keyword. The keyword is a
    /// lowercase schema keyword, a PascalCase WinUI type (<c>Expander</c>) or <c>prefix.Type</c> for a
    /// <c>use</c> namespace (generic elements; validated later, not here).
    /// </summary>
    private Element? ParseElement()
    {
        var keywordToken = Current;
        if (keywordToken.IsKeyword("use"))
            throw Error("'use' declarations must come before 'app' and 'view'", UseHint);
        if (keywordToken.Kind is not (TokenKind.Identifier or TokenKind.Keyword))
            throw Error($"Expected element keyword, got '{keywordToken.Value}'");

        var keyword = keywordToken.Value;
        var isGeneric = false;
        string? prefix = null;
        if (IsElementKeyword(keyword))
        {
            Advance();
        }
        else if (IsGenericTypeStart())
        {
            isGeneric = true;
            if (!AstExtensions.IsPascalCase(keyword))
            {
                prefix = keyword;
                keyword = $"{prefix}.{Peek(2).Value}";
                _pos += 3;
                if (!_usePrefixes.Contains(prefix))
                    Diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error, "FS1036",
                        $"Namespace prefix '{prefix}' is not declared",
                        keywordToken.Location,
                        $"add use \"<assembly>\" as {prefix}; at the top of the file"));
            }
            else
            {
                Advance();
            }
        }
        else
        {
            Diagnostics.Add(SemanticValidator.UnknownKeyword(keyword, keywordToken.Location));
            SkipToElementEnd();
            return null;
        }

        if (keyword == "xaml")
            return ParseXamlEscape(keywordToken.Location);

        var props = new List<PropPair>();
        if (Check(TokenKind.StringLiteral))
        {
            var labelLoc = Current.Location;
            props.Add(new PropPair("_label", new StringValue(ConsumeString()), labelLoc));
        }

        // Positional binding: `textbox vm.Name;` or (in item templates) `text item.Name;`
        if (IsBindingStart())
        {
            var bindingLoc = Current.Location;
            props.Add(new PropPair("_binding", ParseBindingValue(), bindingLoc));
        }

        CondExpr? when = null;
        CondExpr? enabled = null;
        FormatClause? format = null;
        string? ofType = null;
        string? navTarget = null;
        string? navTargetArg = null;
        var events = new List<EventBinding>();
        var endedByValue = false;
        int? typeArgTarget = null; // index of a pass-through binding prop parsed just before the cursor

        while (!AtEnd() && !Check(TokenKind.LeftBrace) && !Check(TokenKind.RightBrace) && !Check(TokenKind.Semicolon))
        {
            var pendingTypeArg = typeArgTarget;
            typeArgTarget = null;
            if (Current.IsKeyword("of"))
                typeArgTarget = pendingTypeArg;
            if (Current.IsKeyword("when"))
            {
                when = ParseConditionClause("when");
            }
            else if (Current.IsKeyword("enabled") && Peek(1).Kind == TokenKind.LeftParen)
            {
                enabled = ParseConditionClause("enabled");
            }
            else if (Current.IsKeyword("format"))
            {
                format = ParseFormat();
            }
            else if (Current.IsKeyword("of"))
            {
                Advance();
                ofType = ConsumeIdentifier();
                // `of Namespace.Type`: a qualified C# type name (third-party elements) is passed through verbatim.
                while (Check(TokenKind.Dot) && Peek(1).Kind is TokenKind.Identifier or TokenKind.Keyword)
                {
                    Advance();
                    ofType += "." + ConsumeAnyIdent();
                }
                // `ItemsSource: vm.Rows of Row`: the type also belongs to the binding it follows (generic elements type
                // each binding by its own `of`; TypeInference.ManifestTypeFor).
                if (typeArgTarget is { } index)
                    props[index] = props[index] with { TypeArg = ofType };
            }
            else if (Check(TokenKind.Arrow))
            {
                (navTarget, navTargetArg) = ParseArrowTarget();
            }
            else if (Current.IsKeyword("on"))
            {
                events.Add(ParseEvent());
            }
            else if (Current.Kind is TokenKind.Keyword or TokenKind.Identifier)
            {
                var keyLoc = Current.Location;
                var key = Current.Value;
                if (TryPeekKey(out var fullKey, out var keyTokens))
                {
                    // `key: value`, also dotted (`Grid.Row: 1`, `AutomationProperties.Name: "Save"`).
                    // A PascalCase identifier followed by ':' is always a key, never a new element.
                    _pos += keyTokens;
                    Consume(TokenKind.Colon);
                    var value = ParsePropValue(fullKey);
                    props.Add(new PropPair(fullKey, value, keyLoc));
                    if (value is BindingValue && AstExtensions.IsPassThroughKey(fullKey))
                        typeArgTarget = props.Count - 1;

                    // `button "Save" Icon: SymbolIcon Symbol: "Save";`: the value element took the ';'. Unless the
                    // header visibly continues, that ';' ends this element too.
                    if (value is ElementValue && Peek(-1).Kind == TokenKind.Semicolon && !HeaderContinues())
                    {
                        endedByValue = true;
                        if (Check(TokenKind.Semicolon)) Advance(); // a redundant second ';'
                        break;
                    }
                }
                else if (IsElementKeyword(key) || IsGenericTypeStart())
                {
                    break; // most likely a missing ';' before the next element
                }
                else
                {
                    // Bare flag (`bold`). The validator decides whether this keyword accepts it.
                    Advance();
                    props.Add(new PropPair(key, new BoolValue(true), keyLoc));
                }
            }
            else
            {
                break;
            }
        }

        ElementBody body;
        if (endedByValue)
        {
            body = new EmptyBody();
        }
        else if (Check(TokenKind.LeftBrace))
        {
            // `Key: value` and `on X -> y;` lines in the block belong to this element. A block holding only such
            // lines is the same element as the header form (`button "x" Flyout: MenuFlyout { };`).
            var membersBefore = props.Count + events.Count;
            var block = ParseBlock(props, events);
            body = block.Children.Count == 0 && props.Count + events.Count > membersBefore ? new EmptyBody() : block;
        }
        else if (Check(TokenKind.Semicolon))
        {
            Advance();
            body = new EmptyBody();
        }
        else
        {
            // A header element value with a block (`Flyout: MenuFlyout { ... }`) does not end its owner.
            var hint = props.Count > 0 && props[^1].Value is ElementValue { Element.Body: BlockBody } v
                ? $"after the '}}' of {props[^1].Key}: {v.Element.Keyword} {{ ... }}, end '{keyword}' with ';', or move the {props[^1].Key}: line into its {{ }} block"
                : "every element without { } ends with ;";
            throw Error($"Expected ';' or '{{' after '{keyword}' element, got '{Current.Value}'", hint);
        }

        return new Element(keyword, props, body, when, format, ofType, navTarget, keywordToken.Location, enabled, navTargetArg)
        {
            IsGeneric = isGeneric,
            NamespacePrefix = prefix,
            Events = events,
        };
    }

    /// <summary>The element header goes on at the cursor: a key, a clause, <c>on</c>, <c>-></c> or a <c>{</c> block.</summary>
    private bool HeaderContinues() =>
        TryPeekKey(out _, out _)
        || Check(TokenKind.Arrow) || Check(TokenKind.LeftBrace)
        || Current.IsKeyword("when") || Current.IsKeyword("format") || Current.IsKeyword("of") || Current.IsKeyword("on")
        || (Current.IsKeyword("enabled") && Peek(1).Kind == TokenKind.LeftParen);

    /// <summary><c>on SelectionChanged -> vm.SelectCommand</c> (targets as for <c>-></c>, including <c>(item)</c>).</summary>
    private EventBinding ParseEvent()
    {
        var loc = Current.Location;
        Expect("on");
        if (Current.Kind is not (TokenKind.Identifier or TokenKind.Keyword) || Peek(1).Kind != TokenKind.Arrow)
            throw Error(
                $"Expected an event name and '->' after 'on', got '{Current.Value}'",
                "write on Click -> vm.SaveCommand");
        var eventName = ConsumeIdentifier();
        var (target, arg) = ParseArrowTarget();
        return new EventBinding(eventName, target, arg, loc);
    }

    /// <summary>
    /// A generic element type at the cursor: a PascalCase identifier, or <c>prefix.PascalCase</c> (a lowercase
    /// identifier, a dot and a PascalCase identifier; whether the prefix is declared is checked by the caller).
    /// Never true for a key (an identifier path followed by ':').
    /// </summary>
    private bool IsGenericTypeStart()
    {
        if (Current.Kind != TokenKind.Identifier || TryPeekKey(out _, out _))
            return false;
        if (AstExtensions.IsPascalCase(Current.Value))
            return Peek(1).Kind != TokenKind.Dot;
        return Current.Value != "item"
            && Peek(1).Kind == TokenKind.Dot
            && Peek(2).Kind == TokenKind.Identifier && AstExtensions.IsPascalCase(Peek(2).Value)
            && Peek(3).Kind != TokenKind.Dot;
    }

    /// <summary>
    /// A key at the cursor: <c>Ident ( "." Ident )* ":"</c>. Returns the joined key and the number of tokens
    /// before the colon.
    /// </summary>
    private bool TryPeekKey(out string key, out int tokenCount)
    {
        key = "";
        tokenCount = 0;
        if (Current.Kind is not (TokenKind.Identifier or TokenKind.Keyword))
            return false;
        var parts = new List<string> { Current.Value };
        var i = 1;
        while (Peek(i).Kind == TokenKind.Dot && Peek(i + 1).Kind is TokenKind.Identifier or TokenKind.Keyword)
        {
            parts.Add(Peek(i + 1).Value);
            i += 2;
        }
        if (Peek(i).Kind != TokenKind.Colon)
            return false;
        key = string.Join('.', parts);
        tokenCount = i;
        return true;
    }

    /// <summary>A <c>Key: value</c> line in a block or view body (pass-through keys only; lowercase keys stay errors).</summary>
    private bool IsPropertyLine() => TryPeekKey(out var key, out _) && AstExtensions.IsPassThroughKey(key);

    /// <summary>
    /// Parses a <c>Key: value</c> line. An element value ends itself (<c>;</c> or <c>}</c>) and a block value
    /// ends with <c>}</c>, so a trailing <c>;</c> is optional for both; a scalar value needs <c>;</c>.
    /// </summary>
    private PropPair ParsePropertyLine()
    {
        var loc = Current.Location;
        TryPeekKey(out var key, out var keyTokens);
        _pos += keyTokens;
        Consume(TokenKind.Colon);
        var value = ParsePropValue(key);
        if (value is ElementValue or ElementsValue)
        {
            if (Check(TokenKind.Semicolon)) Advance();
        }
        else if (Check(TokenKind.Semicolon))
        {
            Advance();
        }
        else
        {
            throw Error($"Expected ';' after '{key}: ...', got '{Current.Value}'", "a property line ends with ;");
        }
        return new PropPair(key, value, loc);
    }

    private (string Target, string? Arg) ParseArrowTarget()
    {
        Consume(TokenKind.Arrow);
        if (Current.IsKeyword("navigate"))
        {
            Advance();
            Consume(TokenKind.LeftParen);
            var view = ConsumeIdentifier();
            Consume(TokenKind.RightParen);
            return (view, null);
        }

        var target = ConsumeAnyIdentOrBinding();
        if (!Check(TokenKind.LeftParen))
            return (target, null);

        var argLoc = Current.Location;
        Advance(); // (
        if (target.StartsWith("vm.", StringComparison.Ordinal)
            && Current.Kind == TokenKind.Identifier && Current.Value == "item"
            && Peek(1).Kind == TokenKind.RightParen)
        {
            Advance();
            Consume(TokenKind.RightParen);
            return (target, "item");
        }

        var parts = new List<string>();
        while (!AtEnd() && !Check(TokenKind.RightParen) && !Check(TokenKind.Semicolon) && !Check(TokenKind.LeftBrace))
        {
            parts.Add(Current.Value);
            Advance();
        }
        Diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error, "FS1052",
            $"Invalid command argument '({string.Join("", parts)})' after '{target}'; only (item) is supported",
            argLoc,
            "pass the current row with -> vm.XCommand(item) inside a table or list"));
        if (Check(TokenKind.RightParen))
            Advance();
        return (target, null);
    }

    private Element ParseXamlEscape(SourceLocation location)
    {
        if (Check(TokenKind.RawXaml))
        {
            var content = Current.Value;
            Advance();
            return new Element("xaml", [], new RawXamlBody(content), null, null, null, null, location);
        }

        // xaml fill { ... } / xaml width: 200 { ... }: the escape takes no props, '{' must follow 'xaml' directly.
        if (!Check(TokenKind.LeftBrace) && Current.Kind is TokenKind.Identifier or TokenKind.Keyword)
        {
            var written = Current.Value + Peek(1).Kind switch
            {
                TokenKind.Colon => ": ...",
                TokenKind.LeftParen => "(...)",
                _ => "",
            };
            throw new FlagstaffParseException(
                $"'xaml' takes no props or conditions ('{Current.Value}'): '{{' must follow 'xaml' directly",
                Current.Location, "FS1051",
                $"wrap the escape instead: stack {written} {{ xaml {{ ... }} }}");
        }

        Consume(TokenKind.LeftBrace);
        int depth = 1;
        var parts = new List<string>();
        while (!AtEnd() && depth > 0)
        {
            if (Check(TokenKind.LeftBrace)) depth++;
            if (Check(TokenKind.RightBrace)) { depth--; if (depth == 0) break; }
            parts.Add(Current.Value);
            Advance();
        }
        Consume(TokenKind.RightBrace);
        return new Element("xaml", [], new RawXamlBody(string.Join(" ", parts)), null, null, null, null, location);
    }

    /// <summary>
    /// Parses <c>{ children }</c>; <c>Key: value</c> lines are appended to <paramref name="ownerProps"/> and
    /// <c>on Event -> target;</c> lines to <paramref name="ownerEvents"/>, exactly as if written in the header.
    /// </summary>
    private BlockBody ParseBlock(List<PropPair> ownerProps, List<EventBinding> ownerEvents)
    {
        Consume(TokenKind.LeftBrace);
        var children = new List<ElementOrText>();
        while (!Check(TokenKind.RightBrace))
        {
            if (AtEnd())
                throw Error("Unexpected end of file; missing '}'");
            if (IsPropertyLine())
            {
                ownerProps.Add(ParsePropertyLine());
            }
            else if (Current.IsKeyword("on"))
            {
                ownerEvents.Add(ParseEvent());
                Consume(TokenKind.Semicolon);
            }
            else if (Check(TokenKind.StringLiteral))
            {
                children.Add(new TextContent(ConsumeString()));
                if (Check(TokenKind.Semicolon)) Advance(); // optional trailing semicolon
            }
            else if (ParseElement() is { } element)
            {
                children.Add(new ChildElement(element));
            }
        }
        Consume(TokenKind.RightBrace);
        return new BlockBody(children);
    }

    /// <summary><c>Key: { el; el; }</c>: a block of elements for a collection property element.</summary>
    private ElementsValue ParseElementList()
    {
        Consume(TokenKind.LeftBrace);
        var elements = new List<Element>();
        while (!Check(TokenKind.RightBrace))
        {
            if (AtEnd())
                throw Error("Unexpected end of file; missing '}'");
            if (TryPeekKey(out var key, out _))
                throw Error(
                    $"'{key}:' cannot appear directly in a property block; it needs an element to belong to",
                    "a Key: { ... } block holds elements only, e.g. Page.Resources: { Style TargetType: Button { ... } }");
            if (Check(TokenKind.StringLiteral))
                throw Error("Expected an element in a property block, got a string",
                    "a Key: { ... } block holds elements only; write Key: \"text\"; for a string value");
            if (ParseElement() is { } element)
                elements.Add(element);
        }
        Consume(TokenKind.RightBrace);
        return new ElementsValue(elements);
    }

    /// <summary>
    /// Error recovery after an unknown keyword: skips to the end of the element
    /// (the next ';' at depth 0, or past a balanced { } block), without consuming a parent's '}'.
    /// </summary>
    private void SkipToElementEnd()
    {
        int depth = 0;
        while (!AtEnd())
        {
            if (Check(TokenKind.LeftBrace))
            {
                depth++;
                Advance();
                continue;
            }
            if (Check(TokenKind.RightBrace))
            {
                if (depth == 0) return;
                depth--;
                Advance();
                if (depth == 0) return;
                continue;
            }
            if (Check(TokenKind.Semicolon) && depth == 0)
            {
                Advance();
                return;
            }
            Advance();
        }
    }

    // ── Clauses ────────────────────────────────────────────────────

    private CondExpr ParseConditionClause(string keyword)
    {
        Expect(keyword);
        Consume(TokenKind.LeftParen);
        var cond = ParseCondition();
        Consume(TokenKind.RightParen);
        return cond;
    }

    /// <summary>Parses a condition expression (the contents of <c>when(...)</c> / <c>enabled(...)</c>).</summary>
    private CondExpr ParseCondition() => ParseOr();

    private CondExpr ParseOr()
    {
        var left = ParseAnd();
        while (Check(TokenKind.OrOr))
        {
            Advance();
            var right = ParseAnd();
            left = new BinaryCond(left, LogicalOp.Or, right, left.Location);
        }
        return left;
    }

    private CondExpr ParseAnd()
    {
        var left = ParseUnary();
        while (Check(TokenKind.AndAnd))
        {
            Advance();
            var right = ParseUnary();
            left = new BinaryCond(left, LogicalOp.And, right, left.Location);
        }
        return left;
    }

    private CondExpr ParseUnary()
    {
        if (Check(TokenKind.Bang))
        {
            var loc = Current.Location;
            Advance();
            return new NotCond(ParseUnary(), loc);
        }
        return ParsePrimary();
    }

    private CondExpr ParsePrimary()
    {
        if (Check(TokenKind.LeftParen))
        {
            Advance();
            var inner = ParseCondition();
            Consume(TokenKind.RightParen);
            return inner;
        }

        var left = ParseOperand();
        if (IsComparisonOp())
        {
            var op = Current.Kind switch
            {
                TokenKind.Greater => ComparisonOp.Gt,
                TokenKind.Less => ComparisonOp.Lt,
                TokenKind.GreaterEqual => ComparisonOp.Gte,
                TokenKind.LessEqual => ComparisonOp.Lte,
                TokenKind.EqualEqual => ComparisonOp.Eq,
                _ => ComparisonOp.Neq,
            };
            Advance();
            var right = ParseOperand();
            return new CompareCond(left, op, right, left.Location);
        }

        if (left is PathOperand path)
            return new PathCond(path.Path, path.Location);

        throw new FlagstaffParseException(
            "Expected a comparison operator after a literal in a condition",
            left.Location, "FS1051", "conditions are vm.X, item.X or comparisons like vm.Count > 0");
    }

    private Operand ParseOperand()
    {
        var loc = Current.Location;
        if (IsBindingStart())
        {
            var binding = (BindingValue)ParseBindingValue();
            var path = binding.Property.StartsWith("item.", StringComparison.Ordinal)
                ? binding.Property
                : "vm." + binding.Property;
            return new PathOperand(path, loc);
        }

        PropValue? literal = Current.Kind switch
        {
            TokenKind.StringLiteral => new StringValue(Current.Value),
            TokenKind.IntLiteral => new IntValue(int.Parse(Current.Value, CultureInfo.InvariantCulture)),
            TokenKind.FloatLiteral => new FloatValue(double.Parse(Current.Value, CultureInfo.InvariantCulture)),
            _ when Current.IsKeyword("true") => new BoolValue(true),
            _ when Current.IsKeyword("false") => new BoolValue(false),
            _ when Current.IsKeyword("null") => new NullValue(),
            _ => null,
        };
        if (literal is null)
            throw Error(
                $"Expected vm.X, item.X or a literal in condition, got '{Current.Value}'",
                hint: "conditions combine vm.X / item.X with literals, ! && || and > < >= <= == !=");
        Advance();
        return new LiteralOperand(literal, loc);
    }

    private FormatClause ParseFormat()
    {
        Expect("format");
        Consume(TokenKind.Colon);
        var kind = Current.Value switch
        {
            "currency" => FormatKind.Currency,
            "date" => FormatKind.Date,
            "percent" => FormatKind.Percent,
            "number" => FormatKind.Number,
            _ => throw new FlagstaffParseException(
                $"Invalid format '{Current.Value}'", Current.Location, "FS1003",
                "allowed: currency, date, percent, number" + DidYouMeanSuffix(Current.Value, ["currency", "date", "percent", "number"]))
        };
        Advance();
        int? precision = null;
        if (kind == FormatKind.Number && Check(TokenKind.LeftParen))
        {
            Advance();
            precision = int.Parse(ConsumeValue(TokenKind.IntLiteral), CultureInfo.InvariantCulture);
            Consume(TokenKind.RightParen);
        }
        return new FormatClause(kind, precision);
    }

    private static string DidYouMeanSuffix(string input, IEnumerable<string> candidates) =>
        SemanticValidator.Closest(input, candidates) is { } s ? $"; did you mean '{s}'?" : "";

    // ── Property parsing ───────────────────────────────────────────

    private List<PropPair> ParseInlineProps()
    {
        var props = new List<PropPair>();
        while (Current.Kind is TokenKind.Keyword or TokenKind.Identifier
               && Peek(1).Kind == TokenKind.Colon
               && Current.Value is not "format")
        {
            var loc = Current.Location;
            var key = Current.Value;
            Advance();
            Consume(TokenKind.Colon);
            var value = ParsePropValue();
            props.Add(new PropPair(key, value, loc));
        }
        return props;
    }

    /// <summary>
    /// Parses a property value. Under a pass-through key (<paramref name="key"/> starts uppercase), a PascalCase
    /// identifier or <c>prefix.Type</c> starts an element value (<c>Flyout: MenuFlyout { }</c>) and <c>{</c> starts
    /// an element block (<c>Page.Resources: { ... }</c>), except under type-name keys (<see cref="IsTypeNameKey"/>,
    /// <c>TargetType: Button</c>), whose PascalCase value is a type name. Under lowercase keys a PascalCase
    /// identifier stays an identifier (<c>name: SearchBox</c>).
    /// </summary>
    private PropValue ParsePropValue(string? key = null)
    {
        if (IsBindingStart())
            return ParseBindingValue();

        if (Check(TokenKind.LeftBracket))
            return ParseList();

        if (Peek(1).Kind == TokenKind.LeftParen && Current.Kind == TokenKind.Keyword
            && Current.Value is "theme" or "resource" or "load" or "ref")
            return ParseFunctionValue();

        if (key is not null && AstExtensions.IsPassThroughKey(key))
        {
            if (Check(TokenKind.LeftBrace))
                return ParseElementList();
            if (IsTypeNameKey(key) && Check(TokenKind.Identifier))
            {
                var typeName = ConsumeAnyIdent();
                if (Check(TokenKind.Dot) && Peek(1).Kind == TokenKind.Identifier)
                {
                    Advance();
                    typeName += "." + ConsumeAnyIdent();
                }
                return new IdentValue(typeName);
            }
            if (IsGenericTypeStart())
            {
                // `Orientation: Horizontal;` / `Fill: Red` followed by a clause: a bare name with nothing of its
                // own (no props, no block) is an identifier; the manifest pass resolves it (enum, color, type).
                // A name the manifest knows as a value and not as a type (`Orientation: Horizontal Spacing: 8 { }`,
                // `DisplayMode: Overlay OpenPaneLength: 280 { }`) is a bare name too, so the following keys and block
                // stay on the owner (see GenericValidator.IsBareValueName).
                var nameTokens = AstExtensions.IsPascalCase(Current.Value) ? 1 : 3;
                if (IsBareNameEnd(Peek(nameTokens), Peek(nameTokens + 1))
                    || (nameTokens == 1 && GenericValidator.IsBareValueName(Current.Value)))
                {
                    var name = string.Concat(Enumerable.Range(0, nameTokens).Select(i => Peek(i).Value));
                    _pos += nameTokens;
                    return new IdentValue(name);
                }
                return new ElementValue(ParseElement()!);
            }
        }

        return Current.Kind switch
        {
            TokenKind.StringLiteral => new StringValue(ConsumeString()),
            TokenKind.IntLiteral => ParseIntOrRange(),
            TokenKind.FloatLiteral => new FloatValue(double.Parse(ConsumeValue(TokenKind.FloatLiteral), CultureInfo.InvariantCulture)),
            TokenKind.ColorLiteral => new ColorValue(ConsumeValue(TokenKind.ColorLiteral)),
            TokenKind.Star => new IdentValue(ConsumeValue(TokenKind.Star)), // width: * / 2* (see XamlGenerator.Common.cs)
            _ when Current.IsKeyword("true") => ConsumeAndReturn(new BoolValue(true)),
            _ when Current.IsKeyword("false") => ConsumeAndReturn(new BoolValue(false)),
            _ when Current.IsKeyword("null") => ConsumeAndReturn(new NullValue()),
            TokenKind.Keyword or TokenKind.Identifier => new IdentValue(ConsumeAnyIdent()),
            _ => throw Error($"Expected property value, got '{Current.Value}' ({Current.Kind})")
        };
    }

    /// <summary>
    /// Keys whose value is a type name rather than an element: the last segment ends in <c>Type</c>
    /// (<c>TargetType: Button</c>, <c>DataType: Order</c>).
    /// </summary>
    private static bool IsBareNameEnd(Token next, Token after) =>
        next.Kind is TokenKind.Semicolon or TokenKind.RightBrace or TokenKind.Arrow or TokenKind.EndOfFile
        || (next.Kind == TokenKind.Keyword && next.Value is "when" or "format" or "of" or "on")
        || (next.Kind == TokenKind.Keyword && next.Value == "enabled" && after.Kind == TokenKind.LeftParen);

    /// <summary>Keys whose bare value names a type (<c>TargetType</c>) or a property (<c>Setter Property: Padding</c>).</summary>
    private static bool IsTypeNameKey(string key) =>
        key[(key.LastIndexOf('.') + 1)..] is var member && (member.EndsWith("Type", StringComparison.Ordinal) || member == "Property");

    /// <summary><c>theme(Key)</c>, <c>resource(Key)</c>, <c>load(vm.X)</c>, <c>ref(Name)[.Member.Path]</c>.</summary>
    private PropValue ParseFunctionValue()
    {
        var form = Current.Value;
        Advance();
        Consume(TokenKind.LeftParen);
        switch (form)
        {
            case "load":
                if (!IsBindingStart())
                    throw Error($"Expected vm.X inside load(...), got '{Current.Value}'", "write load(vm.ShowDetails)");
                var load = new LoadValue(((BindingValue)ParseBindingValue()).Property);
                Consume(TokenKind.RightParen);
                return load;
            case "ref":
                if (Current.Kind is not (TokenKind.Identifier or TokenKind.Keyword))
                    throw Error($"Expected an element name inside ref(...), got '{Current.Value}'",
                        "write ref(Nav) for an element declared with name: Nav");
                var name = ConsumeAnyIdent();
                Consume(TokenKind.RightParen);
                var path = new List<string>();
                while (Check(TokenKind.Dot) && Peek(1).Kind is TokenKind.Identifier or TokenKind.Keyword)
                {
                    Advance();
                    path.Add(ConsumeAnyIdent());
                }
                return new RefValue(name, path.Count > 0 ? string.Join('.', path) : null);
            default:
                if (Current.Kind is not (TokenKind.Identifier or TokenKind.Keyword))
                    throw Error($"Expected a resource key inside {form}(...), got '{Current.Value}'", $"write {form}(KeyName)");
                var resourceKey = ConsumeAnyIdent();
                Consume(TokenKind.RightParen);
                return form == "theme" ? new ThemeResourceValue(resourceKey) : new StaticResourceValue(resourceKey);
        }
    }

    private ListValue ParseList()
    {
        Consume(TokenKind.LeftBracket);
        var items = new List<PropValue>();
        while (!Check(TokenKind.RightBracket))
        {
            items.Add(ParsePropValue());
            if (Check(TokenKind.Comma))
                Advance();
            else if (!Check(TokenKind.RightBracket))
                throw Error($"Expected ',' or ']' in list, got '{Current.Value}'");
        }
        Consume(TokenKind.RightBracket);
        return new ListValue(items);
    }

    private PropValue ParseIntOrRange()
    {
        var val = int.Parse(ConsumeValue(TokenKind.IntLiteral), CultureInfo.InvariantCulture);
        if (Check(TokenKind.DotDot))
        {
            Advance();
            var end = int.Parse(ConsumeValue(TokenKind.IntLiteral), CultureInfo.InvariantCulture);
            return new RangeValue(val, end);
        }
        return new IntValue(val);
    }

    private bool IsBindingStart() =>
        Peek(1).Kind == TokenKind.Dot
        && (Current.IsKeyword("vm") || (Current.Kind == TokenKind.Identifier && Current.Value == "item"));

    /// <summary>Parses <c>vm.X</c> (→ <c>BindingValue("X")</c>) or <c>item.X</c> (→ <c>BindingValue("item.X")</c>).</summary>
    private PropValue ParseBindingValue()
    {
        var isItem = !Current.IsKeyword("vm");
        Advance(); // vm / item
        Consume(TokenKind.Dot);
        var name = ConsumeAnyIdent();
        if (Check(TokenKind.Dot) && Peek(1).Kind is TokenKind.Identifier or TokenKind.Keyword)
        {
            var root = isItem ? "item" : "vm";
            var member = Peek(1).Value;
            throw Error(
                $"Nested binding path '{root}.{name}.{member}' is not supported",
                $"nested paths are not supported; expose `{member}` on the {(isItem ? "row type" : "ViewModel")} (e.g. {root}.{name}{member})");
        }
        return new BindingValue(isItem ? "item." + name : name);
    }

    // ── Helpers ────────────────────────────────────────────────────

    private Token Current => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];
    private bool AtEnd() => Current.Kind == TokenKind.EndOfFile;
    private bool Check(TokenKind kind) => Current.Kind == kind;

    private Token Peek(int offset)
    {
        var idx = _pos + offset;
        if (idx < 0) return _tokens[0];
        return idx < _tokens.Count ? _tokens[idx] : _tokens[^1];
    }

    private void Advance() => _pos++;

    private void Expect(string keyword)
    {
        if (!Current.IsKeyword(keyword))
            throw Error($"Expected '{keyword}', got '{Current.Value}'");
        Advance();
    }

    private void Consume(TokenKind kind)
    {
        if (Current.Kind != kind)
            throw Error($"Expected {kind}, got {Current.Kind} ('{Current.Value}')");
        Advance();
    }

    private string ConsumeValue(TokenKind kind)
    {
        var val = Current.Value;
        Consume(kind);
        return val;
    }

    private string ConsumeString()
    {
        return ConsumeValue(TokenKind.StringLiteral);
    }

    private string ConsumeIdentifier()
    {
        if (Current.Kind is not (TokenKind.Identifier or TokenKind.Keyword))
            throw Error($"Expected identifier, got {Current.Kind} ('{Current.Value}')");
        var val = Current.Value;
        Advance();
        return val;
    }

    private string ConsumeAnyIdent()
    {
        if (Current.Kind is TokenKind.Identifier or TokenKind.Keyword)
        {
            var val = Current.Value;
            Advance();
            return val;
        }
        throw Error($"Expected identifier, got {Current.Kind}");
    }

    private string ConsumeAnyIdentOrBinding()
    {
        if (Current.IsKeyword("vm") && Peek(1).Kind == TokenKind.Dot)
        {
            Advance(); // vm
            Consume(TokenKind.Dot);
            return "vm." + ConsumeAnyIdent();
        }
        return ConsumeAnyIdent();
    }

    private PropValue ConsumeAndReturn(PropValue v)
    {
        Advance();
        return v;
    }

    private bool IsComparisonOp() => Current.Kind is
        TokenKind.Greater or TokenKind.Less or
        TokenKind.GreaterEqual or TokenKind.LessEqual or
        TokenKind.EqualEqual or TokenKind.BangEqual;

    private FlagstaffParseException Error(string message, string? hint = null)
    {
        // A tolerant lexer turns unexpected characters into Invalid tokens; report them as FS1050.
        if (Current.Kind == TokenKind.Invalid)
            return new FlagstaffParseException($"Unexpected character '{Current.Value}'", Current.Location, "FS1050", Lexer.HintFor(Current.Value));
        return new FlagstaffParseException(message, Current.Location, "FS1051", hint);
    }
}
