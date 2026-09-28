using Puck.Commands;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>world.wait ready &lt;seconds&gt;</c> holds only its issuing session until the engine
/// readiness it was composed with reads ready, whatever the tick, and a host that composes no renderer refuses it by
/// name; <c>world.wait bakes &lt;seconds&gt;</c> holds the same way until the bake readiness reads settled, and a host
/// that bakes nothing refuses it by name; <c>world.wait captures &lt;seconds&gt;</c> holds the same way until every armed
/// capture has landed, and a host that composes no renderer refuses it by name. The readiness here is a flag the law sets, so nothing is timed; the deadline path is left to the canaries that
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
        public bool CapturesSettled { get; set; }
        public bool IsReady { get; set; }
        public string? NotReadyReason => (IsReady
            ? null
            : "the engine's pipeline set is building (0 of 12 pipelines created)"
        );
    }
    private sealed class FlagBakes : IWorldBakeReadiness {
        public bool IsSettled { get; set; }
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

    private static (TextCommandSource Source, TextCommandSession Session, List<(string Line, CommandResult Result)> Answered) Console(HostRow row, IWorldEngineReadiness? readiness, IWorldBakeReadiness? bakes = null) {
        var answered = new List<(string Line, CommandResult Result)>();
        var source = new TextCommandSource(registry: new CommandRegistry(modules: [
            new WorldWaitCommandModule(
                authority: new FixedAuthority(instance: row.Instance),
                gates: new FixedGate(gate: new WorldConsoleWaitGate()),
                readiness: readiness,
                bakes: bakes
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
    [Fact]
    public void ABakesWaitHoldsItsSessionUntilTheBakesAreSettledAndRefusesWithoutThem() {
        using var row = HostRow.Build(
            definition: Fixtures.BuildDocument(),
            name: "boot"
        );
        var bakes = new FlagBakes();

        var (source, session, answered) = Console(
            bakes: bakes,
            readiness: null,
            row: row
        );
        var (bakeless, bakelessSession, bakelessAnswers) = Console(
            readiness: null,
            row: row
        );

        session.Enqueue(line: "world.wait bakes 600");
        session.Enqueue(line: "probe");

        for (var drain = 0; (drain < 8); drain++) {
            source.Collect();
        }

        var armed = Assert.Single(collection: answered);

        Assert.Equal(
            actual: (armed.Line, armed.Result.IsError, armed.Result.Output),
            expected: ("world.wait bakes 600", false, "[world.wait: holding until the bakes are settled, at most 600 seconds, from tick 0]")
        );

        bakes.IsSettled = true;
        source.Collect();

        Assert.Equal(
            actual: answered.Select(selector: static answer => answer.Line),
            expected: ["world.wait bakes 600", "probe"]
        );

        bakelessSession.Enqueue(line: "world.wait bakes 5");
        bakeless.Collect();

        Assert.Equal(
            actual: bakelessAnswers.Select(selector: static answer => (answer.Result.IsError, answer.Result.Output)),
            expected: [(true, "[world.wait: refused (this host bakes nothing, so no bake will ever settle)]")]
        );
    }
    [Fact]
    public void ACapturesWaitHoldsItsSessionUntilEveryCaptureHasLandedAndRefusesWithoutARenderer() {
        using var row = HostRow.Build(
            definition: Fixtures.BuildDocument(),
            name: "boot"
        );
        var readiness = new FlagReadiness { IsReady = true };

        var (source, session, answered) = Console(
            readiness: readiness,
            row: row
        );
        var (headless, headlessSession, headlessAnswers) = Console(
            readiness: null,
            row: row
        );

        // A capture is pending: the engine is ready, so only the capture's landing can release the hold.
        session.Enqueue(line: "world.wait captures 600");
        session.Enqueue(line: "probe");

        for (var drain = 0; (drain < 8); drain++) {
            source.Collect();
        }

        var armed = Assert.Single(collection: answered);

        Assert.Equal(
            actual: (armed.Line, armed.Result.IsError, armed.Result.Output),
            expected: ("world.wait captures 600", false, "[world.wait: holding until the captures have landed, at most 600 seconds, from tick 0]")
        );

        readiness.CapturesSettled = true;
        source.Collect();

        Assert.Equal(
            actual: answered.Select(selector: static answer => answer.Line),
            expected: ["world.wait captures 600", "probe"]
        );

        headlessSession.Enqueue(line: "world.wait captures 5");
        headless.Collect();

        Assert.Equal(
            actual: headlessAnswers.Select(selector: static answer => (answer.Result.IsError, answer.Result.Output)),
            expected: [(true, "[world.wait: refused (this host composes no renderer, so no capture will ever land)]")]
        );
    }
}
