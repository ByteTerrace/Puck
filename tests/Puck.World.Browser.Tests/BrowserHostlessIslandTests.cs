using System.Text;

using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>Judges the composed flagship island (<c>puck.world.json</c> under its own basis and imports) — the
/// document whose <c>dive</c>/<c>kart</c>/<c>jump</c> modules author rules reading world-scoped operand facts
/// (<c>PhysicsQuiescentOperand</c> among them) that only <see cref="BrowserRuleReader"/>'s own hostless answers make
/// judgeable at all (see that type's own remarks, and the project README's "Verified scope boundary").</summary>
public sealed class BrowserHostlessIslandTests {
    private static string RepositoryRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }
    private static byte[] ComposedPuckWorldBytes() {
        var path = Path.Combine(RepositoryRoot(), "src", "Puck.World", "Assets", "worlds", "puck.world.json");

        Assert.True(condition: WorldDefinitionFileSource.TryComposeDocumentTree(path: path, tree: out var tree, reason: out var reason), userMessage: reason);

        return Encoding.UTF8.GetBytes(s: tree!.ToJsonString());
    }
    private static BrowserSession NewIslandSession() {
        var errors = new List<string>();
        var deferred = new List<string>();

        Assert.True(condition: BrowserParser.TryParseAndValidate(utf8Json: ComposedPuckWorldBytes(), errors: errors, deferred: deferred, definition: out var definition, compilation: out var compilation), userMessage: string.Join(separator: "; ", values: errors));

        return new BrowserSession(definition: definition!, compilation: compilation!);
    }

    [Fact]
    public void Judge_over_the_composed_island_runs_ticks_1_through_3_without_throwing_and_names_a_host_fact() {
        var session = NewIslandSession();
        var hostFacts = new List<BrowserHostFact>();

        for (var tick = 1UL; (tick <= 3UL); tick++) {
            var trace = session.Judge(tick: tick);

            hostFacts.AddRange(collection: trace.HostFacts);
        }

        Assert.NotEmpty(collection: hostFacts);
        Assert.Contains(collection: hostFacts, filter: fact => (fact.Operand.Contains(value: "Physics", comparisonType: StringComparison.Ordinal) || fact.Operand.Contains(value: "Body", comparisonType: StringComparison.Ordinal)));
    }
    [Fact]
    public void Judge_over_the_composed_island_is_deterministic_across_two_independent_sessions() {
        var sessionA = NewIslandSession();
        var sessionB = NewIslandSession();

        for (var tick = 1UL; (tick <= 3UL); tick++) {
            sessionA.Judge(tick: tick);
            sessionB.Judge(tick: tick);
        }

        Assert.Equal(expected: sessionA.StateHash(), actual: sessionB.StateHash());
    }
}
