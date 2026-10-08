using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>
/// ACCEPTANCE LAW 1 (docs/plans/acceptance-laws.md): a rewind reaches an existing viewer. A federation projection sink
/// attached at the Presentation tier before a history seek follows the seek: it stays attached, the value it reads is
/// the authority's at the restored tick and not one written after it, it never keeps a clock anchor ahead of the
/// authority, a row it may not read never reaches it, the seek delivers one definition and one snapshot at its target
/// and nothing for a re-simulated tick, and the projection it holds is the one a sink attached afresh after the seek
/// receives. It holds through the in-place restore, through the load door after a structural edit, across a clock
/// that has no number at the keyframe, and for a target between keyframes.
/// </summary>
public sealed partial class ProjectionAnchorLawTests {
    private const string Hidden = "secret";

    // Every timeline delivery an IClientSink receives, by kind, and each snapshot's tick.
    private sealed class DeliveryLog : IClientSink {
        public List<string> Kinds { get; } = [];
        public List<ulong> SnapshotTicks { get; } = [];

        public void DeliverAnswer(in QueryAnswer answer) { }
        public void DeliverComposition(WorldComposition composition) => Kinds.Add(item: "composition");
        public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) => Kinds.Add(item: "definition");
        public void DeliverSessionLever(WorldSessionLever lever) { }
        public void DeliverSnapshot(in WorldSnapshot snapshot) {
            Kinds.Add(item: "snapshot");
            SnapshotTicks.Add(item: snapshot.Tick);
        }
        public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) => Kinds.Add(item: "state");
    }

    private static WorldFederationProjectionSink PresentationViewer(WorldServer server) => new(
        authority: "boot",
        disclosure: static () => new WorldSinkDisclosure(ObserverBodyIndex: -1, Policy: new WorldObserverDisclosure(UpdateSeconds: 1f)),
        revision: static () => 1,
        server: server,
        tier: WorldDisclosureTier.Presentation
    );
    // Streams whatever the sink has queued into the hold. The queue is bounded, so a long run drains it as it goes.
    private static async Task DrainAsync(WorldFederationProjectionSink sink, WorldProjectionHold hold) {
        using var wire = new MemoryStream();

        await sink.StreamAsync(ct: TestContext.Current.CancellationToken, output: wire);

        using var received = new MemoryStream(buffer: wire.ToArray());

        while (received.Position < received.Length) {
            var frame = await WorldFederationCodec.ReadResponseAsync(ct: TestContext.Current.CancellationToken, stream: received);

            Assert.True(condition: frame.Ok);

            if (frame.Kind == ((byte)WorldFederationResponse.Definition)) {
                Assert.True(condition: WorldFederationCodec.TryDecodeDocument(body: frame.Body.Span, definition: out _, failure: out var failure, hold: hold, tier: out _, version: out _), userMessage: failure.ToString());
            } else if (frame.Kind == ((byte)WorldFederationResponse.ProjectionDelta)) {
                Assert.True(condition: WorldFederationCodec.TryDecodeProjectionDelta(body: frame.Body.Span, definition: out _, failure: out var failure, hold: hold, stamp: out _, valuesOnly: out _, version: out _), userMessage: failure.ToString());
            }
        }
    }
    // The clock's eased raw value in a document at the authority's time, or null when the clock holds no number.
    private static long? ClockValue(WorldDefinition document, ArenaTime time) => (WorldStateReader.TryReadEased(
        definition: document,
        engineTick: time.EngineTick,
        key: null,
        rawValue: out var raw,
        row: out _,
        rowName: Clock,
        text: out _,
        tick: time.Tick
    )
        ? raw
        : null);
    // The authority's presentation and the recipient's at one tick: the clock's phase, or its absence on both sides when
    // the clock holds no number, and the keyed fog.
    private static void AssertPresentsTheSame(WorldDefinition host, WorldDefinition recipient, ulong tick, ulong engineTick) {
        var hostMirror = ClientFixtures.StateMirror(definition: host, engineTick: engineTick, tick: tick);
        var recipientMirror = ClientFixtures.StateMirror(definition: recipient, engineTick: engineTick, tick: tick);
        var hostHas = hostMirror.TryPhase(clock: out _, name: Clock, phase: out var hostPhase);
        var recipientHas = recipientMirror.TryPhase(clock: out _, name: Clock, phase: out var recipientPhase);

        Assert.Equal(actual: (recipientHas, recipientPhase), expected: (hostHas, hostPhase));
        Assert.Equal(
            expected: hostMirror.Scalar(fallback: float.NaN, scalar: Density),
            actual: recipientMirror.Scalar(fallback: float.NaN, scalar: Density)
        );
    }
    // The definition with its clocks' anchors cleared: everything structural a projection carries.
    private static WorldDefinition WithoutAnchors(WorldDefinition definition) => definition with {
        TimelineRaw = ((definition.TimelineRaw is { Clocks: { } clocks } timeline)
            ? timeline with { Clocks = [.. clocks.Select(selector: static clock => clock with { Anchor = null })] }
            : definition.TimelineRaw),
    };
    private static void AssertNoHiddenRow(WorldDefinition held, string when) => Assert.False(
        condition: held.State.Any(predicate: static row => (row.Name.Value == Hidden)),
        userMessage: $"the viewer holds the row it may not read {when}"
    );
    private static WorldMutation WriteClock(long raw) => new WorldMutation.UpsertStateCell(
        Kind: WorldDocumentWriteKind.Set,
        Key: WorldStateRow.SlotKey.Value,
        Principal: Principal.Console,
        Row: Clock,
        Value: raw
    );

    [InlineData("in-place")]
    [InlineData("load-door")]
    [InlineData("unnumbered-clock")]
    [InlineData("between-keyframes")]
    [Theory]
    public async Task A_history_seek_reaches_an_existing_viewer(string variant) {
        // The clock advances by a whole raw unit stride every tick, keyed fog follows it, and a row only seat1 may
        // read sits beside it. The unnumbered variant's clock holds no number until one is written after the keyframe.
        var authored = Document(row: ((variant == "unnumbered-clock")
            ? Row(max: FixedQ4816.FromDouble(value: 0.75d).Value, min: FixedQ4816.FromDouble(value: 0.375d).Value, raw: FixedQ4816.FromDouble(value: 0.5d).Value, slot: false)
            : Row(advance: PerTick(raw: 37L), raw: 0L)));
        var definition = authored.WithWorldState(rows: [
            .. authored.State,
            new WorldStateRow(
                Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: FixedQ4816.One.Value))],
                Kind: CellKind.Fixed,
                Name: CellName.Parse(candidate: Hidden),
                Visibility: new StateVisibility(Readers: ["seat1"])
            ),
        ]);
        using var harness = new WorldHistoryHarness(definition: definition, seats: 0, seed: 53UL);
        var server = harness.Fixture.Server;
        var sink = PresentationViewer(server: server);
        var hold = new WorldProjectionHold();

        using var lease = server.AttachSink(sink: sink);

        await DrainAsync(hold: hold, sink: sink);

        // Run until the window holds a keyframe to return to, draining the viewer as it goes.
        var structural = (variant == "load-door");

        for (var index = 0; (index < 130); index++) {
            harness.StepWithoutInput();
            await DrainAsync(hold: hold, sink: sink);
        }

        if (structural) {
            harness.Submit(mutation: new WorldMutation.UpsertStateRow(
                Principal: Principal.Console,
                Row: new WorldStateRow(Kind: CellKind.Int, Name: CellName.Parse(candidate: "bonus"))
            ));
        }

        for (var index = 0; (index < 170); index++) {
            harness.StepWithoutInput();
            await DrainAsync(hold: hold, sink: sink);
        }

        var keyframes = harness.History.KeyframeTicks;

        // The second keyframe stands after the viewer's own anchor row was minted, and before the structural edit.
        Assert.True(condition: (keyframes.Count >= 3), userMessage: $"the window holds {keyframes.Count} keyframe(s); the law needs a seek back over the second");

        var keyframe = keyframes[1];
        var target = ((variant == "between-keyframes") ? (keyframe + 5UL) : keyframe);

        // What the seek discards. In place, the clock's own advance past the keyframe, which the journal never sees; a
        // journaled write would send the seek through the load door. Through the load door, a write with a fraction so
        // its phase is its own; for the unnumbered variant, the row with its first number, so the viewer holds an anchor
        // stood after the target.
        if (variant == "unnumbered-clock") {
            harness.Submit(mutation: new WorldMutation.UpsertStateRow(Principal: Principal.Console, Row: Row(max: FixedQ4816.FromDouble(value: 0.75d).Value, min: FixedQ4816.FromDouble(value: 0.375d).Value, raw: FixedQ4816.FromDouble(value: 0.625d).Value)));
        } else if (structural) {
            harness.Submit(mutation: WriteClock(raw: (458752L + 12345L)));
        }

        for (var index = 0; (index < 2); index++) {
            harness.StepWithoutInput();
            await DrainAsync(hold: hold, sink: sink);
        }

        var before = server.Time;
        var written = ClockValue(document: server.Definition, time: before);

        Assert.NotNull(@object: written);
        AssertPresentsAsTheHost(engineTick: before.EngineTick, host: server.Definition, recipient: hold.Definition!, tick: before.Tick);
        AssertNoHiddenRow(held: hold.Definition!, when: "before the seek");

        if (variant == "unnumbered-clock") {
            Assert.True(condition: ((AnchorOf(definition: hold.Definition!) is { } ahead) && (ahead.Tick > (target * Fixtures.StepTicksAt(rateHz: RateHz)))), userMessage: "the control: the viewer holds an anchor stood after the seek's target");
        }

        var log = new DeliveryLog();
        WorldHistorySeekReport report;

        using (server.AttachSink(sink: log)) {
            log.Kinds.Clear();
            log.SnapshotTicks.Clear();
            report = harness.SeekAndProve(target: target);
        }

        await DrainAsync(hold: hold, sink: sink);

        // The door and the span the variant names.
        Assert.Equal(expected: (variant is "load-door" or "unnumbered-clock"), actual: report.RebuiltDocument);
        Assert.Equal(expected: ((int)(target - keyframe)), actual: report.TicksResimulated);

        // One definition and one snapshot at the target, through either door, and nothing for a re-simulated tick.
        Assert.Equal(expected: ["definition", "snapshot"], actual: log.Kinds);
        Assert.Equal(expected: [target], actual: log.SnapshotTicks);

        // Still attached, reading the authority's value at the restored tick, never the write the seek discarded.
        Assert.Null(@object: sink.DetachReason);

        // The restored tick's own time: the rule host's clock moves on the next step.
        var time = ArenaTime.At(engineTick: (target * Fixtures.StepTicksAt(rateHz: RateHz)), tick: target);

        Assert.NotEqual(expected: written, actual: ClockValue(document: server.Definition, time: time));

        // No anchor stood after the authority's tick survives the jump back.
        if (AnchorOf(definition: hold.Definition!) is { } anchor) {
            Assert.True(condition: (anchor.Tick <= time.EngineTick), userMessage: $"the viewer keeps an anchor at engine tick {anchor.Tick}, ahead of the authority's {time.EngineTick}");
        }

        AssertNoHiddenRow(held: hold.Definition!, when: "after the seek");

        // The projection held is the one a viewer attaching now receives: no row the structural edit added or
        // removed, and no value, survives from the timeline the seek left. Its clock anchors may differ from the fresh
        // viewer's, since an anchor is replaced only when its prediction misses, but they present the same phase.
        var fresh = PresentationViewer(server: server);
        var freshHold = new WorldProjectionHold();

        using (server.AttachSink(sink: fresh)) {
            await DrainAsync(hold: freshHold, sink: fresh);
        }

        Assert.Equal(
            expected: WorldDefinitionSerialization.Serialize(definition: WithoutAnchors(definition: freshHold.Definition!)),
            actual: WorldDefinitionSerialization.Serialize(definition: WithoutAnchors(definition: hold.Definition!))
        );
        AssertPresentsTheSame(engineTick: time.EngineTick, host: freshHold.Definition!, recipient: hold.Definition!, tick: time.Tick);

        // Where the clock has a number, the authority presents the same too.
        if (variant != "unnumbered-clock") {
            AssertPresentsTheSame(engineTick: time.EngineTick, host: server.Definition, recipient: hold.Definition!, tick: time.Tick);
        }
        sink.Release();
        fresh.Release();
    }
}
