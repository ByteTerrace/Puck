using Puck.World.Protocol;
using Xunit;
using static Puck.World.Tests.SolitaireFixtures;

namespace Puck.World.Tests;

/// <summary>Pins the rule frame's own fold (WorldServer.RuleFrame.cs): every tick's rule-written state mutations
/// land on the frame during <c>EvaluateWorldRules</c> and install as ONE real mutation at the tick's own end, so a
/// tick that wrote installs exactly once — never once per effect. Console-submitted mutations (the deal's own
/// action/from/to/card/request cells, each its own ordinary per-command install) are a different door and excluded
/// by principal — WorldPrincipal.World names only what a rule effect fired.</summary>
public sealed class WorldRuleFrameFoldLawTests {
    [Fact]
    public void TheKlondikeDealInstallsAtMostOnceEveryTickThatWrote() {
        using var fixture = Fixtures.FreshServer(definition: Game(game: "solitaireKlondike"));
        var installsByTick = new Dictionary<ulong, int>();

        fixture.Server.MutationJournalTap = (tick, mutation) => {
            if (mutation.Principal == WorldPrincipal.World) {
                installsByTick[tick] = (installsByTick.GetValueOrDefault(tick) + 1);
            }
        };

        Request(f: fixture, game: "solitaireKlondike", action: 1);

        Assert.True(condition: (installsByTick.Count > 0), userMessage: "the deal must apply at least one tick's worth of rule-fired mutations to prove the fold");
        Assert.All(collection: installsByTick, action: entry => Assert.True(
            condition: (entry.Value == 1),
            userMessage: $"tick {entry.Key} installed {entry.Value} times — a tick whose rules wrote should install exactly once, not once per effect"
        ));
    }
}
