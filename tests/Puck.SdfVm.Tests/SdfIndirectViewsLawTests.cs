using System.Text.RegularExpressions;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The indirect probe and cell diagnostics read stored records without evaluating the field. Every field call site is
/// an inlined interpreter, so these debug views (<c>indirect/sdf-indirect-read.hlsli</c>, called
/// from <c>debug/</c>) read stored records alone. The evaluating functions are derived from the tree: the indirect
/// module's two field calls (<c>sdfIndirectSample</c>, <c>sdfIndirectGradient</c>) and every indirect function that
/// reaches one of them.
/// </summary>
public sealed partial class SdfIndirectViewsLawTests {
    private static string Root => RepositoryPaths.Resolve(relativePath: SdfKernelInterfaces.KernelDirectory);

    private static string CodeOf(string path) => File.ReadAllText(path: Path.Combine(path1: Root, path2: path));
    private static IEnumerable<string> SourcesIn(string directory) => Directory.EnumerateFiles(path: Path.Combine(path1: Root, path2: directory), searchPattern: "*.hlsli")
        .Select(selector: path => Path.GetRelativePath(path: path, relativeTo: Root).Replace(newChar: '/', oldChar: '\\'))
        .Order(comparer: StringComparer.Ordinal);
    // Each indirect function's body, from its signature to the closing brace at the start of a line.
    private static Dictionary<string, string> Functions(IEnumerable<string> paths) {
        var functions = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var path in paths) {
            foreach (Match match in FunctionPattern().Matches(input: CodeOf(path: path))) {
                functions[match.Groups["name"].Value] = match.Groups["body"].Value;
            }
        }
        return functions;
    }
    private static HashSet<string> Evaluators(Dictionary<string, string> functions) {
        var evaluators = new HashSet<string>(collection: ["sdfIndirectSample", "sdfIndirectGradient"], comparer: StringComparer.Ordinal);
        var grew = true;

        while (grew) {
            grew = false;
            foreach (var (name, body) in functions) {
                if (!evaluators.Contains(item: name) && evaluators.Any(predicate: evaluator => Calls(body: body, name: evaluator))) {
                    grew |= evaluators.Add(item: name);
                }
            }
        }
        return evaluators;
    }
    private static bool Calls(string body, string name) => Regex.IsMatch(input: body, pattern: $@"\b{name}\s*\(");

    [Fact]
    public void TheIndirectProbeAndCellDiagnosticsReadTheCacheWithoutEvaluatingTheField() {
        var functions = Functions(paths: SourcesIn(directory: "indirect"));
        var evaluators = Evaluators(functions: functions);

        // The derivation reaches the trace's evaluating entry points, so an empty set never passes the law vacuously.
        Assert.Superset(new HashSet<string>(collection: ["sdfIndirectMarch", "sdfIndirectSegment", "sdfIndirectLaunch", "sdfIndirectProve"]), evaluators);
        var read = Functions(paths: ["indirect/sdf-indirect-read.hlsli"]);
        var views = SourcesIn(directory: "debug").Select(selector: CodeOf).Concat(second: read.Values);
        var violations = views.SelectMany(selector: code => evaluators.Where(predicate: evaluator => Calls(body: code, name: evaluator))).Distinct().Order(comparer: StringComparer.Ordinal);

        Assert.Empty(collection: violations);
    }
    [Fact]
    public void ViewsAppliesIndirectLightWithoutEvaluatingTheField() {
        var functions = Functions(paths: SourcesIn(directory: "indirect"));
        var evaluators = Evaluators(functions: functions);
        // The receiver pass owns every field query of indirect light; views reads its answer and the bank.
        var apply = CodeOf(path: "indirect/sdf-indirect-apply.hlsli");
        var violations = evaluators.Where(predicate: evaluator => Calls(body: apply, name: evaluator)).Order(comparer: StringComparer.Ordinal);

        Assert.Empty(collection: violations);
        Assert.Contains(expectedSubstring: "sdfIndirectReceive(", actualString: CodeOf(path: "indirect/sdf-indirect-receiver.hlsli"));
        Assert.Contains(expected: "sdfIndirectReceive", collection: evaluators);
        Assert.DoesNotContain(expectedSubstring: "#include \"sdf-indirect-near.hlsli\"", actualString: apply);
    }

    [GeneratedRegex(pattern: @"^[A-Za-z_][\w<>]*\s+(?<name>sdfIndirect\w+)\([^)]*\)\s*\{(?<body>.*?)^\}", options: RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FunctionPattern();
}
