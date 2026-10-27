using System.Text.Json.Nodes;
using Flagstaff.Compiler;

namespace Flagstaff.Cli;

/// <summary>
/// The <c>contract</c> envelope field of <c>check</c> and <c>stubs</c>: per view, what the generated page and ViewModel
/// declare (so the hand-written half does not redeclare it) and the delegates to implement; plus the item types.
/// </summary>
internal static class ContractOutput
{
    public static void Attach(CommandResult result, CompilationResult compilation, Func<string, string> display)
    {
        result.Extra["contract"] = new JsonArray(compilation.Contracts.Select(c => (JsonNode)ToJson(c, display)).ToArray());
        result.Extra["itemTypes"] = new JsonArray(compilation.ItemTypes.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.TypeName,
            ["file"] = "Generated/" + t.File,
            ["partial"] = true,
            ["properties"] = new JsonArray(t.Properties.Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name,
                ["type"] = p.CSharpType,
                ["inferredType"] = p.InferredType,
                ["declaredByYou"] = t.DeclaredByUser?.Contains(p.Name) == true ? true : null,
            }).ToArray()),
        }).ToArray());
    }

    public static JsonObject ToJson(ViewContract c, Func<string, string> display) => new()
    {
        ["view"] = c.ViewName,
        ["source"] = string.IsNullOrEmpty(c.Location.File) ? null : display(c.Location.File),
        ["pageFile"] = "Generated/" + c.PageFile,
        ["constructors"] = Output.ToArray(c.Constructors),
        ["viewModel"] = c.ViewModelType,
        ["viewModelFile"] = c.ViewModelFile is null || c.Properties.Count + c.Commands.Count == 0 ? null : "Generated/" + c.ViewModelFile,
        ["properties"] = new JsonArray(c.Properties.Select(p => (JsonNode)new JsonObject
        {
            ["name"] = p.Name,
            ["field"] = p.FieldName,
            ["type"] = p.CSharpType,
            ["inferredType"] = p.InferredType,
            ["inferred"] = p.Confidence.ToString().ToLowerInvariant(),
            ["from"] = p.Origin,
            ["line"] = p.Location?.Line,
            ["declaredBy"] = p.DeclaredBy?.File,
        }).ToArray()),
        ["commands"] = new JsonArray(c.Commands.Select(cmd => (JsonNode)new JsonObject
        {
            ["name"] = cmd.Name,
            ["parameterType"] = cmd.ParameterType,
            ["implement"] = cmd.DeclaredBy is null && cmd.DelegateMode != DelegateMode.UserMethod ? cmd.DelegateSignature : null,
            ["from"] = cmd.Origin,
            ["line"] = cmd.Location?.Line,
            ["declaredBy"] = cmd.DeclaredBy?.File,
        }).ToArray()),
    };

    /// <summary>One text line per ViewModel: what is generated and what to implement.</summary>
    public static IEnumerable<string> TextLines(CompilationResult compilation)
    {
        foreach (var c in compilation.Contracts.Where(c => c.ViewModelType is not null && c.Properties.Count + c.Commands.Count > 0))
        {
            var generated = c.Properties.Where(p => p.DeclaredBy is null).Select(p => $"{ViewContract.ShortType(p.CSharpType)} {p.Name}")
                .Concat(c.Commands.Where(x => x.DeclaredBy is null).Select(x => x.Name));
            var implement = c.Delegates.Where(d => d.DelegateMode != DelegateMode.UserMethod)
                .Select(d => d.DelegateSignature!.Replace("private partial Task ", "", StringComparison.Ordinal)).ToList();
            yield return $"{c.ViewModelType}: generated (do not redeclare): {string.Join(", ", generated)}"
                         + (implement.Count > 0 ? $"; implement: {string.Join(", ", implement)}" : "");
        }
    }
}
