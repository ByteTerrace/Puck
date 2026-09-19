using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: a placement's <c>board</c> facet anchors its topology's world origin, so the
/// document's own rules compile their board queries against an anchored topology while the arena lays the same row
/// out unanchored. Anchoring translates a board; it never makes it a different board, so a query still answers over
/// the row rather than refusing as unaddressable.</summary>
public sealed class ArenaBoardAnchorLawTests {
    [Fact]
    public void ABoardQueryCompiledAgainstAnAnchoredTopologyAnswersOverTheArenaRow() {
        using var fixture = Fixtures.FreshServer(definition: ChessHost());

        // The installed document is what the server compiles its rules against, so it is the one whose topologies
        // the arena and the compiler must agree about.
        var definition = fixture.Server.Definition;
        var arena = fixture.Server.Arena;

        Assert.True(
            condition: arena.Catalog.TryResolve(
                handle: out var row,
                lane: StateLane.Document,
                name: "lastLegal"
            )
        );

        var anchored = WorldTopologyCompilation.Find(
            definition: definition,
            name: "chessBoard"
        );

        // The law is only about something when the two compiles really are different instances.
        Assert.NotNull(@object: anchored);
        Assert.NotSame(
            actual: arena.Layout[row.Ordinal].Topology,
            expected: anchored
        );

        // The opening rank and pawn rank carry every white code; cells 0..15 and nothing above them.
        Assert.Equal(
            actual: Evaluate(
                definition: definition,
                fixture: fixture,
                text: "$board:mask:lastLegal:1:6"
            ),
            expected: 0xFFFFL
        );
    }

    private static WorldDefinition ChessHost() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while (
            (directory is not null) &&
            !File.Exists(path: Path.Combine(
                path1: directory.FullName,
                path2: "Puck.slnx"
            ))
        ) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);
        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                path: Path.Combine(
                    directory!.FullName,
                    "src",
                    "Puck.World",
                    "Assets",
                    "worlds",
                    "games",
                    "chess.world.json"
                ),
                definition: out var loaded,
                reason: out var reason
            ),
            userMessage: reason
        );

        return loaded!;
    }
    private static long Evaluate(WorldFixture fixture, WorldDefinition definition, string text) {
        var host = new ArenaSearchEffectHost(
            arena: fixture.Server.Arena,
            dynamics: definition.Dynamics,
            generators: definition.Generators,
            ticksPerSecond: definition.SimulationRateHz
        );

        host.Advance(
            engineTick: 1UL,
            tick: 1UL
        );

        Assert.True(
            condition: Puck.State.Rules.RuleExpressions.TryEvaluate(
                fault: out var fault,
                kind: CellKind.Int,
                program: Puck.State.Rules.RuleCompiler.CompileExpression(
                    context: WorldFactsCompiler.Context(definition: definition),
                    expression: ExpressionProgram.Parse(text: text),
                    kind: CellKind.Int,
                    ruleName: "law",
                    verb: "law"
                ),
                reader: host,
                value: out var value
            ),
            userMessage: fault.ToString()
        );

        return value;
    }
}
