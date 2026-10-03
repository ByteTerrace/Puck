using Puck.Cli.Affected;
using Puck.Cli.Baselines;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class AffectedBaselineLawTests {
    private static readonly AffectedProject[] Projects = [
        new(Directory: "src/Core", IsSuite: false, Name: "Core", References: []),
        .. BaselinesCommand.Artifacts.Select(selector: static artifact => new AffectedProject(
            Directory: ("tests/" + artifact.Project), Name: artifact.Project, References: ["Core"], IsSuite: true)),
    ];

    private static AffectedPlan Select(string path, bool deleted = false, bool consumer = false) => AffectedSelection.Select(
        changed: [path], projects: Projects, canaries: [], coverage: new Dictionary<string, IReadOnlySet<string>>(),
        consumersOf: _ => (consumer ? ["Puck.State.Rebuild.Corpus"] : []), worldClosure: new HashSet<string>(),
        declaresTests: static _ => false, catalogInputs: static (_, _) => false,
        standInsFor: static _ => [], canariesReaching: static _ => new HashSet<string>(),
        deleted: (deleted ? new HashSet<string>(collection: [path]) : null));

    [InlineData("src/Puck.World/Assets/worlds/game.puck")]
    [InlineData("src/Puck.World/Assets/package.puck")]
    [InlineData("worlds/game.puck")]
    [InlineData("worlds/package/modules/game.puck")]
    [InlineData("src/Puck.World.Transpiler/Samples/game.puck")]
    [InlineData("src/Puck.World.Transpiler/Samples/modules/game.puck")]
    [Theory]
    public void CorpusInputsReachTheInventoryEvenWhenDeleted(string path) {
        Assert.Contains(collection: Select(path: path).Baselines, filter: static artifact => (artifact.Name == "corpus-inventory"));
        Assert.Contains(collection: Select(path: path, deleted: true).Baselines, filter: static artifact => (artifact.Name == "corpus-inventory"));
    }
    [Fact]
    public void DocsOnlyAndUnrelatedChangesReachNoBaseline() {
        Assert.Empty(collection: Select(path: "docs/reference/cli.md").Baselines);
        Assert.Empty(collection: Select(path: "src/Unrelated/Thing.cs").Baselines);
    }
    [InlineData("Puck.State.Rebuild.Corpus", "corpus-inventory")]
    [InlineData("Puck.World.Tests", "state")]
    [InlineData("Puck.Maths.Tests", "maths-ledger")]
    [InlineData("Puck.World.Browser.Tests", "browser-parity")]
    [Theory]
    public void TheOwningTestProjectReachesItsBaseline(string project, string artifact) {
        Assert.Equal(expected: [artifact], actual: Select(path: $"tests/{project}/Law.cs").Baselines.Select(selector: static baseline => baseline.Name));
    }
    [Fact]
    public void BaselinesFollowProjectDependenciesAndConsumersInOrdinalOrder() {
        string[] all = ["browser-parity", "corpus-inventory", "maths-ledger", "state"];

        Assert.Equal(expected: all, actual: Select(path: "src/Core/Value.cs").Baselines.Select(selector: static artifact => artifact.Name));
        Assert.Equal(expected: all, actual: Select(path: "build/changed.props").Baselines.Select(selector: static artifact => artifact.Name));
        Assert.Equal(expected: ["corpus-inventory"], actual: Select(path: "fixtures/data.json", consumer: true).Baselines.Select(selector: static artifact => artifact.Name));
    }
    [InlineData("src/Puck.World/Assets/worlds/standard.world.json", "browser-parity")]
    [InlineData("src/Puck.World/Assets/worlds/quality.puck", "browser-parity")]
    [InlineData("src/Puck.World/Assets/worlds/games/tictactoe.puck", "browser-parity")]
    [InlineData("VerifiedCode.json", "maths-ledger")]
    [InlineData("src/Puck.World/Assets/worlds/games/go.puck", "state")]
    [InlineData("src/Puck.World/Assets/worlds/pipeline.world.json", "state")]
    [InlineData("worlds/parlor/lineup.puck", "state")]
    [Theory]
    public void ExternalTestDataReachesItsBaseline(string path, string name) {
        Assert.Contains(collection: Select(path: path).Baselines, filter: artifact => (artifact.Name == name));
    }
}
