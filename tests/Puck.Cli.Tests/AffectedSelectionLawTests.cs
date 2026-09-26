using Puck.Cli.Affected;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class AffectedSelectionLawTests {
    // A small graph: Core <- World <- Cli, with World.Tests and Cli.Tests (which builds World without referencing its
    // assembly) and an unrelated Maths with its own suite.
    private static readonly AffectedProject[] Projects = [
        new(Directory: "src/Core", IsSuite: false, Name: "Core", References: []),
        new(Directory: "src/World", IsSuite: false, Name: "World", References: ["Core"]),
        new(Directory: "src/Cli", IsSuite: false, Name: "Cli", References: ["Core"]),
        new(Directory: "src/Maths", IsSuite: false, Name: "Maths", References: []),
        new(Directory: "tests/World.Tests", IsSuite: true, Name: "World.Tests", References: ["World"]),
        new(Directory: "tests/Cli.Tests", IsSuite: true, Name: "Cli.Tests", References: ["Cli", "World"]),
        new(Directory: "tests/Maths.Tests", IsSuite: true, Name: "Maths.Tests", References: ["Maths"]),
    ];
    private static readonly AffectedCanary[] Canaries = [
        new(Directory: "tests/Canaries/ink", Files: ["tests/Canaries/ink/fixture.world.json", "src/World/Assets/ink.hlsl"], Id: "ink", RequiresGpu: true),
        new(Directory: "tests/Canaries/doors", Files: ["tests/Canaries/doors/fixture.world.json"], Id: "doors", RequiresGpu: false),
    ];
    private static readonly HashSet<string> WorldClosure = new(collection: ["World", "Core"], comparer: StringComparer.OrdinalIgnoreCase);

    private static AffectedPlan Select(string[] changed, Dictionary<string, IReadOnlySet<string>>? coverage = null, Func<string, IReadOnlyList<string>>? consumersOf = null, Func<string, IReadOnlyList<string>>? standInsFor = null, Func<string, IReadOnlySet<string>>? canariesReaching = null, IReadOnlySet<string>? deleted = null, Dictionary<string, IReadOnlySet<string>>? recorded = null) => AffectedSelection.Select(
        canaries: Canaries,
        changed: changed,
        consumersOf: (consumersOf ?? (static _ => [])),
        coverage: (coverage ?? []),
        declaresTests: static path => path.Contains(comparisonType: StringComparison.Ordinal, value: "tested"),
        catalogInputs: static (path, owner) => (path.StartsWith(comparisonType: StringComparison.Ordinal, value: "src/World/Assets/worlds/") || (owner == "Core")),
        projects: Projects,
        canariesReaching: (canariesReaching ?? (static _ => new HashSet<string>())),
        standInsFor: (standInsFor ?? (static _ => [])),
        worldClosure: WorldClosure,
        deleted: deleted,
        recorded: recorded
    );

    [Fact]
    public void AChangedProjectChoosesItsOwnSuiteAndEverySuiteThatReachesIt() {
        Assert.Equal(actual: Select(changed: ["src/Core/Thing.cs"]).Suites, expected: ["Cli.Tests", "World.Tests"]);
        Assert.Equal(actual: Select(changed: ["src/Maths/Field.cs"]).Suites, expected: ["Maths.Tests"]);
        Assert.Equal(actual: Select(changed: ["tests/World.Tests/Law.cs"]).Suites, expected: ["World.Tests"]);
    }
    [Fact]
    public void ACanaryIsChosenByItsDirectoryItsNamedFilesAndWhatItExecuted() {
        Assert.Equal(actual: Select(changed: ["tests/Canaries/doors/positive.script.txt"]).Canaries, expected: ["doors"]);
        Assert.Equal(actual: Select(changed: ["src/World/Assets/ink.hlsl"]).Canaries, expected: ["ink"]);
        Assert.Equal(
            actual: Select(
                changed: ["src/World/Door.cs"],
                coverage: new() { ["src/World/Door.cs"] = new HashSet<string>(collection: ["doors"]) }
            ),
            expected: new AffectedPlan(Canaries: ["doors"], Catalog: false, Deleted: [], Everything: false, Parity: false, Suites: ["Cli.Tests", "World.Tests"], Unmapped: [], Worlds: []),
            comparer: new PlanComparer()
        );
    }
    [Fact]
    public void AChangedPuckSourceWithTestBlocksIsRunByPuckTest() {
        var plan = Select(changed: ["worlds/garden/tested.puck", "worlds/garden/module.puck"]);

        Assert.Equal(actual: plan.Worlds, expected: ["worlds/garden/tested.puck"]);
        Assert.Empty(collection: plan.Suites);
    }
    [Fact]
    public void TheCatalogIsCheckedWhenAShippedWorldOrTheCompileThatWritesItChanges() {
        Assert.True(condition: Select(changed: ["src/World/Assets/worlds/puck.puck"]).Catalog);
        Assert.True(condition: Select(changed: ["src/Core/Thing.cs"]).Catalog);
        Assert.True(condition: Select(changed: ["build/Shaders.targets"]).Catalog);
        Assert.False(condition: Select(changed: ["src/Maths/Field.cs"]).Catalog);
    }
    [Fact]
    public void ParityRunsWhenAChosenCanaryRendersOnAGpu() {
        Assert.True(condition: Select(changed: ["src/World/Assets/ink.hlsl"]).Parity);
        Assert.False(condition: Select(changed: ["tests/Canaries/doors/positive.script.txt"]).Parity);
    }
    /// <summary>A file the index cannot know is placed through its indexed stand-ins: their canaries are its canaries and
    /// it is not unmapped; a stand-in the index does not know places nothing.</summary>
    [Fact]
    public void AFileTheIndexCannotKnowIsPlacedThroughItsIndexedStandIns() {
        var coverage = new Dictionary<string, IReadOnlySet<string>> { ["src/World/Door.cs"] = new HashSet<string>(collection: ["doors"]) };
        var placed = Select(
            changed: ["src/World/World.csproj"],
            coverage: coverage,
            standInsFor: static path => ((path == "src/World/World.csproj") ? ["src/World/Door.cs"] : [])
        );
        var unplaced = Select(
            changed: ["src/World/World.csproj"],
            coverage: coverage,
            standInsFor: static _ => ["src/World/Unrecorded.cs"]
        );

        Assert.Equal(actual: placed.Canaries, expected: ["doors"]);
        Assert.Empty(collection: placed.Unmapped);
        Assert.Empty(collection: unplaced.Canaries);
        Assert.Equal(actual: unplaced.Unmapped, expected: ["src/World/World.csproj"]);
    }
    [Fact]
    public void AWorldSourceTheIndexDoesNotKnowIsReportedAndChoosesNoCanary() {
        var plan = Select(changed: ["src/World/New.cs", "src/Maths/Field.cs"]);

        Assert.Empty(collection: plan.Canaries);
        Assert.Equal(actual: plan.Unmapped, expected: ["src/World/New.cs"]);
    }
    [Fact]
    public void ASourceTheIndexRecordsAsExecutedByNothingChoosesNoCanaryAndIsNotUnmapped() {
        var plan = Select(
            changed: ["src/World/Quiet.cs"],
            coverage: new() { ["src/World/Quiet.cs"] = new HashSet<string>() }
        );

        Assert.Empty(collection: plan.Canaries);
        Assert.Empty(collection: plan.Unmapped);
    }
    /// <summary>A file deleted since the base can never be recorded, so it is never unmapped: the index the base
    /// recorded places it, choosing the canaries that executed it; one that index does not name either is listed as
    /// deleted; and nothing reads a deleted file, so a deleted <c>.puck</c> source is not run by <c>puck test</c>.
    /// Its project's suites still run.</summary>
    [Fact]
    public void ADeletedSourceIsPlacedByTheIndexTheBaseRecordedOrListedAsDeletedNeverUnmapped() {
        var plan = Select(
            changed: ["src/World/Door.cs", "src/World/Gone.cs", "src/World/Unrecorded.cs", "src/World/tested.puck", "src/World/New.cs"],
            deleted: new HashSet<string>(collection: ["src/World/Door.cs", "src/World/Gone.cs", "src/World/Unrecorded.cs", "src/World/tested.puck"]),
            recorded: new() {
                ["src/World/Door.cs"] = new HashSet<string>(collection: ["doors"]),
                ["src/World/Gone.cs"] = new HashSet<string>(collection: ["ink"]),
            }
        );

        Assert.Equal(actual: plan.Canaries, expected: ["doors", "ink"]);
        Assert.Equal(actual: plan.Deleted, expected: ["src/World/Unrecorded.cs", "src/World/tested.puck"]);
        Assert.Equal(actual: plan.Unmapped, expected: ["src/World/New.cs"]);
        Assert.Empty(collection: plan.Worlds);
        Assert.Equal(actual: plan.Suites, expected: ["Cli.Tests", "World.Tests"]);

        // Without the base's index, a deleted file the current index names is still placed by it.
        Assert.Equal(
            actual: Select(
                changed: ["src/World/Door.cs"],
                coverage: new() { ["src/World/Door.cs"] = new HashSet<string>(collection: ["doors"]) },
                deleted: new HashSet<string>(collection: ["src/World/Door.cs"])
            ).Canaries,
            expected: ["doors"]
        );
    }
    [Fact]
    public void BuildInfrastructureReachesEverySuiteAndProseReachesNothing() {
        var infrastructure = Select(changed: ["build/Shaders.targets"]);

        Assert.True(condition: infrastructure.Everything);
        Assert.Equal(actual: infrastructure.Suites, expected: ["Cli.Tests", "Maths.Tests", "World.Tests"]);

        var prose = Select(changed: ["docs/manual.md", "src/World/README.md", ".claude/skills/x/SKILL.md", "experimental/Old/Thing.cs"]);

        Assert.Empty(collection: prose.Suites);
        Assert.Empty(collection: prose.Canaries);
        Assert.Empty(collection: prose.Unmapped);
    }
    [Fact]
    public void AFileNoProjectOwnsChoosesTheProjectsThatNameItsDirectory() {
        var plan = Select(
            changed: ["tests/Verdicts/phase.world.json"],
            consumersOf: static path => (path.StartsWith(comparisonType: StringComparison.Ordinal, value: "tests/Verdicts/") ? ["Cli"] : [])
        );

        Assert.Equal(actual: plan.Suites, expected: ["Cli.Tests"]);
    }
    // The printed plan names what --run does with each line: a test line is a source for puck test, and the catalog line
    // names the compile check that runs over it, so the catalog, which holds no test worlds, never reads as a test target.
    [Fact]
    public void ThePrintedPlanNamesTheCatalogCheckBesideTheCatalog() {
        var plan = Select(changed: ["src/World/Assets/worlds/tested.puck"]);
        var text = new StringWriter();

        AffectedCommand.Describe(
            into: text,
            plan: plan
        );

        var lines = text.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: Environment.NewLine);

        Assert.Contains(collection: lines, expected: "test src/World/Assets/worlds/tested.puck");
        Assert.Contains(collection: lines, expected: $"catalog {AffectedCommand.ShippedCatalog} (puck compile --tree {AffectedCommand.ShippedTree} --check)");
        Assert.DoesNotContain(collection: lines, filter: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: $"test {AffectedCommand.ShippedCatalog}"));
    }

    private sealed class PlanComparer : IEqualityComparer<AffectedPlan> {
        public bool Equals(AffectedPlan? x, AffectedPlan? y) =>
            ((x is not null) && (y is not null) &&
            x.Canaries.SequenceEqual(second: y.Canaries) &&
            (x.Catalog == y.Catalog) &&
            (x.Everything == y.Everything) &&
            (x.Parity == y.Parity) &&
            x.Suites.SequenceEqual(second: y.Suites) &&
            x.Unmapped.SequenceEqual(second: y.Unmapped) &&
            x.Worlds.SequenceEqual(second: y.Worlds) &&
            x.Deleted.SequenceEqual(second: y.Deleted));
        public int GetHashCode(AffectedPlan obj) => obj.Suites.Count;
    }
}
