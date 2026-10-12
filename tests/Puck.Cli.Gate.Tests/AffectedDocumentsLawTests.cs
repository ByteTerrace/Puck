using Puck.Cli.Affected;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Gate.Tests;

/// <summary>
/// Laws for <see cref="AffectedDocuments"/>: a graph document reaches the pass sources it declares, resolved beside it as
/// the loader resolves them, and the includes they reach, and nothing it does not name; and a file a canary's documents
/// reach chooses that canary. The laws that read the real tree's canaries and its shipped game modules are quarantined
/// with them in <c>experimental/tests/Puck.Cli.Gate.Tests</c>.
/// </summary>
public sealed class AffectedDocumentsLawTests {
    [Fact]
    public void AGraphDocumentReachesThePassSourcesItNamesAndTheirIncludes() {
        using var scratch = new TemporaryDirectory(prefix: "puck-affected-graph-");

        var passes = Directory.CreateDirectory(path: Path.Combine(path1: scratch.RootPath, path2: "passes"));
        var named = Path.Combine(path1: passes.FullName, path2: "fill.hlsl");
        var unnamed = Path.Combine(path1: passes.FullName, path2: "other.hlsl");
        var include = Path.Combine(path1: passes.FullName, path2: "common.hlsli");
        var unincluded = Path.Combine(path1: passes.FullName, path2: "unused.hlsli");
        var graph = Path.Combine(path1: scratch.RootPath, path2: "fill.graph.json");

        File.WriteAllText(contents: "#include \"common.hlsli\"\n[numthreads(8, 8, 1)] void main() {}", path: named);
        File.WriteAllText(contents: "static const uint Fill = 1;", path: include);
        File.WriteAllText(contents: "static const uint Unused = 1;", path: unincluded);
        File.WriteAllText(contents: "[numthreads(8, 8, 1)] void main() {}", path: unnamed);
        File.WriteAllText(
            contents: """
                {
                  "$schema": "puck.render.graph.v1",
                  "name": "fill",
                  "resources": [ { "name": "out", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "relative", "width": 1, "height": 1 } } ],
                  "passes": [ { "name": "fill", "kind": "compute", "source": "passes/fill.hlsl", "entryPoint": "main", "outputs": [ { "name": "out" } ] } ],
                  "outputs": [ "out" ]
                }
                """,
            path: graph
        );

        var tree = new AffectedWorkingTree(root: scratch.RootPath);

        Assert.Equal(actual: AffectedDocuments.PassSources(graph: "fill.graph.json", tree: tree), expected: ["passes/fill.hlsl"]);
        Assert.Equal(
            actual: AffectedDocuments.Reach(path: "fill.graph.json", tree: tree).Order(comparer: StringComparer.Ordinal),
            expected: ["fill.graph.json", "passes/common.hlsli", "passes/fill.hlsl"]
        );
    }
    [Fact]
    public void AFileACanarysDocumentsReachChoosesItAndIsMapped() {
        var plan = AffectedSelection.Select(
            canaries: [new AffectedCanary(Directory: "tests/Canaries/ink", Files: ["tests/Canaries/ink/fixture.world.json"], Id: "ink", RequiresGpu: true)],
            canariesReaching: static path => ((path == "src/World/Assets/ink.hlsl") ? new HashSet<string>(collection: ["ink"]) : new HashSet<string>()),
            catalogInputs: static (_, _) => false,
            changed: ["src/World/Assets/ink.hlsl"],
            consumersOf: static _ => [],
            coverage: new Dictionary<string, IReadOnlySet<string>>(),
            declaresTests: static _ => false,
            projects: [new AffectedProject(Directory: "src/World", IsSuite: false, Name: "World", References: [])],
            standInsFor: static _ => [],
            worldClosure: new HashSet<string>(collection: ["World"], comparer: StringComparer.OrdinalIgnoreCase),
            worldInput: static _ => false
        );

        Assert.Equal(actual: plan.Canaries, expected: ["ink"]);
        Assert.Empty(collection: plan.Unmapped);
    }
}
