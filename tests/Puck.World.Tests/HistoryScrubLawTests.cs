using Puck.Commands;
using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Recorded-history branch hashes and saved-tape replay invariants.</summary>
public sealed class HistoryScrubLawTests {
    [Fact]
    public void SwitchingToAKeptBranchReproducesItsRecordedHashesAndKeepsTheDisplacedFuture() {
        using var harness = new WorldHistoryHarness(seats: 1);
        using var console = new HistoryConsole(harness: harness);

        harness.Steps(count: 60);
        var original = new Dictionary<ulong, ulong>(dictionary: harness.Live);

        _ = console.Expect(line: "world.history seek 30");
        _ = console.Expect(line: "world.history branch kept");
        harness.Steps(count: 20);
        var replaced = new Dictionary<ulong, ulong>(dictionary: harness.Live);

        Assert.NotEqual(expected: original[50], actual: replaced[50]);

        var switched = console.Expect(line: "world.history switch kept");

        Assert.Contains(expectedSubstring: "matches the branch's recording", actualString: switched.Output, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: 60UL, actual: harness.Tick);
        Assert.Equal(expected: original[60], actual: harness.Hash());
        Assert.Contains(collection: harness.History.Status().Branches, filter: static branch => ((branch.Name == "kept") && (branch.ForkTick == 30UL) && (branch.HeadTick == 50UL)));

        // Every re-entered tick is the one the branch recorded.
        for (var tick = 31UL; (tick <= 60UL); tick += 7UL) {
            Assert.True(condition: harness.History.TrySeek(documentPath: null, refusal: out var refusal, report: out var report, target: tick), userMessage: refusal);
            Assert.True(condition: report.Matches, userMessage: $"tick {tick} diverged at {report.DivergedAt}");
            Assert.Equal(expected: original[tick], actual: harness.Hash());
        }

        // Switching back re-enters the future the first switch displaced.
        _ = console.Expect(line: "world.history seek 60");
        _ = console.Expect(line: "world.history switch kept");
        Assert.Equal(expected: 50UL, actual: harness.Tick);
        Assert.Equal(expected: replaced[50], actual: harness.Hash());
    }
    [Fact]
    public void AKeptBranchSavedAsATapeNamesItsForkAndPassesReplayVerify() {
        using var harness = new WorldHistoryHarness(seats: 1);
        using var console = new HistoryConsole(harness: harness);

        harness.Steps(count: 60);
        _ = console.Expect(line: "world.history seek 30");
        _ = console.Expect(line: "world.history branch kept");
        harness.Steps(count: 10);

        var saved = console.Expect(line: "world.history save kept kept-tape");

        Assert.Contains(expectedSubstring: "re-drive MATCH", actualString: saved.Output, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: harness.Tape.Verify(name: "kept-tape").Passing);

        WorldReplaySnapshot tape;

        using (var stream = File.OpenRead(path: harness.Tape.PathFor(name: "kept-tape"))) {
            tape = WorldReplaySnapshot.Read(stream: stream);
        }

        Assert.Equal(expected: "world.history", actual: tape.ForkedFrom?.ParentName);
        Assert.Equal(expected: 30UL, actual: (tape.StartTick + ((ulong)tape.ForkedFrom!.Value.Tick)));
        Assert.Equal(expected: ((30 - ((int)tape.StartTick!.Value)) + 30), actual: tape.TickCount);
    }

    // A command registry over the history's module, with the console's door and a session per local seat through
    // the text source a host's seat consoles use, so a seat's line runs under the seat's own principal.
    private sealed class HistoryConsole : IDisposable {
        private readonly TemporaryDirectory m_state = new(prefix: "puck-history-console-");
        private readonly WorldInstanceHost m_instances;
        private readonly TextCommandSource m_source;
        private readonly TextCommandSession[] m_seats;

        private CommandResult m_last;

        public HistoryConsole(WorldHistoryHarness harness) {
            m_instances = new WorldInstanceHost(
                applicationStopping: CancellationToken.None,
                machineHostFactory: Fixtures.MachineHostFactory,
                machineId: Guid.NewGuid(),
                resolver: new WorldSessionResolver(),
                seats: WorldEmbodiedSeats.None,
                stateRoot: new WorldStateRoot(path: m_state.RootPath)
            );
            Registry = new CommandRegistry(modules: [new WorldHistoryCommandModule(history: harness.History, instances: m_instances)]);

            var router = new InputRouter(
                bindings: new NoBindings(),
                principalResolver: new SeatPrincipals(),
                registry: Registry
            );

            m_source = new TextCommandSource(registry: Registry);
            m_seats = [.. Enumerable.Range(count: 2, start: 0).Select(selector: slot => m_source.CreateSeatSession(
                onResult: (_, result) => m_last = result,
                router: router,
                slot: slot
            ))];
        }

        public CommandRegistry Registry { get; }

        public CommandResult Console(string line) => Registry.Submit(line: line);
        public void Dispose() {
            m_instances.Dispose();
            m_state.Dispose();
        }
        public CommandResult Expect(string line) {
            var result = Console(line: line);

            Assert.False(condition: result.IsError, userMessage: result.Output);

            return result;
        }
        public CommandResult Seat(int slot, string line) {
            m_last = CommandResult.None;
            m_seats[slot].Enqueue(line: line);
            m_source.Collect();

            return m_last;
        }
    }
    private sealed class NoBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class SeatPrincipals : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Seat(slot: slot);
    }
}
