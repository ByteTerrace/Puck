using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Overlays;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAWS of scrubbing the recorded past from a seat. Each drives <c>world.history</c> through a command registry,
/// from the console and from a seat's own session under the seat's principal, over a live history:
/// <list type="bullet">
/// <item>the bindable forms step, resume and branch run for a seat that holds control over history, and a seat without
/// it is refused by name, while the operator forms stay the operator's;</item>
/// <item>a drag on the scrubber row, by the seat's pointer, seeks to the tick drawn under the pointer, and the row's
/// read-back echoes where it went; a seat without the grant is refused by name;</item>
/// <item>switching to a kept branch reproduces every hash the branch recorded, and keeps the future it displaced in its
/// place;</item>
/// <item>a kept branch saved as a tape names its fork and passes <c>replay.verify</c>.</item>
/// </list>
/// </summary>
public sealed class HistoryScrubLawTests {
    private const int Height = 200;
    private const int Width = 400;

    // Revokes the seeded control wildcard a seat holds, which is what covers control over history.
    private static void RevokeControl(WorldHistoryHarness harness, int slot) => Assert.True(condition: harness.Fixture.Server.GrantTable.Revoke(
        capability: WorldCapability.Control,
        grantee: Principal.Seat(slot: slot),
        subject: GrantSubject.All
    ));

    [Fact]
    public void ASeatStepsResumesAndKeepsABranchUnderItsOwnPrincipalAndIsRefusedWithoutTheGrant() {
        using var harness = new WorldHistoryHarness(seats: 1);
        using var console = new HistoryConsole(harness: harness);

        harness.Steps(count: 20);
        Assert.True(condition: console.Registry.TryGetMetadata(metadata: out var metadata, name: WorldEditorBindings.HistoryCommand));
        Assert.Equal(expected: CommandBindability.Bindable, actual: metadata.Bindability);
        Assert.Contains(
            collection: WorldEditorBindings.BuildPageDefinition.Entries,
            filter: static entry => ((entry.Command == WorldEditorBindings.HistoryCommand) && (entry.Text == "step -1"))
        );

        var stepped = console.Seat(line: "world.history step -5", slot: 0);

        Assert.False(condition: stepped.IsError, userMessage: stepped.Output);
        Assert.Equal(expected: 15UL, actual: harness.History.CursorTick);
        Assert.Equal(expected: harness.Live[15], actual: harness.Hash());

        var kept = console.Seat(line: $"world.history branch {WorldEditorBindings.KeptBranch}", slot: 0);

        Assert.False(condition: kept.IsError, userMessage: kept.Output);
        harness.Steps(count: 1);
        Assert.Contains(collection: harness.History.Status().Branches, filter: static branch => ((branch.Name == WorldEditorBindings.KeptBranch) && (branch.ForkTick == 15UL)));

        var resumed = console.Seat(line: "world.history resume", slot: 0);

        Assert.False(condition: resumed.IsError, userMessage: resumed.Output);

        // The operator's forms stay the operator's, whatever a seat holds.
        var on = console.Seat(line: "world.history on", slot: 0);

        Assert.True(condition: on.IsError);
        Assert.Contains(expectedSubstring: "an operator form; seat1 is not the operator", actualString: on.Output, comparisonType: StringComparison.Ordinal);

        // The red leg: without control over history the seat is refused by name, and the timeline does not move.
        RevokeControl(harness: harness, slot: 0);
        var cursor = harness.History.CursorTick;
        var refused = console.Seat(line: "world.history step -1", slot: 0);

        Assert.True(condition: refused.IsError);
        Assert.Contains(expectedSubstring: "seat1 cannot control history (no grant names it)", actualString: refused.Output, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: cursor, actual: harness.History.CursorTick);

        // The grant that names the history admits it again.
        Assert.True(condition: harness.Fixture.Server.GrantTable.TryGrant(
            grant: new WorldGrant(Capability: WorldCapability.Control, Exclusive: false, Grantee: Principal.Seat(slot: 0), Subject: GrantSubject.History),
            reason: out var reason
        ), userMessage: reason);
        var granted = console.Seat(line: "world.history step -1", slot: 0);

        Assert.False(condition: granted.IsError, userMessage: granted.Output);
        Assert.Equal(expected: (cursor - 1UL), actual: harness.History.CursorTick);
    }
    [Fact]
    public void ADragOnTheScrubberRowSeeksToTheTickUnderTheSeatsPointer() {
        using var harness = new WorldHistoryHarness(seats: 1);
        using var console = new HistoryConsole(harness: harness);
        var bindings = new WorldSeatBindings(definition: harness.Fixture.Server.Definition);
        var viewports = new WorldSeatViewports();
        var pointer = new WorldPointer();
        var row = new WorldHistoryRow(bindings: bindings, history: harness.History, pointer: pointer, viewports: viewports);

        harness.History.Pointer = row;
        harness.Steps(count: 40);
        bindings.SetContextState(family: WorldContextFamilies.Editor, slot: 0, state: WorldContextFamilies.EditorBuild);
        viewports.Publish(camera: default(CameraSnapshot), height: Height, region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f), slot: 0, width: Width);

        Assert.True(condition: harness.History.TryReadRow(forkCount: out _, forks: [], keyframeCount: out _, keyframes: [], window: out var window));
        var expected = (window.Oldest + ((ulong)Math.Round(value: (0.25d * (window.Head - window.Oldest)), mode: MidpointRounding.AwayFromZero)));
        var rect = HistoryRowWriter.Rect;

        // A press on the row a quarter of the way along it.
        pointer.SetPosition(position: new Vector2(x: ((rect.X + (0.25f * rect.Width)) * Width), y: ((rect.Y + (0.5f * rect.Height)) * Height)), slot: 0);
        var dragged = console.Seat(line: WorldEditorBindings.HistoryDragCommand, slot: 0);

        Assert.False(condition: dragged.IsError, userMessage: dragged.Output);
        Assert.Equal(expected: expected, actual: harness.History.CursorTick);
        Assert.Equal(expected: harness.Live[expected], actual: harness.Hash());

        // The row the overlay draws and its read-back agree on where the drag went.
        Span<ulong> keyframes = stackalloc ulong[HistoryRowWriter.MaxKeyframes];
        var forks = new HistoryRowFork[HistoryRowWriter.MaxForks];

        Assert.True(condition: row.TryRead(cursor: out var drawn, forkCount: out _, forks: forks, head: out var head, keyframeCount: out var keyframeCount, keyframes: keyframes, oldest: out var oldest, slot: 0, viewport: out _));
        Assert.Equal(actual: drawn, expected: expected);
        var echo = console.Console(line: "world.history row");

        Assert.Contains(expectedSubstring: $"window {oldest}..{head} | cursor {drawn} | keyframes {string.Join(separator: ",", values: keyframes[..keyframeCount].ToArray())} |", actualString: echo.Output, comparisonType: StringComparison.Ordinal);

        // A press off the row does nothing.
        pointer.SetPosition(position: new Vector2(x: (0.5f * Width), y: (0.1f * Height)), slot: 0);
        var missed = console.Seat(line: WorldEditorBindings.HistoryDragCommand, slot: 0);

        Assert.Equal(expected: string.Empty, actual: missed.Output);
        Assert.Equal(expected: expected, actual: harness.History.CursorTick);

        // The red leg: a seat without control over history is refused by name, and the timeline stays where it was.
        RevokeControl(harness: harness, slot: 0);
        pointer.SetPosition(position: new Vector2(x: ((rect.X + (0.75f * rect.Width)) * Width), y: ((rect.Y + (0.5f * rect.Height)) * Height)), slot: 0);
        var refused = console.Seat(line: WorldEditorBindings.HistoryDragCommand, slot: 0);

        Assert.True(condition: refused.IsError);
        Assert.Contains(expectedSubstring: "seat1 cannot control history (no grant names it)", actualString: refused.Output, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: expected, actual: harness.History.CursorTick);
    }
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
