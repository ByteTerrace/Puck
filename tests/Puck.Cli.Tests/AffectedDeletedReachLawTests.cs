using Puck.Cli.Affected;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for placing a file deleted since the base through document reach in the base's tree: each canary's worlds and
/// graph documents are read from the tree the base recorded (an <see cref="IAffectedTree"/>), never the working tree, so a
/// deleted pass source a base graph document declared chooses that graph's canary, a deleted graph document a base world
/// named chooses that world's canary, and a file no base document reached stays deleted.
/// </summary>
public sealed class AffectedDeletedReachLawTests {
    private const string InkGraph = "src/Shaders/Graph/ink.graph.json";
    private const string InkSource = "src/Shaders/Graph/ink.hlsl";
    private const string Orphan = "src/Shaders/Graph/orphan.hlsl";
    private const string Resample = "src/Shaders/Graph/resample.hlsl";

    private static readonly AffectedCanary InkCanary = new(Directory: "tests/Canaries/ink", Files: ["tests/Canaries/ink/fixture.world.json"], Id: "ink", RequiresGpu: true);
    private static readonly AffectedCanary ResampleCanary = new(Directory: "tests/Canaries/resample", Files: ["tests/Canaries/resample/fixture.graph.json"], Id: "resample", RequiresGpu: true);
    private static readonly AffectedProject Shaders = new(Directory: "src/Shaders", IsSuite: false, Name: "Shaders", References: []);

    // A graph document with one compute pass reading the given source.
    private static string Graph(string name, string source) => $$"""
        {
          "$schema": "puck.render.graph.v1",
          "name": "{{name}}",
          "resources": [ { "name": "out", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "relative", "width": 1, "height": 1 } } ],
          "passes": [ { "name": "{{name}}", "kind": "compute", "source": "{{source}}", "entryPoint": "main", "outputs": [ { "name": "out" } ] } ],
          "outputs": [ "out" ]
        }
        """;

    // The tree the base recorded: the resample canary's fixture graph declaring a pass source outside its directory, the
    // ink canary's world naming a graph document outside its directory through views.graphs, and a shader nothing names.
    private static readonly AffectedMemoryTree Base = new(files: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
        ["tests/Canaries/resample/fixture.graph.json"] = Graph(name: "resample", source: "../../../src/Shaders/Graph/resample.hlsl"),
        [Resample] = "[numthreads(8, 8, 1)] void main() {}",
        ["tests/Canaries/ink/fixture.world.json"] = """
            {
              "schema": "puck.world.definition.v1",
              "documentId": "ink",
              "views": { "graphs": [ { "name": "ink", "source": "../../../src/Shaders/Graph/ink.graph.json" } ] }
            }
            """,
        [InkGraph] = Graph(name: "ink", source: "ink.hlsl"),
        [InkSource] = "[numthreads(8, 8, 1)] void main() {}",
        [Orphan] = "[numthreads(8, 8, 1)] void main() {}",
    });

    private static AffectedPlan Select(string deleted) {
        var reachedBy = AffectedDocuments.ReachedBy(canaries: [InkCanary, ResampleCanary], tree: Base);

        return AffectedSelection.Select(
            canaries: [InkCanary, ResampleCanary],
            canariesReaching: static _ => throw new InvalidOperationException(message: "A deleted file is reached through the base's documents, never the working tree's."),
            catalogInputs: static (_, _) => false,
            changed: [deleted],
            consumersOf: static _ => [],
            coverage: new Dictionary<string, IReadOnlySet<string>>(),
            declaresTests: static _ => throw new InvalidOperationException(message: "A deleted file is never read."),
            deleted: new HashSet<string>(collection: [deleted], comparer: StringComparer.Ordinal),
            projects: [Shaders],
            recorded: new Dictionary<string, IReadOnlySet<string>>(),
            recordedCanariesReaching: path => (reachedBy.TryGetValue(key: path, value: out var reaching)
                ? reaching
                : new HashSet<string>()),
            recordedStandInsFor: static _ => [],
            standInsFor: static _ => throw new InvalidOperationException(message: "A deleted file stands for what the base said, never the working tree."),
            worldClosure: new HashSet<string>(collection: ["Shaders"], comparer: StringComparer.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void ADeletedPassSourceABaseGraphDocumentDeclaredChoosesThatGraphsCanary() {
        var plan = Select(deleted: Resample);

        Assert.Equal(actual: plan.Canaries, expected: ["resample"]);
        Assert.Empty(collection: plan.Deleted);
        Assert.Empty(collection: plan.Unmapped);
    }
    [Fact]
    public void ADeletedGraphDocumentABaseWorldNamedChoosesThatWorldsCanary() {
        var plan = Select(deleted: InkGraph);

        Assert.Equal(actual: plan.Canaries, expected: ["ink"]);
        Assert.Empty(collection: plan.Deleted);
        Assert.Equal(actual: Select(deleted: InkSource).Canaries, expected: ["ink"]);
    }
    [Fact]
    public void AFileNoBaseDocumentReachedStaysDeleted() {
        var plan = Select(deleted: Orphan);

        Assert.Empty(collection: plan.Canaries);
        Assert.Equal(actual: plan.Deleted, expected: [Orphan]);
        Assert.Empty(collection: plan.Unmapped);
    }
    /// <summary>Reach in the base's tree reads nothing from disk: the tree is rooted where no file exists, so every
    /// document and source it reaches came from the tree.</summary>
    [Fact]
    public void TheBasesDocumentsAreReadFromItsTreeAlone() {
        Assert.False(condition: Directory.Exists(path: Base.Root));
        Assert.Equal(
            actual: AffectedDocuments.Reach(path: "tests/Canaries/ink/fixture.world.json", tree: Base).Order(comparer: StringComparer.Ordinal),
            expected: [InkGraph, InkSource, "tests/Canaries/ink/fixture.world.json"]
        );
    }
}
