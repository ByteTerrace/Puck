using Puck.Commands;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>world.wait ready &lt;seconds&gt;</c> holds only its issuing session until the engine
/// readiness it was composed with reads ready, whatever the tick, and a host that composes no renderer refuses it by
/// name. The readiness here is a flag the law sets, so nothing is timed; the deadline path is left to the canaries that
/// run a real engine.
/// </summary>
public sealed class WorldWaitReadyLawTests {
    // Resolves every invocation to the fixture's one row, as the desktop's boot console authority does.
    private sealed class FixedAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;

            return true;
        }
    }
    private sealed class FixedGate(WorldConsoleWaitGate gate) : IWorldWaitGateResolver {
        public WorldConsoleWaitGate GateFor(WorldInstance instance) => gate;
    }
    private sealed class FlagReadiness : IWorldEngineReadiness {
        public bool IsReady { get; set; }
        public string? NotReadyReason => (IsReady
            ? null
            : "the engine's pipeline set is building (0 of 12 pipelines created)"
        );
    }
    // Answers at once, so its answer shows whether the session behind a wait was drained.
    private sealed class ProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Answers at once.",
                handler: static (_, _) => CommandResult.None,
                name: "probe"
            );
        }
    }

    private static (TextCommandSource Source, TextCommandSession Session, List<(string Line, CommandResult Result)> Answered) Console(HostRow row, IWorldEngineReadiness? readiness) {
        var answered = new List<(string Line, CommandResult Result)>();
        var source = new TextCommandSource(registry: new CommandRegistry(modules: [
            new WorldWaitCommandModule(
                authority: new FixedAuthority(instance: row.Instance),
                gates: new FixedGate(gate: new WorldConsoleWaitGate()),
                readiness: readiness
            ),
            new ProbeModule(),
        ]));
        var session = source.CreateSession(
            onResult: (line, result) => answered.Add(item: (line, result)),
            principal: Principal.Console
        );

        return (source, session, answered);
    }

    [Fact]
    public void AReadyWaitHoldsItsSessionUntilTheEngineIsReady() {
        using var row = HostRow.Build(
            definition: Fixtures.BuildDocument(),
            name: "boot"
        );
        var readiness = new FlagReadiness();

        var (source, session, answered) = Console(
            readiness: readiness,
            row: row
        );

        session.Enqueue(line: "world.wait ready 600");
        session.Enqueue(line: "probe");

        // No tick is ever published: only readiness can release the hold.
        for (var drain = 0; (drain < 8); drain++) {
            source.Collect();
        }

        var armed = Assert.Single(collection: answered);

        Assert.Equal(
            actual: (armed.Line, armed.Result.IsError, armed.Result.Output),
            expected: ("world.wait ready 600", false, "[world.wait: holding until the engine is ready, at most 600 seconds, from tick 0]")
        );

        readiness.IsReady = true;
        source.Collect();

        Assert.Equal(
            actual: answered.Select(selector: static answer => answer.Line),
            expected: ["world.wait ready 600", "probe"]
        );
    }
    [Fact]
    public void AReadyWaitRefusesByNameWithoutARendererAndOutsideItsDeadlineRange() {
        using var row = HostRow.Build(
            definition: Fixtures.BuildDocument(),
            name: "boot"
        );

        var (headless, headlessSession, headlessAnswers) = Console(
            readiness: null,
            row: row
        );
        var (rendered, renderedSession, renderedAnswers) = Console(
            readiness: new FlagReadiness(),
            row: row
        );

        headlessSession.Enqueue(line: "world.wait ready 5");
        headless.Collect();
        renderedSession.Enqueue(line: "world.wait ready 0");
        renderedSession.Enqueue(line: "world.wait ready 601");
        rendered.Collect();

        Assert.Equal(
            actual: headlessAnswers.Concat(second: renderedAnswers).Select(selector: static answer => (answer.Result.IsError, answer.Result.Output)),
            expected: [
                (true, "[world.wait: refused (this host composes no renderer, so no engine will ever be ready)]"),
                (true, "[world.wait: '0' is not a whole number of seconds in 1..600]"),
                (true, "[world.wait: '601' is not a whole number of seconds in 1..600]"),
            ]
        );
    }
}
