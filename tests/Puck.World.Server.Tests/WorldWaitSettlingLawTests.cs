using Puck.Commands;
using Puck.Testing;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// CONTRACT UNDER TEST: in a text session that settles its results, as an operator attachment's session does,
/// <c>world.wait</c> answers when its hold ends, never as it arms, so the caller's call waits out the hold and its next
/// call runs at once. The tick form answers with its release tick; the ready form with its release report. Against an
/// answer deadline, a tick wait the world's rate cannot complete in time is refused before it arms, and a wait still
/// held when the answer is due ends with a refusal that names it. Ticks are published by hand and the deadline runs
/// on a <see cref="VirtualClock"/>, so nothing is timed.
/// </summary>
public sealed class WorldWaitSettlingLawTests {
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
        public string? NotReadyReason => (IsReady ? null : "the pipeline set is building");
    }
    private sealed class ProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Answers at once.",
                handler: static (_, _) => new CommandResult(Output: "[probe]"),
                name: "probe"
            );
        }
    }
    private sealed class SettlingConsole : IDisposable {
        private readonly HostRow m_row;

        public SettlingConsole(IWorldEngineReadiness? readiness = null) {
            m_row = HostRow.Build(
                definition: Fixtures.BuildDocument(),
                name: "boot"
            );
            Source = new TextCommandSource(registry: new CommandRegistry(modules: [
                new WorldWaitCommandModule(
                    authority: new FixedAuthority(instance: m_row.Instance),
                    gates: new FixedGate(gate: Gate),
                    readiness: readiness
                ),
                new ProbeModule(),
            ]));
            Session = Source.CreateSession(
                onSettled: (line, result) => Answered.Add(item: (line, result.IsError, result.Output)),
                principal: Principal.Console
            );
        }

        public List<(string Line, bool IsError, string Output)> Answered { get; } = [];
        public VirtualClock Clock { get; } = new();
        public WorldConsoleWaitGate Gate { get; } = new();
        public int RateHz => m_row.Instance.Server.Definition.SimulationRateHz;
        public TextCommandSession Session { get; }
        public TextCommandSource Source { get; }

        public void Dispose() {
            Session.Dispose();
            m_row.Dispose();
        }
        public void Due(TimeSpan budget) => Session.AnswerDeadline = new TextAnswerDeadline(
            budget: budget,
            clock: Clock
        );
    }

    [Fact]
    public void ATickWaitAnswersAtItsReleaseTickWithNoLineBehindIt() {
        using var console = new SettlingConsole();

        console.Session.Enqueue(line: "world.wait 3");
        console.Source.Collect();

        for (var tick = 1UL; (tick < 3UL); tick++) {
            console.Gate.PublishTick(tick: tick);
            console.Source.Collect();
        }

        Assert.Empty(collection: console.Answered);

        console.Gate.PublishTick(tick: 3UL);
        console.Source.Collect();
        console.Session.Enqueue(line: "probe");
        console.Source.Collect();

        Assert.Equal(
            actual: console.Answered,
            expected: [
                ("world.wait 3", false, "[world.wait: 3 ticks from 0 — released at tick 3]"),
                ("probe", false, "[probe]"),
            ]
        );
    }
    [Fact]
    public void ATickWaitTheRateCannotCompleteBeforeTheAnswerIsDueIsRefusedBeforeItArms() {
        using var console = new SettlingConsole();
        var budget = TimeSpan.FromSeconds(seconds: 1);
        var fits = ((ulong)console.RateHz);

        console.Due(budget: budget);
        console.Session.Enqueue(line: $"world.wait {(fits + 1UL)}");
        console.Session.Enqueue(line: "probe");
        console.Source.Collect();

        var refused = console.Answered[0];

        Assert.True(condition: refused.IsError);
        Assert.StartsWith(
            actualString: refused.Output,
            expectedStartString: $"[world.wait: refused ({(fits + 1UL)} ticks at {console.RateHz} Hz take "
        );
        Assert.Equal(
            actual: console.Answered.Select(selector: static answer => answer.Line),
            expected: [$"world.wait {(fits + 1UL)}", "probe"]
        );

        // A wait that fits arms; a world completing ticks slower than its rate leaves it held when the answer is due,
        // and the refusal releases the session.
        console.Answered.Clear();
        console.Due(budget: budget);
        console.Session.Enqueue(line: $"world.wait {fits}");
        console.Session.Enqueue(line: "probe");
        console.Source.Collect();
        console.Gate.PublishTick(tick: 1UL);
        console.Source.Collect();
        Assert.Empty(collection: console.Answered);

        console.Clock.Advance(by: budget);
        console.Source.Collect();

        Assert.Equal(
            actual: console.Answered,
            expected: [
                ($"world.wait {fits}", true, $"[world.wait: this call's answer was due at tick 1, before release tick {fits}, so the session stopped waiting; the world is completing ticks slower than its rate — wait in shorter steps or give the call a longer deadline]"),
                ("probe", false, "[probe]"),
            ]
        );
    }
    [Fact]
    public void ATickWaitReleasedByAStalledWorldAnswersWithARefusal() {
        using var console = new SettlingConsole();

        console.Session.Enqueue(line: "world.wait 5");
        console.Source.Collect();
        console.Gate.PublishTick(tick: 2UL);
        _ = console.Gate.ReleaseStalled();
        console.Source.Collect();

        Assert.Equal(
            actual: console.Answered,
            expected: [("world.wait 5", true, "[world.wait: released at tick 2, before release tick 5 — the world stopped stepping or its clock was reset]")]
        );
    }
    [Fact]
    public void AReadyWaitAnswersWithItsReleaseReport() {
        var readiness = new FlagReadiness();
        using var console = new SettlingConsole(readiness: readiness);

        console.Session.Enqueue(line: "world.wait ready 600");
        console.Source.Collect();
        console.Source.Collect();
        Assert.Empty(collection: console.Answered);

        readiness.IsReady = true;
        console.Source.Collect();

        Assert.Equal(
            actual: console.Answered,
            expected: [("world.wait ready 600", false, "[engine: ready at tick 0]")]
        );

        // Not ready when the answer is due: the hold ends with a refusal naming why, and the session is free.
        console.Answered.Clear();
        readiness.IsReady = false;
        console.Due(budget: TimeSpan.FromSeconds(seconds: 5));
        console.Session.Enqueue(line: "world.wait ready 600");
        console.Session.Enqueue(line: "probe");
        console.Source.Collect();
        Assert.Empty(collection: console.Answered);

        console.Clock.Advance(by: TimeSpan.FromSeconds(seconds: 5));
        console.Source.Collect();

        Assert.Equal(
            actual: console.Answered,
            expected: [
                ("world.wait ready 600", true, "[engine: not ready when this call's answer was due, so world.wait released: the pipeline set is building]"),
                ("probe", false, "[probe]"),
            ]
        );
    }
}
