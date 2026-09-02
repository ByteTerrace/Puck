using Xunit;

using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// The law behind <c>replay.inspect</c>'s default view: a tick prints when, and only when, an intent channel value
/// moved from the entity's previous submission or an authority entry landed. The intent edges are judged over a
/// hand-built tape; the authority tick over a real recording, since the entry leaves are not constructible here.
/// </summary>
public sealed class ReplayInspectLawTests {
    private static readonly WorldChannelTable Channels = WorldChannelTable.Compile(channels: Fixtures.BuildDocument().Channels);
    private static readonly ulong[] Hashes = [0x1UL, 0x2UL, 0x3UL, 0x4UL, 0x5UL];

    private static IntentSubmission Submit(int tick, int entity, FixedQ4816 forward, FixedQ4816 strafe = default) => new(
        Tick: ((ulong)tick),
        EntityIndex: entity,
        Intent: Channels.RoleOrdinals.Intent(
            moveAdvance: forward,
            moveStrafe: strafe
        ),
        Principal: WorldPrincipal.Seat(slot: entity)
    );
    private static WorldReplayTickInput Tick(params IntentSubmission[] intents) => new(
        Authority: [],
        Intents: intents
    );
    // Five ticks: forward rises at 0, holds at 1, falls at 2, and stays down through 3 and 4.
    private static List<WorldReplayTickInput> BuildTicks() => [
        Tick(Submit(tick: 0, entity: 0, forward: FixedQ4816.One)),
        Tick(Submit(tick: 1, entity: 0, forward: FixedQ4816.One)),
        Tick(Submit(tick: 2, entity: 0, forward: FixedQ4816.Zero)),
        Tick(Submit(tick: 3, entity: 0, forward: FixedQ4816.Zero)),
        Tick(Submit(tick: 4, entity: 0, forward: FixedQ4816.Zero)),
    ];
    private static List<string> Walk(IReadOnlyList<WorldReplayTickInput>? ticks = null, IReadOnlyList<ulong>? hashes = null, int from = 0, int to = int.MaxValue, bool all = false, int divergedAt = -1) {
        var lines = new List<string>();

        WorldReplayInspector.AppendTicks(
            all: all,
            channels: Channels,
            divergedAt: divergedAt,
            from: from,
            hashes: (hashes ?? Hashes),
            lines: lines,
            poses: null,
            ticks: (ticks ?? BuildTicks()),
            to: to
        );

        return lines;
    }
    // "[replay.inspect: tick N hash=…" — the tick is the third space-separated token.
    private static int TickOf(string line) => int.Parse(s: line.Split(separator: ' ')[2]);

    [Fact]
    public void DefaultView_PrintsOnlyTheEdges() {
        var lines = Walk();

        // THE DISCRIMINATOR: ticks 1, 3, and 4 (the vector unchanged from the previous submission) must NOT print;
        // ticks 0 (rise) and 2 (fall) must, each with its own recorded hash.
        Assert.Equal(expected: [0, 2], actual: lines.Select(selector: TickOf).ToArray());
        Assert.Contains(expectedSubstring: "p1 forward=1", actualString: lines[0]);
        Assert.Contains(expectedSubstring: "hash=0x0000000000000001", actualString: lines[0]);
        Assert.Contains(expectedSubstring: "p1 forward=0", actualString: lines[1]);
        Assert.Contains(expectedSubstring: "hash=0x0000000000000003", actualString: lines[1]);
    }
    [Fact]
    public void AllView_PrintsEveryTickInRange() {
        Assert.Equal(expected: [0, 1, 2, 3, 4], actual: Walk(all: true).Select(selector: TickOf).ToArray());
    }
    [Fact]
    public void Range_ClampsThePrintedTicksButNotTheEdgeBaseline() {
        // The walk still starts at tick 0, so the fall at tick 2 is measured against the rise at 0 even though 0 is
        // outside the printed range; `to` past the tape clamps rather than refusing.
        var lines = Walk(from: 2, to: 99);

        Assert.Equal(expected: [2], actual: lines.Select(selector: TickOf).ToArray());
        Assert.Contains(expectedSubstring: "p1 forward=0", actualString: lines[0]);
    }
    [Fact]
    public void DivergenceTick_PrintsEvenWithoutAnEdge() {
        var lines = Walk(divergedAt: 4);

        Assert.Contains(collection: lines, filter: line => ((TickOf(line: line) == 4) && line.Contains(value: "DIVERGED")));
        Assert.DoesNotContain(collection: Walk(), filter: line => (TickOf(line: line) == 4));
    }
    [Fact]
    public void ChangedChannelsOnly_AreNamed() {
        // A second seat moving only strafe names only strafe — never the whole vector, never the other seat.
        var ticks = BuildTicks();

        ticks[1] = Tick(
            Submit(tick: 1, entity: 0, forward: FixedQ4816.One),
            Submit(tick: 1, entity: 1, forward: FixedQ4816.Zero, strafe: -(FixedQ4816.One / FixedQ4816.FromInteger(value: 2L)))
        );

        var tick1 = Assert.Single(collection: Walk(ticks: ticks), predicate: line => (TickOf(line: line) == 1));

        Assert.Contains(expectedSubstring: "p2 strafe=-0.5", actualString: tick1);
        Assert.DoesNotContain(expectedSubstring: "p1", actualString: tick1);
        Assert.DoesNotContain(expectedSubstring: "forward", actualString: tick1);
    }
    [Fact]
    public void AuthorityEntry_PrintsItsTickCompactlyNamed() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: [], addonHostFactory: static (_, _) => new NullAddonHost());
        var name = $"inspect-authority-{Guid.NewGuid():N}";

        Assert.True(condition: tape.TryBeginRecording(name: name, refusal: out var refusal), userMessage: $"refused to arm: {refusal}");

        // Tick 0 carries nothing; tick 1 carries one grant and no intent at all — the only thing that can make it
        // print is the authority entry.
        fixture.Step();
        tape.NoteTick();
        transport.SubmitGrant(
            grant: new WorldGrant(Principal: WorldPrincipal.Seat(slot: 1), Capability: WorldCapability.Drive, Subject: GrantSubject.Body(index: 0), Exclusive: false),
            actor: WorldPrincipal.Console
        );
        fixture.Step();
        tape.NoteTick();

        _ = tape.StopRecording();

        using var stream = File.OpenRead(path: WorldReplayTape.PathFor(name: name));
        var persisted = WorldReplaySnapshot.Read(stream: stream);
        var lines = Walk(ticks: persisted.Ticks, hashes: persisted.RecordedHashes);

        var line = Assert.Single(collection: lines);

        Assert.Equal(expected: 1, actual: TickOf(line: line));
        Assert.Contains(expectedSubstring: "grant drive body:0 -> seat2 by console", actualString: line);
        Assert.Contains(expectedSubstring: $"hash=0x{persisted.RecordedHashes[1]:X16}", actualString: line);
    }
}
