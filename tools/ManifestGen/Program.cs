using System.Text;
using System.Xml.Linq;
using Flagstaff.ManifestGen;

// ManifestGen: WinUI 3 metadata (.winmd) -> src/Flagstaff.Compiler/Manifest/winui-manifest.json[.gz]
//
//   dotnet run --project tools/ManifestGen                 regenerate the committed manifest
//   dotnet run --project tools/ManifestGen -- --check      exit 1 when the committed manifest is stale
//   dotnet run --project tools/ManifestGen -- --stats      print counts and short-name collisions
//   --winmd <path> (repeatable)   read these files instead of the WindowsAppSDK package in the NuGet cache
//   --out <path>                  output path (default src/Flagstaff.Compiler/Manifest/winui-manifest.json)

var winmds = new List<string>();
string? outPath = null;
bool check = false, stats = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--winmd" when i + 1 < args.Length: winmds.Add(Path.GetFullPath(args[++i])); break;
        case "--out" when i + 1 < args.Length: outPath = Path.GetFullPath(args[++i]); break;
        case "--check": check = true; break;
        case "--stats": stats = true; break;
        case "-h" or "--help":
            Console.WriteLine("usage: ManifestGen [--winmd <path>]... [--out <path>] [--check] [--stats]");
            return 0;
        default:
            Console.Error.WriteLine($"ManifestGen: unknown argument '{args[i]}' (see --help)");
            return 2;
    }
}

var repoRoot = FindRepoRoot();
outPath ??= Path.Combine(repoRoot ?? Directory.GetCurrentDirectory(), "src", "Flagstaff.Compiler", "Manifest", "winui-manifest.json");
if (outPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) outPath = outPath[..^3];

List<string> sources;
if (winmds.Count == 0)
{
    if (repoRoot is null)
    {
        Console.Error.WriteLine("ManifestGen: cannot find Directory.Packages.props; pass --winmd explicitly.");
        return 2;
    }
    var (version, files) = ResolveDefaultWinmds(repoRoot);
    if (files is null) return 2;
    winmds.AddRange(files);
    sources = files.Select(f => $"Microsoft.WindowsAppSDK {version}: {Path.GetFileName(f)}").ToList();
    // Windows.* enums referenced by WinUI properties (KeyboardAccelerator.Key: Windows.System.VirtualKey, ...): read
    // for their members only; types outside Microsoft.UI.Xaml* never become manifest types.
    var (sdkVersion, sdkWinmd) = ResolveSdkContractWinmd(repoRoot);
    if (sdkWinmd is null) return 2;
    winmds.Add(sdkWinmd);
    sources.Add($"Microsoft.Windows.SDK.NET.Ref {sdkVersion}: {Path.GetFileName(sdkWinmd)}");
}
else
{
    sources = winmds.Select(Path.GetFileName).Select(n => n!).ToList();
}

ManifestModel model;
using (var reader = new WinMdReader(winmds))
    model = reader.Read(sources);

var json = ManifestWriter.ToJson(model);
var useGzip = Encoding.UTF8.GetByteCount(json) > ManifestWriter.GzipThresholdBytes;
var gzPath = outPath + ".gz";

if (stats || check) PrintStats(model, json, useGzip);

if (check)
{
    string? committed = null;
    if (File.Exists(gzPath)) committed = ManifestWriter.Gunzip(File.ReadAllBytes(gzPath));
    else if (File.Exists(outPath)) committed = File.ReadAllText(outPath, Encoding.UTF8);

    if (committed is null)
    {
        Console.Error.WriteLine($"ManifestGen --check: no committed manifest at {outPath}[.gz]. Run: dotnet run --project tools/ManifestGen");
        return 1;
    }
    committed = committed.Replace("\r\n", "\n", StringComparison.Ordinal);
    if (committed != json)
    {
        Console.Error.WriteLine("ManifestGen --check: the committed manifest is stale.");
        Console.Error.WriteLine(Diff(committed, json));
        Console.Error.WriteLine("Regenerate with: dotnet run --project tools/ManifestGen");
        return 1;
    }
    var expectGz = useGzip;
    if (File.Exists(gzPath) != expectGz)
    {
        Console.Error.WriteLine($"ManifestGen --check: the manifest should be committed as {(expectGz ? gzPath : outPath)}.");
        return 1;
    }
    Console.WriteLine("ManifestGen --check: manifest is up to date.");
    return 0;
}

if (stats && !args.Contains("--out")) return 0; // --stats alone reports without writing

Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
if (useGzip)
{
    File.WriteAllBytes(gzPath, ManifestWriter.Gzip(json));
    if (File.Exists(outPath)) File.Delete(outPath);
    Console.WriteLine($"Wrote {gzPath}");
}
else
{
    File.WriteAllText(outPath, json, new UTF8Encoding(false));
    if (File.Exists(gzPath)) File.Delete(gzPath);
    Console.WriteLine($"Wrote {outPath}");
}
return 0;

// ── helpers ──────────────────────────────────────────────────────────────────

static string? FindRepoRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props"))) return dir.FullName;
    }
    return null;
}

static (string Version, List<string>? Files) ResolveDefaultWinmds(string repoRoot)
{
    var props = XDocument.Load(Path.Combine(repoRoot, "Directory.Packages.props"));
    var version = props.Descendants("PackageVersion")
        .FirstOrDefault(e => (string?)e.Attribute("Include") == "Microsoft.WindowsAppSDK")?.Attribute("Version")?.Value;
    if (version is null)
    {
        Console.Error.WriteLine("ManifestGen: Microsoft.WindowsAppSDK is not in Directory.Packages.props.");
        return ("?", null);
    }

    var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
    if (string.IsNullOrEmpty(cache))
        cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    var lib = Path.Combine(cache, "microsoft.windowsappsdk", version.ToLowerInvariant(), "lib");

    // Microsoft.UI.Xaml.winmd lives in lib/uap10.0; Microsoft.UI.winmd in the newest lib/uap10.0.N folder.
    var files = new List<string>();
    foreach (var name in new[] { "Microsoft.UI.Xaml.winmd", "Microsoft.UI.winmd" })
    {
        var found = Directory.Exists(lib)
            ? Directory.GetDirectories(lib, "uap10.0*")
                .Select(d => Path.Combine(d, name))
                .Where(File.Exists)
                .OrderByDescending(p => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(p))!.Replace("uap", ""), out var v) ? v : new Version(0, 0))
                .FirstOrDefault()
            : null;
        if (found is null)
        {
            Console.Error.WriteLine($"ManifestGen: {name} not found under {lib}. Restore first (dotnet restore tools/ManifestGen) or pass --winmd.");
            return (version, null);
        }
        files.Add(found);
    }
    return (version, files);
}

static (string Version, string? File) ResolveSdkContractWinmd(string repoRoot)
{
    var props = XDocument.Load(Path.Combine(repoRoot, "Directory.Packages.props"));
    var version = props.Descendants("PackageVersion")
        .FirstOrDefault(e => (string?)e.Attribute("Include") == "Microsoft.Windows.SDK.NET.Ref")?.Attribute("Version")?.Value;
    if (version is null)
    {
        Console.Error.WriteLine("ManifestGen: Microsoft.Windows.SDK.NET.Ref is not in Directory.Packages.props.");
        return ("?", null);
    }
    var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
    if (string.IsNullOrEmpty(cache))
        cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    var file = Path.Combine(cache, "microsoft.windows.sdk.net.ref", version.ToLowerInvariant(), "winmd", "Windows.Foundation.UniversalApiContract.winmd");
    if (!File.Exists(file))
    {
        Console.Error.WriteLine($"ManifestGen: {file} not found. Restore first (dotnet restore tools/ManifestGen) or pass --winmd.");
        return (version, null);
    }
    return (version, file);
}

static void PrintStats(ManifestModel model, string json, bool gz)
{
    var props = model.Types.Sum(t => t.Properties.Count);
    var dps = model.Types.Sum(t => t.Properties.Count(p => p.IsDependencyProperty));
    var attached = model.Types.Sum(t => t.AttachedProperties.Count) + model.AttachedOwners.Sum(o => o.AttachedProperties.Count);
    var events = model.Types.Sum(t => t.Events.Count);
    var bytes = Encoding.UTF8.GetByteCount(json);
    Console.WriteLine($"types:               {model.Types.Count} ({model.Types.Count(t => t.IsControl)} controls, {model.Types.Count(t => t.IsUIElement)} UIElements, {model.Types.Count(t => t.IsAbstract)} abstract)");
    Console.WriteLine($"properties:          {props} ({dps} dependency properties)");
    Console.WriteLine($"attached properties: {attached} ({model.AttachedOwners.Count} non-DependencyObject owners)");
    Console.WriteLine($"events:              {events}");
    Console.WriteLine($"value types:         {model.ValueTypes.Count} ({model.ValueTypes.Count(v => v.IsEnum)} enums)");
    Console.WriteLine($"json size:           {bytes:N0} bytes{(gz ? " (> 2 MB, written gzipped)" : "")}");

    var collisions = model.Types.GroupBy(t => t.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();
    Console.WriteLine($"short-name collisions: {collisions.Count}");
    foreach (var g in collisions.OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        var winner = g.OrderBy(t => NamespaceRank(t.Namespace)).ThenBy(t => t.Namespace, StringComparer.Ordinal).First();
        Console.WriteLine($"  {g.Key}: {string.Join(", ", g.Select(t => t.Namespace))} -> {winner.Namespace}");
    }
}

// Must match ControlManifest's short-name preference.
static int NamespaceRank(string ns) => ns switch
{
    "Microsoft.UI.Xaml.Controls" => 0,
    "Microsoft.UI.Xaml" => 1,
    _ => 2,
};

static string Diff(string a, string b)
{
    var la = a.Split('\n');
    var lb = b.Split('\n');
    var sb = new StringBuilder();
    sb.AppendLine($"  committed: {la.Length} lines, regenerated: {lb.Length} lines");
    var n = Math.Min(la.Length, lb.Length);
    var shown = 0;
    for (var i = 0; i < n && shown < 10; i++)
    {
        if (la[i] == lb[i]) continue;
        sb.AppendLine($"  line {i + 1}:");
        sb.AppendLine($"    - {la[i].Trim()}");
        sb.AppendLine($"    + {lb[i].Trim()}");
        shown++;
        if (shown == 1 && la.Length != lb.Length) break; // insertion/removal: further lines are shifted
    }
    return sb.ToString().TrimEnd();
}
