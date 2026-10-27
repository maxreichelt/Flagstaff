namespace Flagstaff.Compiler;

/// <summary>The binding mode a generic-element property gets when the source does not say.</summary>
public enum BindingMode { OneWay, TwoWay }

/// <summary>The Flagstaff value form a manifest property accepts (drives FS1032 and value emission).</summary>
public enum ValueShape
{
    Bool, Int, Double, String, Enum, Thickness, CornerRadius, GridLength, Brush, Color, Uri, Icon, Object,
    Collection, Reference, Unknown,
}

/// <summary>
/// Whether an attached property's correctness is local to the element (the locality rule: only local attributes pass
/// through unchecked).
/// Non-local ones never pass through unchecked: <see cref="CheckedByGrid"/> is validated against the parent
/// grid's definitions, <see cref="CheckedByRef"/> takes a <c>ref(Name)</c> value resolved against the view.
/// </summary>
public enum AttachedLocality { Local, CheckedByGrid, CheckedByRef, Unknown }

/// <summary>How a <c>ref(Name)</c> value is emitted for a reference property.</summary>
public enum ReferenceEmit { None, Name, TargetPath }

/// <summary>A WinUI type deriving from <c>Microsoft.UI.Xaml.DependencyObject</c>.</summary>
/// <param name="BaseType">Full name of the base type, or null for <c>DependencyObject</c> itself.</param>
/// <param name="IsAbstract">Abstract in metadata, or no public constructor (composable bases such as
/// <c>Control</c>, <c>Panel</c>); such a type cannot be written as an element.</param>
/// <param name="ContentProperty">Declared on this type only; use <see cref="ControlManifest.ContentPropertyOf"/>.</param>
/// <param name="Properties">Declared on this type only; use <see cref="ControlManifest.TryGetProperty"/>.</param>
public sealed record ManifestType(
    string FullName, string Name, string Namespace, string? BaseType, bool IsAbstract, bool IsControl,
    bool IsUIElement, string? ContentProperty, IReadOnlyDictionary<string, ManifestProperty> Properties,
    IReadOnlyDictionary<string, ManifestAttachedProperty> AttachedProperties,
    IReadOnlyDictionary<string, ManifestEvent> Events);

/// <param name="TypeName">Normalized: <c>bool</c>, <c>int</c>, <c>double</c>, <c>string</c>, <c>float</c>,
/// <c>object</c>, <c>DateTimeOffset</c>, <c>TimeSpan</c>, <c>Uri</c>, <c>T?</c> for nullable value types,
/// otherwise the full WinRT type name (generic arguments in angle brackets).</param>
/// <param name="EnumMembers">Member names when the type is an enum known to the manifest, else null.</param>
public sealed record ManifestProperty(
    string Name, string TypeName, bool IsDependencyProperty, bool IsCollection, bool IsReadOnly,
    IReadOnlyList<string>? EnumMembers);

/// <param name="Owner">Short name of the owner type (<c>Grid</c> for <c>Grid.Row</c>).</param>
public sealed record ManifestAttachedProperty(string Owner, string Name, string TypeName);

/// <param name="ArgsTypeName">Normalized type of the handler's second parameter.</param>
public sealed record ManifestEvent(string Name, string ArgsTypeName);

/// <summary>An enum or struct used by WinUI properties (<c>Symbol</c>, <c>Orientation</c>, <c>Thickness</c>).</summary>
public sealed record ManifestValueType(
    string FullName, string Name, string Namespace, bool IsEnum, IReadOnlyList<string>? EnumMembers);
