using System.Text;
using Puck.Commands;
using Puck.Maths;
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
    public void Advancing_and_cycling_bound_observations_move_on_the_recipient_with_nothing_composed_or_sent() {
        var density = new BindableScalar(binding: $"state.{Clock}");

        foreach (var row in new[] {
            Row(advance: PerTick(raw: 37L), raw: 0L),
            Row(cycle: new StateCycle(Output: CycleOutput.Turns, TicksPerStep: 3L), raw: 0L),
        }) {
            var definition = Document(row: row) with {
                RenderRaw = new WorldRenderDefaults(Atmosphere: new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: density))),
                TimelineRaw = null,
            };
            using var fixture = Fixtures.FreshServer(definition: definition);

            var (observation, mirror) = Observe(fixture: fixture);
            var held = mirror.Definition;
            var initial = ClientFixtures.StateMirror(definition: held).Scalar(fallback: float.NaN, scalar: density);
            // Red leg: the cell's value alone, as observations once carried it, never moves on the recipient.
            var literal = held.WithWorldState(rows: [.. held.State.Select(selector: static stored => ((stored.Name.Value == Clock)
                ? (stored with { Advance = null, Cells = [.. (stored.Cells ?? []).Select(selector: static cell => (cell with { Advance = null, Clock = null, Cycle = null }))], Cycle = null })
                : stored))]);
            var work = new WorldProjectionWork();
            var moved = false;
            var stale = false;

            using (WorldProjectionWork.Attribute(work: work)) {
                for (var index = 0; (index < 12); index++) {
                    fixture.Step();
                    var expected = ClientFixtures.StateMirror(definition: fixture.Server.Definition, engineTick: mirror.EngineTick, tick: mirror.Tick).Scalar(fallback: float.NaN, scalar: density);
                    var actual = ClientFixtures.StateMirror(definition: mirror.Definition, engineTick: mirror.EngineTick, tick: mirror.Tick).Scalar(fallback: float.NaN, scalar: density);

                    Assert.Equal(actual: actual, expected: expected);
                    moved |= (actual != initial);
                    stale |= (ClientFixtures.StateMirror(definition: literal, engineTick: mirror.EngineTick, tick: mirror.Tick).Scalar(fallback: float.NaN, scalar: density) != expected);
                }
            }

            Assert.True(condition: moved);
            Assert.True(condition: stale);

            // The recipient advances the cell itself: no tick composes a projection or sends a byte.
            foreach (var kind in WorldProjectionWork.Kinds) {
                Assert.Equal(expected: 0L, actual: work.Read(kind: kind));
            }

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
        Assert.Equal(expected: WorldProjectionDeliveryKind.None, actual: feed.Step(definition: unkeyed, engineTick: Fixtures.StepTicksAt(rateHz: RateHz), tick: 1UL).Kind);
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
    [Fact]
    public void A_composition_that_does_not_flatten_faults_rather_than_detaching_as_a_disclosure_refusal() {
        using var fixture = Fixtures.FreshServer(definition: Document(row: Row(raw: 0L)));
        var sink = new WorldFederationProjectionSink(
            authority: "boot",
            disclosure: static () => new WorldSinkDisclosure(ObserverBodyIndex: -1, Policy: new WorldObserverDisclosure(UpdateSeconds: 0f)),
            revision: static () => 1,
            server: fixture.Server,
            tier: WorldDisclosureTier.Presentation
        );
        using var lease = fixture.Server.AttachSink(sink: sink);
        var installed = fixture.Server.Definition;
        // A look named by a state reference whose row the delivered document does not hold cannot be flattened.
        var unflattenable = (installed with {
            LookRowsRaw = [new WorldLook(
                Motion: WorldLookMotion.Default,
                Name: System.Text.Json.JsonSerializer.Deserialize<Puck.Assets.Documents.DocumentIdentifier>(json: "\"state.missing\"")!,
                Scale: 1f,
                Source: new WorldLookSource.Creation(PrototypeId: installed.Creations[0].Id)
            )],
        });

        var fault = Assert.Throws<InvalidOperationException>(testCode: () => sink.DeliverDefinition(definition: unflattenable, version: fixture.Server.DocumentVersion));

        Assert.Contains(actualString: fault.Message, expectedSubstring: "could not be flattened");
        Assert.Null(@object: sink.DetachReason);

        // Red leg: a disclosure refusal through the same door detaches by name.
        var hidden = installed.WithWorldState(rows: [.. installed.State.Where(predicate: static row => (row.Name.Value != Clock)), Row(raw: 0L, visibility: new StateVisibility(Readers: ["seat1"]))]);

        sink.DeliverDefinition(definition: hidden, version: fixture.Server.DocumentVersion);
        Assert.Equal(expected: WorldFederationProjectionSink.DisclosureDetachReason, actual: sink.DetachReason);
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
            RenderRaw = new WorldRenderDefaults(Atmosphere: new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: density))),
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
    [Fact]
    public void An_eased_binding_presents_what_the_authority_presents_and_its_target_reads_the_stored_value() {
        var eased = new BindableScalar(binding: $"state.{Clock}");
        var target = new BindableScalar(binding: $"state.{Clock}.$target");
        var one = FixedQ4816.FromDouble(value: 1d).Value;
        // An underdamped follower overshoots its target, so the envelope it is clamped to crosses with it.
        var definition = Document(row: Row(dynamics: new StateDynamics(Row: "chase"), max: one, min: 0L, raw: 0L)) with {
            RenderRaw = new WorldRenderDefaults(
                Atmosphere: new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: eased)),
                Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.SunDisc(Intensity: target, Name: "sun")])
            ),
            TimelineRaw = null,
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        var (observation, mirror) = Observe(fixture: fixture);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: Principal.Console, Row: Clock, Key: WorldStateRow.SlotKey.Value, Value: one, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        var work = new WorldProjectionWork();
        var easing = 0;
        var stripped = false;

        using (WorldProjectionWork.Attribute(work: work)) {
            for (var index = 0; (index < (3 * RateHz)); index++) {
                fixture.Step();

                var host = ClientFixtures.StateMirror(definition: fixture.Server.Definition, engineTick: mirror.EngineTick, tick: mirror.Tick);
                var recipient = ClientFixtures.StateMirror(definition: mirror.Definition, engineTick: mirror.EngineTick, tick: mirror.Tick);
                var presented = host.Scalar(fallback: float.NaN, scalar: eased);

                Assert.Equal(expected: presented, actual: recipient.Scalar(fallback: float.NaN, scalar: eased));
                Assert.Equal(expected: 1f, actual: host.Scalar(fallback: float.NaN, scalar: target));
                Assert.Equal(expected: 1f, actual: recipient.Scalar(fallback: float.NaN, scalar: target));

                if (presented == 1f) {
                    continue;
                }

                easing++;

                if (stripped) {
                    continue;
                }

                // Red leg: the stored truth alone, as observations carried it before, presents the target mid-ease.
                var literal = mirror.Definition.WithWorldState(rows: [.. mirror.Definition.State.Select(selector: static row => ((row.Name.Value == Clock)
                    ? (row with { Cells = [.. (row.Cells ?? []).Select(selector: static cell => (cell with { Clock = null, Dynamics = null }))], Dynamics = null })
                    : row))]);

                Assert.Equal(expected: 1f, actual: ClientFixtures.StateMirror(definition: literal, engineTick: mirror.EngineTick, tick: mirror.Tick).Scalar(fallback: float.NaN, scalar: eased));
                stripped = true;
            }
        }

        Assert.True(condition: stripped);
        Assert.InRange(actual: easing, high: int.MaxValue, low: 2);
        // The follower eases on the recipient from the state it was sent once: nothing crosses while it moves.
        Assert.Equal(expected: 0L, actual: work.Read(kind: WorldProjectionWork.Compositions));
        Assert.Equal(expected: 0L, actual: work.Read(kind: WorldProjectionWork.Deltas));
        Assert.Equal(expected: 0L, actual: work.Read(kind: WorldProjectionWork.Documents));
        observation.Dispose();
    }
    [Fact]
    public void A_recipient_presents_forward_from_the_anchor_it_holds_and_never_seeks_back_through_coalesced_ones() {
        var step = Fixtures.StepTicksAt(rateHz: RateHz);
        var quarter = FixedQ4816.FromDouble(value: 0.25d).Value;
        var threeQuarters = FixedQ4816.FromDouble(value: 0.75d).Value;
        // The authority's history: still at a quarter through tick 1, then sought to three quarters at tick 2, where it
        // advances or holds.
        var history = Document(row: Row(raw: quarter));

        foreach (var sought in new[] { Document(row: Row(advance: PerTick(raw: 1024L), raw: threeQuarters)), Document(row: Row(raw: threeQuarters)) }) {
            var feed = new WorldProjectionFeed(recipient: null);

            _ = feed.Compose(arena: Fixtures.Store(definition: history), authority: "boot", definition: history, revision: 1, time: ArenaTime.At(engineTick: step, tick: 1UL));

            // Tick 1's anchor is replaced before a delivery reaches the recipient, which is sent only tick 2's.
            var coalesced = feed.Compose(arena: Fixtures.Store(definition: sought), authority: "boot", definition: sought, revision: 1, time: ArenaTime.At(engineTick: (2UL * step), tick: 2UL));

            Assert.True(condition: WorldProjection.TryToDefinition(definition: out var recipient, projection: coalesced.Projection!, reason: out var reason), userMessage: reason);

            var anchor = AnchorOf(definition: recipient)!;

            Assert.Equal(expected: (2UL * step), actual: anchor.Tick);

            // Forward from the anchor, the recipient presents as the authority at every tick.
            for (var tick = 2UL; (tick < 8UL); tick++) {
                AssertPresentsAsTheHost(engineTick: (tick * step), host: sought, recipient: recipient, tick: tick);
            }

            // Behind it, the recipient presents the anchor's own phase, the latest it was told about, and the
            // prediction refuses the seek by name.
            Assert.True(condition: ClientFixtures.StateMirror(definition: recipient, engineTick: step, tick: 1UL).TryPhase(clock: out _, name: Clock, phase: out var held));
            Assert.Equal(expected: WorldClockAnchor.ToTurn(phase: anchor.Phase), actual: held);

            var refused = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => anchor.Predict(engineTick: step));

            Assert.Contains(actualString: refused.Message, expectedSubstring: "never seeks backward");

            // Red leg: a seek back through the coalesced anchor, holding it or extending its line, answers a phase the
            // authority never presented at tick 1.
            var presentedThen = HostPhase(definition: history, engineTick: step, tick: 1UL);

            Assert.Equal(expected: 0.25d, actual: WorldClockAnchor.ToTurn(phase: presentedThen));
            Assert.NotEqual(expected: presentedThen, actual: unchecked((anchor.Phase - ((ulong)anchor.Rate))));

            // An authority restored to before the anchor it sent re-anchors rather than predicting backward.
            Assert.Equal(expected: WorldProjectionDeliveryKind.Delta, actual: feed.Step(definition: history, engineTick: step, tick: 1UL).Kind);
            Assert.True(condition: feed.Anchors.TryHeld(anchor: out var restored, clock: Clock));
            Assert.Equal(expected: step, actual: restored.Tick);
            Assert.Equal(expected: presentedThen, actual: restored.Phase);
            feed.Release();
        }
    }
    // An authority moved back past the anchor a recipient holds, to a tick whose row reads no number: the next
    // composition or step drops that anchor and carries what a fresh recipient composed there holds, the seed or no
    // anchor. Held, the anchor would present a frozen phase from a future the authority never presented.
    [InlineData("compose", false)]
    [InlineData("compose", true)]
    [InlineData("step", false)]
    [InlineData("step", true)]
    [Theory]
    public void A_held_anchor_ahead_of_a_restored_authority_is_dropped_for_what_a_fresh_recipient_holds(string door, bool seeded) {
        var step = Fixtures.StepTicksAt(rateHz: RateHz);
        var seed = WorldClockAnchor.PhaseOf(kind: CellKind.Fixed, raw: FixedQ4816.FromDouble(value: 0.375d).Value);
        IReadOnlyDictionary<string, ulong>? seeds = (seeded
            ? new Dictionary<string, ulong>(comparer: StringComparer.Ordinal) { [Clock] = seed }
            : null);
        var history = Document(row: Row(raw: FixedQ4816.FromDouble(value: 0.25d).Value));
        var restored = Document(row: Row(raw: 0L, slot: false));
        var restoredTime = ArenaTime.At(engineTick: (3UL * step), tick: 3UL);
        var feed = new WorldProjectionFeed(recipient: null, seeds: seeds);
        var work = new WorldProjectionWork();

        _ = feed.Compose(arena: Fixtures.Store(definition: history), authority: "boot", definition: history, revision: 1, time: ArenaTime.At(engineTick: (8UL * step), tick: 8UL));
        Assert.True(condition: feed.Anchors.TryHeld(anchor: out var future, clock: Clock));
        Assert.Equal(expected: (8UL * step), actual: future.Tick);

        using (WorldProjectionWork.Attribute(work: work)) {
            if (door == "compose") {
                _ = feed.Compose(arena: Fixtures.Store(definition: restored), authority: "boot", definition: restored, revision: 1, time: in restoredTime);
            } else {
                Assert.NotEqual(
                    expected: WorldProjectionDeliveryKind.None,
                    actual: feed.Step(definition: restored, engineTick: restoredTime.EngineTick, tick: restoredTime.Tick).Kind
                );
            }
        }

        var fresh = new WorldProjectionFeed(recipient: null, seeds: seeds);

        _ = fresh.Compose(arena: Fixtures.Store(definition: restored), authority: "boot", definition: restored, revision: 1, time: in restoredTime);

        var freshAnchor = (fresh.Anchors.TryHeld(anchor: out var freshHeld, clock: Clock) ? freshHeld : null);
        var heldAnchor = (feed.Anchors.TryHeld(anchor: out var held, clock: Clock) ? held : null);

        Assert.Equal(actual: (freshAnchor is not null), expected: seeded);
        Assert.Equal(actual: heldAnchor, expected: freshAnchor);
        Assert.Equal(expected: freshAnchor, actual: AnchorOf(definition: WorldProjectionTimeline(feed: feed)));
        Assert.Equal(expected: 1L, actual: work.Read(kind: WorldProjectionWork.AnchorRowsReleased));
    }

    // The timeline a feed's recipient holds, hydrated into a definition the anchor reader takes.
    private static WorldDefinition WorldProjectionTimeline(WorldProjectionFeed feed) {
        Assert.True(condition: WorldProjection.TryToDefinition(definition: out var definition, projection: feed.Held!, reason: out var reason), userMessage: reason);

        return definition;
    }

    // The same contract through the history's own seek. The clock's row reads no number, so a recipient holds the
    // seed anchored at the tick it joined; a seek to a keyframe before that tick leaves it no anchor ahead of the
    // authority once the restored timeline is delivered, and a step after the seek puts none back.
    [Fact]
    public async Task A_seek_behind_an_anchor_leaves_a_presentation_recipient_no_future_anchor() {
        var envelope = Row(max: FixedQ4816.FromDouble(value: 0.75d).Value, min: FixedQ4816.FromDouble(value: 0.375d).Value, raw: FixedQ4816.FromDouble(value: 0.5d).Value, slot: false);

        using var harness = new WorldHistoryHarness(definition: Document(row: envelope));
        var server = harness.Fixture.Server;

        harness.Steps(count: 8);

        var keyframe = harness.History.KeyframeTicks[0];
        var joined = server.Time.EngineTick;
        var sink = new WorldFederationProjectionSink(
            authority: "boot",
            disclosure: static () => new WorldSinkDisclosure(ObserverBodyIndex: -1, Policy: new WorldObserverDisclosure(UpdateSeconds: 1f)),
            revision: static () => 1,
            server: server,
            tier: WorldDisclosureTier.Presentation
        );
        using var lease = server.AttachSink(sink: sink);

        Assert.True(condition: (keyframe < harness.Tick), userMessage: $"the keyframe at {keyframe} does not precede the join at {harness.Tick}");
        _ = harness.SeekAndProve(target: keyframe);
        harness.StepWithoutInput();
        Assert.Null(@object: sink.DetachReason);

        using var wire = new MemoryStream();

        await sink.StreamAsync(ct: TestContext.Current.CancellationToken, output: wire);
        sink.Release();

        using var received = new MemoryStream(buffer: wire.ToArray());
        var hold = new WorldProjectionHold();
        var heldAtJoin = false;

        while (received.Position < received.Length) {
            var frame = await WorldFederationCodec.ReadResponseAsync(ct: TestContext.Current.CancellationToken, stream: received);

            Assert.True(condition: frame.Ok);

            if (frame.Kind == ((byte)WorldFederationResponse.Definition)) {
                Assert.True(condition: WorldFederationCodec.TryDecodeDocument(body: frame.Body.Span, definition: out _, failure: out var failure, hold: hold, tier: out _, version: out _), userMessage: failure.ToString());
            } else if (frame.Kind == ((byte)WorldFederationResponse.ProjectionDelta)) {
                Assert.True(condition: WorldFederationCodec.TryDecodeProjectionDelta(body: frame.Body.Span, definition: out _, failure: out var failure, hold: hold, stamp: out _, valuesOnly: out _, version: out _), userMessage: failure.ToString());
            }

            heldAtJoin |= (AnchorOf(definition: hold.Definition!)?.Tick == joined);
        }

        var anchor = AnchorOf(definition: hold.Definition!);

        // The control: the recipient was sent the seed anchored where it joined, ahead of where the seek moved.
        Assert.True(condition: heldAtJoin);
        Assert.NotNull(@object: anchor);
        Assert.InRange(actual: anchor.Tick, high: server.Time.EngineTick, low: 0UL);
        Assert.Equal(expected: server.ClockSeeds[Clock], actual: anchor.Phase);
    }
}
