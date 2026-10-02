using System.Text;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class ProjectionAnchorLawTests {
    [Fact]
    public void A_large_fixed_clock_preserves_its_fraction_before_converting_to_presentation_precision() {
        foreach (var raw in new[] { long.MaxValue, (long.MinValue + 1L), ((1L << 53) + 1L), (-(1L << 53) - 1L) }) {
            var definition = Document(row: Row(raw: raw));
            var projection = Fixtures.Project(authority: "boot", definition: definition, revision: 1, tier: WorldDisclosureTier.Presentation)!;

            Assert.True(condition: WorldProjection.TryToDefinition(definition: out var recipient, projection: projection, reason: out var reason), userMessage: reason);
            var mirror = ClientFixtures.StateMirror(definition: definition);

            Assert.True(condition: mirror.TryPhase(clock: out _, name: Clock, phase: out var phase));
            Assert.Equal(actual: phase, expected: ((raw & 65535L) / 65536d));
            AssertPresentsAsTheHost(engineTick: 0UL, host: definition, recipient: recipient!, tick: 0UL);
        }
    }
    [Fact]
    public void Advancing_and_cycling_bound_observations_refresh_without_writes_or_a_timeline() {
        var density = new BindableScalar(binding: $"state.{Clock}");

        foreach (var row in new[] {
            Row(advance: PerTick(raw: 37L), raw: 0L),
            Row(cycle: new StateCycle(Output: CycleOutput.Turns, TicksPerStep: 3L), raw: 0L),
        }) {
            var definition = Document(row: row) with {
                RenderRaw = new WorldRenderDefaults(Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: density, Name: "haze")])),
                TimelineRaw = null,
            };
            using var fixture = Fixtures.FreshServer(definition: definition);

            var (observation, mirror) = Observe(fixture: fixture);
            var initial = ClientFixtures.StateMirror(definition: mirror.Definition).Scalar(fallback: float.NaN, scalar: density);
            var moved = false;

            for (var index = 0; (index < 12); index++) {
                fixture.Step();
                var expected = ClientFixtures.StateMirror(definition: fixture.Server.Definition, engineTick: mirror.EngineTick, tick: mirror.Tick).Scalar(fallback: float.NaN, scalar: density);
                var actual = ClientFixtures.StateMirror(definition: mirror.Definition, engineTick: mirror.EngineTick, tick: mirror.Tick).Scalar(fallback: float.NaN, scalar: density);

                Assert.Equal(actual: actual, expected: expected);
                moved |= (actual != initial);
            }

            Assert.True(condition: moved);
            observation.Dispose();
        }
    }
    [Fact]
    public void Removing_the_last_keyed_use_releases_its_anchor_and_sends_no_unused_clock_deltas() {
        var definition = Document(row: Row(advance: new StateAdvance(PerSecondDenominator: 3L, PerSecondNumerator: 1L), raw: 0L));
        var arena = Fixtures.Store(definition: definition);
        var work = new WorldProjectionWork();
        using var attributed = WorldProjectionWork.Attribute(work: work);
        var feed = new WorldProjectionFeed(recipient: null);

        _ = feed.Compose(arena: arena, authority: "boot", definition: definition, revision: 1, time: ArenaTime.Origin);
        Assert.Equal(expected: 1, actual: feed.Anchors.Count);

        var unkeyed = definition with { RenderRaw = new WorldRenderDefaults() };

        _ = feed.Compose(arena: arena, authority: "boot", definition: unkeyed, revision: 1, time: ArenaTime.Origin);

        Assert.Equal(expected: 0, actual: feed.Anchors.Count);
        Assert.Equal(expected: 1L, actual: work.Read(kind: WorldProjectionWork.AnchorRowsReleased));
        Assert.Equal(expected: WorldProjectionDeliveryKind.None, actual: feed.Step(arena: arena, definition: unkeyed, engineTick: Fixtures.StepTicksAt(rateHz: RateHz), tick: 1UL).Kind);
        feed.Release();
        Assert.Equal(expected: work.Read(kind: WorldProjectionWork.AnchorRowsRetained), actual: work.Read(kind: WorldProjectionWork.AnchorRowsReleased));
    }
    [InlineData("{\"provenance\":null}")]
    [InlineData("{\"kits\":null}")]
    [InlineData("{\"kits\":[null]}")]
    [InlineData("{\"observations\":[null]}")]
    [InlineData("{\"timeline\":{},\"timeline\":{}}")]
    [InlineData("{\"timeline\":{\"clocks\":[{\"name\":\"trace\",\"anchor\":{\"tick\":0,\"phase\":0}},null]}}")]
    [InlineData("{\"timeline\":{\"clocks\":[{\"name\":\"trace\",\"periodSeconds\":0}]}}")]
    [InlineData("{\"timeline\":{\"clocks\":[{\"name\":\"trace\",\"spanSeconds\":-1}]}}")]
    [InlineData("{\"timeline\":{\"clocks\":[{\"name\":\"trace\",\"periodSeconds\":1,\"anchor\":{\"tick\":0,\"phase\":1}}]}}")]
    [Theory]
    public void A_malformed_delta_is_refused_without_changing_the_hold(string delta) {
        var definition = Document(row: Row(raw: 0L));
        var feed = new WorldProjectionFeed(recipient: null);
        var whole = feed.Compose(arena: Fixtures.Store(definition: definition), authority: "boot", definition: definition, revision: 1, time: ArenaTime.Origin);
        var hold = new WorldProjectionHold();

        Assert.True(condition: hold.TryHold(definition: out var before, reason: out var reason, utf8Json: whole.Payload), userMessage: reason);

        Assert.False(condition: hold.TryApply(definition: out _, reason: out reason, timelineOnly: out _, utf8Json: Encoding.UTF8.GetBytes(s: delta), valuesOnly: out _));
        Assert.NotEmpty(collection: reason);
        Assert.Same(expected: before, actual: hold.Definition);
        Assert.True(condition: hold.TryApply(definition: out _, reason: out reason, timelineOnly: out _, utf8Json: "{}"u8, valuesOnly: out _), userMessage: reason);
    }
    [Fact]
    public void A_delta_leaf_refuses_negative_versions_and_oversized_payloads_before_mutating_the_hold() {
        var definition = Document(row: Row(raw: 0L));
        var projection = Fixtures.Project(authority: "boot", definition: definition, revision: 1, tier: WorldDisclosureTier.Presentation)!;
        var hold = new WorldProjectionHold();

        Assert.True(condition: hold.TryHold(definition: out var before, reason: out var reason, utf8Json: WorldProjection.SerializeCompact(projection: projection)), userMessage: reason);

        var version = new WorldDocumentVersion(Activation: Guid.NewGuid(), Sequence: 0L);
        var negative = WorldFederationCodec.EncodeProjectionDelta(delta: "{}"u8, engineTick: 0UL, tick: 0UL, version: version with { Sequence = -1L });

        Assert.False(condition: WorldFederationCodec.TryDecodeProjectionDelta(body: negative, definition: out _, failure: out _, hold: hold, stamp: out _, valuesOnly: out _, version: out _));
        Assert.Same(expected: before, actual: hold.Definition);

        // Whitespace padding remains valid JSON; only the wire bound can reject it.
        var payload = new byte[(Puck.Networking.WireLimits.MaxDocumentBytes + 1)];

        payload.AsSpan().Fill(value: ((byte)' '));
        "{}"u8.CopyTo(destination: payload);
        var oversized = WorldFederationCodec.EncodeProjectionDelta(delta: payload, engineTick: 0UL, tick: 0UL, version: version);

        Assert.False(condition: WorldFederationCodec.TryDecodeProjectionDelta(body: oversized, definition: out _, failure: out _, hold: hold, stamp: out _, valuesOnly: out _, version: out _));
        Assert.Same(expected: before, actual: hold.Definition);
    }
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void A_federation_detach_releases_anchors_without_waiting_for_the_socket(bool hidden) {
        using var fixture = Fixtures.FreshServer(definition: Document(row: Row(raw: 0L)));
        var work = new WorldProjectionWork();
        using var attributed = WorldProjectionWork.Attribute(work: work);
        var sink = new WorldFederationProjectionSink(
            authority: "boot",
            disclosure: static () => new WorldSinkDisclosure(ObserverBodyIndex: -1, Policy: new WorldObserverDisclosure(UpdateSeconds: 0f)),
            revision: static () => 1,
            server: fixture.Server,
            tier: WorldDisclosureTier.Presentation
        );
        using var lease = fixture.Server.AttachSink(sink: sink);

        Assert.Equal(expected: 1L, actual: work.Read(kind: WorldProjectionWork.AnchorRowsRetained));

        if (hidden) {
            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(Principal: Principal.Console, Row: Row(raw: 32768L, visibility: new StateVisibility(Readers: ["seat1"]))));
            fixture.Step();
            Assert.Equal(expected: WorldFederationProjectionSink.DisclosureDetachReason, actual: sink.DetachReason);
        } else {
            for (var index = 0; (index < WorldFederationProjectionSink.PendingDeliveryLimit); index++) {
                fixture.Step();
            }

            Assert.Equal(expected: WorldFederationProjectionSink.BackpressureDetachReason, actual: sink.DetachReason);
        }

        Assert.Equal(expected: 1L, actual: work.Read(kind: WorldProjectionWork.AnchorRowsReleased));
        var pending = sink.PendingDeliveries;

        fixture.Step();
        Assert.Equal(expected: pending, actual: sink.PendingDeliveries);
        sink.Release();
        Assert.Equal(expected: 1L, actual: work.Read(kind: WorldProjectionWork.AnchorRowsReleased));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void A_session_follows_deltas_at_their_stamps_without_waiting_for_a_sampled_snapshot(bool followOnDelivery) {
        var definition = Document(row: Row(raw: 0L));
        var arena = Fixtures.Store(definition: definition);
        var feed = new WorldProjectionFeed(recipient: null);
        var whole = feed.Compose(arena: arena, authority: "boot", definition: definition, revision: 1, time: ArenaTime.Origin);

        Assert.True(condition: WorldProjection.TryToDefinition(definition: out var initial, projection: whole.Projection!, reason: out var reason), userMessage: reason);
        var mirror = new WorldSessionMirror(placeholder: initial!);
        var version = new WorldDocumentVersion(Activation: Guid.NewGuid(), Sequence: 0L);

        mirror.DeliverDefinition(definition: initial!, version: version);
        var snapshot = new WorldSnapshot(Authority: "boot", EngineTick: 0UL, Entries: ReadOnlyMemory<EntitySnapshot>.Empty, Revision: 1, StepTicks: 1UL, Tick: 0UL);

        mirror.DeliverSnapshot(snapshot: in snapshot);
        _ = mirror.FollowState();

        if (followOnDelivery) {
            mirror.DocumentDelivered += _ => {
                var current = mirror.FollowState();

                current.Apply(fraction: 1f);
                Assert.Equal(expected: AnchorOf(definition: mirror.Definition)!.Tick, actual: current.Presented.Whole);
            };
        }

        // The render thread misses two deliveries; the next snapshot is not due yet. A moving anchor makes reading
        // the new definition at the old snapshot tick observably different from reading its stamped phase.
        for (var tick = 1UL; (tick <= 2UL); tick++) {
            var anchored = initial! with {
                TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock(Name: Clock, Anchor: new WorldClockAnchor(Phase: (tick << 56), Rate: (1L << 56), Step: 1UL, Tick: tick))]),
            };

            mirror.DeliverState(definition: anchored, stamp: new WorldStateStamp(EngineTick: tick, Everything: false, MovedRows: default, Tick: tick), version: version);
        }

        var state = mirror.FollowState();

        state.Apply(fraction: 1f);
        Assert.Equal(expected: 2UL, actual: state.Presented.Whole);
        Assert.True(condition: state.TryPhase(clock: out _, name: Clock, phase: out var phase));
        Assert.Equal(actual: phase, expected: (2d / 256d));
        Assert.Equal(expected: 0UL, actual: mirror.Tick);
        Assert.Equal(expected: 2UL, actual: mirror.FollowState().Presented.Whole);
    }
    [Fact]
    public void A_mid_step_session_read_composes_at_the_delivery_clock() {
        var definition = Document(row: Row(advance: new StateAdvance(PerSecondDenominator: 3L, PerSecondNumerator: 1L), raw: 0L));

        definition = definition.WithWorldState(rows: [.. definition.State, new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 0L))],
            Kind: CellKind.Int,
            Name: CellName.Parse(candidate: "score"),
            Visibility: new StateVisibility()
        )]);
        using var fixture = Fixtures.FreshServer(definition: definition);

        var (observation, mirror) = Observe(fixture: fixture);
        var reads = new List<(ulong Tick, ulong Phase, WorldClockAnchor? Anchor)>();

        mirror.DocumentDelivered += _ => {
            var now = fixture.Server.DeliveryTime;

            reads.Add(item: (
                now.EngineTick,
                HostPhase(definition: fixture.Server.Definition, engineTick: now.EngineTick, tick: now.Tick),
                AnchorOf(definition: WorldStateReadView.Of(reader: observation.Session, server: fixture.Server).Definition)
            ));
        };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: Principal.Console, Row: "score", Key: WorldStateRow.SlotKey.Value, Value: 1L, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();
        Assert.NotEmpty(collection: reads);

        foreach (var read in reads) {
            Assert.NotNull(@object: read.Anchor);
            Assert.Equal(expected: read.Tick, actual: read.Anchor.Tick);
            Assert.Equal(expected: read.Phase, actual: read.Anchor.Phase);
        }

        observation.Dispose();
    }
    [Fact]
    public void Removing_an_observation_reinstalls_the_remaining_row_ordinals_locally_and_on_the_wire() {
        var density = new BindableScalar(binding: $"state.{Clock}");
        var first = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 3L))],
            Kind: CellKind.Int,
            Name: CellName.Parse(candidate: "first"),
            Visibility: new StateVisibility()
        );
        var definition = Document(row: Row(raw: 1000L)) with {
            RenderRaw = new WorldRenderDefaults(Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: density, Name: "haze")])),
        };

        definition = definition.WithWorldState(rows: [first, .. definition.State]);
        using var fixture = Fixtures.FreshServer(definition: definition);

        var (observation, mirror) = Observe(fixture: fixture);
        var expected = (1000f / 65536f);

        Assert.Equal(expected: expected, actual: mirror.FollowState().Scalar(fallback: float.NaN, scalar: density));
        var revision = mirror.DefinitionRevision;

        var feed = new WorldProjectionFeed(recipient: null);
        var whole = feed.Compose(arena: fixture.Server.Arena, authority: "boot", definition: fixture.Server.Definition, revision: 1, time: fixture.Server.DeliveryTime);
        var hold = new WorldProjectionHold();

        Assert.True(condition: hold.TryHold(definition: out _, reason: out var reason, utf8Json: whole.Payload), userMessage: reason);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(Principal: Principal.Console, Row: first with { Visibility = new StateVisibility(Readers: ["seat1"]) }));
        fixture.Step();
        Assert.True(condition: (mirror.DefinitionRevision > revision));
        Assert.Equal(expected: expected, actual: mirror.FollowState().Scalar(fallback: float.NaN, scalar: density));

        var delta = feed.Compose(arena: fixture.Server.Arena, authority: "boot", definition: fixture.Server.Definition, revision: 1, time: fixture.Server.DeliveryTime);

        Assert.Equal(expected: WorldProjectionDeliveryKind.Delta, actual: delta.Kind);
        Assert.False(condition: delta.ValuesOnly);
        Assert.True(condition: hold.TryApply(definition: out var next, reason: out reason, timelineOnly: out _, utf8Json: delta.Payload, valuesOnly: out var valuesOnly), userMessage: reason);
        Assert.False(condition: valuesOnly);
        Assert.Equal(expected: expected, actual: ClientFixtures.StateMirror(definition: next!).Scalar(fallback: float.NaN, scalar: density));
        observation.Dispose();
        feed.Release();
    }
}
