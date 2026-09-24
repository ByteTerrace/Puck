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

    private static AffectedPlan Select(string[] changed, Dictionary<string, IReadOnlySet<string>>? coverage = null, Func<string, IReadOnlyList<string>>? consumersOf = null) => AffectedSelection.Select(
        canaries: Canaries,
        changed: changed,
        consumersOf: (consumersOf ?? (static _ => [])),
        coverage: (coverage ?? []),
        declaresTests: static path => path.Contains(comparisonType: StringComparison.Ordinal, value: "tested"),
        catalogInputs: static (path, owner) => (path.StartsWith(comparisonType: StringComparison.Ordinal, value: "src/World/Assets/worlds/") || (owner == "Core")),
        projects: Projects,
        worldClosure: WorldClosure
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
            expected: new AffectedPlan(Canaries: ["doors"], Catalog: false, Everything: false, Parity: false, Suites: ["Cli.Tests", "World.Tests"], Unmapped: [], Worlds: []),
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

    private sealed class PlanComparer : IEqualityComparer<AffectedPlan> {
        public bool Equals(AffectedPlan? x, AffectedPlan? y) =>
            ((x is not null) && (y is not null) &&
            x.Canaries.SequenceEqual(second: y.Canaries) &&
            (x.Catalog == y.Catalog) &&
            (x.Everything == y.Everything) &&
            (x.Parity == y.Parity) &&
            x.Suites.SequenceEqual(second: y.Suites) &&
            x.Unmapped.SequenceEqual(second: y.Unmapped) &&
            x.Worlds.SequenceEqual(second: y.Worlds));
        public int GetHashCode(AffectedPlan obj) => obj.Suites.Count;
    }
}
