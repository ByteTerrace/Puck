using Puck.Commands;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a presentation-tier recipient presents a state clock from anchors. The authority and the
/// recipient call one prediction (<see cref="WorldClockAnchor.Predict"/>), the authority sends an anchor exactly at the
/// authoritative ticks the recipient's prediction misses its own phase, so the recipient's phase equals the
/// authority's, bit for bit, at every tick of a mixed trace with no spurious anchor; a steady sky sends nothing; a late
/// join hydrates the exact current phase, or seeds a clock that reads none from its load-validated phase; a clock
/// whose row the recipient may not read refuses before any derived value is emitted; and the last anchor per recipient
/// per clock is a counted row, released when the recipient leaves or loses disclosure.
/// </summary>
public sealed partial class ProjectionAnchorLawTests(ITestOutputHelper output) {
    private const string Clock = "trace";
    private const string Viewer = "viewer/portal";

    private static readonly int RateHz = Fixtures.DefaultRateHz;

    // A rate of whole raw units per tick, which an advance proves affine.
    private static StateAdvance PerTick(long raw) => new(
        PerSecondDenominator: (1L << FixedQ4816.FractionBitCount),
        PerSecondNumerator: (raw * RateHz)
    );
    private static WorldStateRow Row(long raw, StateAdvance? advance = null, StateDynamics? dynamics = null, StateCycle? cycle = null, StateVisibility? visibility = null, long? min = null, long? max = null, bool slot = true) => new(
        Advance: advance,
        Cells: (slot
            ? [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: raw))]
            : [new StateCell(Key: CellName.Parse(candidate: "other"), Value: CellValue.Fixed(rawBits: raw))]),
        Cycle: cycle,
        Dynamics: dynamics,
        Kind: CellKind.Fixed,
        Max: max,
        Min: min,
        Name: CellName.Parse(candidate: Clock),
        Visibility: visibility
    );

    private static BindableScalar Density { get; } = new(keys: new WorldKeyTrack<float>(clock: Clock, keys: [
        new WorldKey<float>(At: 0d, Ease: WorldEase.Linear, Value: 0f),
        new WorldKey<float>(At: 0.5d, Ease: WorldEase.Smooth, Value: 0.1f),
    ]));

    // The fixture world with a presentation-tier admission row, a state clock over one Fixed row, and a fog density
    // keyed on it.
    private static WorldDefinition Document(WorldStateRow row) {
        var document = Fixtures.BuildGradientUpDocument(gradientUp: false);

        return (document with {
            Admission = [new WorldAdmissionEntry(
                Algorithm: string.Empty,
                Disclosure: WorldDisclosureTier.Presentation,
                Domain: WorldAdmissionEntry.AnyAuthority,
                Grants: [new WorldAdmissionGrant(
                    Budget: 64,
                    Capability: WorldCapability.Observe,
                    Subject: GrantSubject.All
                )],
                Mode: WorldAdmissionTrustMode.FederatedAuthority,
                PublicKey: string.Empty,
                Subject: null
            )],
            DynamicsRaw = [new DynamicsRow(Damping: 0.6f, Frequency: 1.5f, Name: "chase", Response: 0f)],
            RenderRaw = new WorldRenderDefaults(Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: Density, Name: "haze")])),
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock(Name: Clock, State: Clock)]),
        }).WithWorldState(rows: [.. document.State.Where(predicate: static existing => (existing.Name.Value != Clock)), row]);
    }
    private static (WorldSessionObservation Observation, WorldSessionMirror Mirror) Observe(WorldFixture fixture) {
        var mirror = new WorldSessionMirror(placeholder: WorldProjection.Undisclosed);
        var observation = fixture.Server.TryObserveAsSession(
            refusal: out var refusal,
            sink: mirror,
            sourceAuthority: Viewer
        );

        Assert.True(condition: (observation is not null), userMessage: refusal);

        return (observation!, mirror);
    }
    private static WorldClockAnchor? AnchorOf(WorldDefinition definition) => (WorldKeyResolver.TryClock(
        clock: out var clock,
        name: Clock,
        timeline: definition.Timeline
    )
        ? clock.Anchor
        : null);
    // The phase the authority's own presentation reads at a tick: its row's value through the engine's eased read.
    private static ulong HostPhase(WorldDefinition definition, ulong tick, ulong engineTick) {
        Assert.True(condition: WorldStateReader.TryReadEased(
            definition: definition,
            engineTick: engineTick,
            key: null,
            rawValue: out var raw,
            row: out _,
            rowName: Clock,
            text: out _,
            tick: tick
        ));

        return WorldClockAnchor.PhaseOf(kind: CellKind.Fixed, raw: raw!.Value);
    }
    // The authority's presentation and the recipient's, each through a real state mirror at the same tick: the clock's
    // phase and the keyed fog it resolves.
    // The records the kernels read for a resolved environment: the light table, and the sky's block, layers and
    // softboxes as the sky packs them over those lights.
    private static (SdfLight[] Lights, SdfSkyBlock Block, SdfSkyLayer[] Stops, SdfSoftbox[] Softboxes) Packed(WorldResolvedEnvironment environment) {
        var layers = new SdfSkyLayer[SdfSky.MaxLayers];
        var softboxes = new SdfSoftbox[SdfSky.MaxSoftboxes];

        environment.Sky.Pack(
            block: out var block,
            details: new SdfSkyDetails(),
            layers: layers,
            lights: environment.Lights,
            softboxes: softboxes
        );

        return (environment.Lights.Records.ToArray(), block, layers, softboxes);
    }
    private static void AssertPresentsAsTheHost(WorldDefinition host, WorldDefinition recipient, ulong tick, ulong engineTick) {
        var hostMirror = ClientFixtures.StateMirror(definition: host, engineTick: engineTick, tick: tick);
        var recipientMirror = ClientFixtures.StateMirror(definition: recipient, engineTick: engineTick, tick: tick);

        Assert.True(condition: hostMirror.TryPhase(clock: out _, name: Clock, phase: out var hostPhase));
        Assert.True(condition: recipientMirror.TryPhase(clock: out _, name: Clock, phase: out var recipientPhase));
        Assert.Equal(actual: recipientPhase, expected: hostPhase);
        Assert.Equal(
            expected: hostMirror.Scalar(fallback: float.NaN, scalar: Density),
            actual: recipientMirror.Scalar(fallback: float.NaN, scalar: Density)
        );
    }

    [InlineData("src/Puck.World/Assets/worlds/moth-courtyard.puck")]
    [InlineData("tests/Puck.Parity/parity.world.json")]
    [Theory]
    public void A_presentation_recipient_of_a_shipped_sky_keyed_on_a_state_clock_presents_it_as_the_authority(string path) {
        var world = AuthoredGameFixtures.Load(relativePath: path);

        foreach (var value in new[] { 0d, 0.25d, 0.5d, 0.625d, 0.75d }) {
            var host = world.WithWorldState(rows: [
                .. world.State.Where(predicate: static row => (row.Name.Value != "skyMode")),
                new WorldStateRow(
                    Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: value).Value))],
                    Kind: CellKind.Fixed,
                    Name: CellName.Parse(candidate: "skyMode")
                ),
            ]);
            var projection = Fixtures.Project(authority: "boot", definition: host, revision: 1, tier: WorldDisclosureTier.Presentation)!;

            Assert.True(condition: WorldProjection.TryToDefinition(definition: out var hydrated, projection: projection, reason: out var reason), userMessage: reason);

            var expected = Packed(environment: new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(definition: host, mirror: ClientFixtures.StateMirror(definition: host), revision: 0));
            var actual = Packed(environment: new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(definition: hydrated, mirror: ClientFixtures.StateMirror(definition: hydrated), revision: 0));

            Assert.Equal(actual: actual.Lights, expected: expected.Lights);
            Assert.Equal(actual: actual.Block, expected: expected.Block);
            Assert.Equal(actual: actual.Stops, expected: expected.Stops);
            Assert.Equal(actual: actual.Softboxes, expected: expected.Softboxes);
        }
    }
    [Fact]
    public void An_authored_document_never_carries_an_anchor() {
        var authored = Document(row: Row(raw: 0L));
        var anchored = (authored with { TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock(Name: Clock, State: Clock, Anchor: new WorldClockAnchor(Tick: 0UL, Phase: 0UL))]) });

        _ = WorldDefinitionValidator.TryValidateLocally(definition: authored, reason: out var control);
        _ = WorldDefinitionValidator.TryValidateLocally(definition: anchored, reason: out var refused);

        Assert.DoesNotContain(actualString: (control ?? string.Empty), expectedSubstring: ".anchor");
        Assert.Contains(actualString: (refused ?? string.Empty), expectedSubstring: "timeline.clocks[0].anchor is what a projection carries for a state clock it discloses");
    }
    [Fact]
    public void The_prediction_is_exact_at_every_authoritative_tick_through_the_wrap_and_refuses_a_tick_before_its_anchor() {
        const ulong Step = 1680UL;

        foreach (var rate in new[] { (1L << 56), -(3L << 50), long.MaxValue, (long.MinValue + 1L) }) {
            var anchor = new WorldClockAnchor(Phase: 0xFEDC_BA98_7654_3210UL, Rate: rate, Step: Step, Tick: 1_000_000UL);

            foreach (var k in new long[] { 0L, 1L, 2L, 255L, 65_537L }) {
                Assert.Equal(
                    expected: unchecked((anchor.Phase + (((ulong)rate) * ((ulong)k)))),
                    actual: anchor.Predict(engineTick: ((ulong)(((long)anchor.Tick) + (k * ((long)Step)))))
                );
            }

            foreach (var k in new long[] { -1L, -40L }) {
                _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => anchor.Predict(engineTick: ((ulong)(((long)anchor.Tick) + (k * ((long)Step))))));
            }
        }

        var still = new WorldClockAnchor(Phase: 42UL, Tick: 7UL);

        Assert.Equal(expected: 42UL, actual: still.Predict(engineTick: ulong.MaxValue));
        // A Fixed value's phase is its fractional bits, exactly as the double a local presentation reads.
        foreach (var value in new[] { 0.25d, -0.25d, 3.75d, 1234.0078125d }) {
            var raw = FixedQ4816.FromDouble(value: value).Value;

            Assert.Equal(
                expected: WorldClocks.Phase(value: (raw / 65536d)),
                actual: WorldClockAnchor.ToTurn(phase: WorldClockAnchor.PhaseOf(kind: CellKind.Fixed, raw: raw))
            );
        }
    }
    [Fact]
    public void A_mixed_trace_matches_the_host_phase_at_every_tick_and_sends_no_spurious_anchor() {
        var work = new WorldProjectionWork();

        using var attributed = WorldProjectionWork.Attribute(work: work);
        using var fixture = Fixtures.FreshServer(definition: Document(row: Row(raw: FixedQ4816.FromDouble(value: 0.25d).Value)));

        var (observation, mirror) = Observe(fixture: fixture);
        var expected = 1L;
        var moving = 0;
        var anchored = new bool[130];
        var segments = new Dictionary<int, WorldStateRow> {
            // An affine span: whole raw units every tick.
            [8] = Row(advance: PerTick(raw: 1024L), raw: 0L),
            // A rate change, downward.
            [24] = Row(advance: PerTick(raw: -3001L), raw: FixedQ4816.FromDouble(value: 0.5d).Value),
            // A quantized advance: a third of a unit a second never sums to whole raw units a tick.
            [40] = Row(advance: new StateAdvance(PerSecondDenominator: 3L, PerSecondNumerator: 1L), raw: 0L),
            // A staircase: a turn in steps that each hold three ticks.
            [56] = Row(cycle: new StateCycle(Output: CycleOutput.Turns, TicksPerStep: 3L), raw: 0L),
            // A nonlinear row: an eased follower chasing a target sought far from it.
            [80] = Row(dynamics: new StateDynamics(Row: "chase"), raw: FixedQ4816.FromDouble(value: 0.875d).Value),
            // Seeks: a still row written by value.
            [110] = Row(raw: FixedQ4816.FromDouble(value: 0.125d).Value),
            [113] = Row(raw: FixedQ4816.FromDouble(value: 0.625d).Value),
        };

        for (var index = 0; (index < 130); index++) {
            if (segments.TryGetValue(key: index, value: out var row)) {
                fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(Principal: Principal.Console, Row: row));
            }

            var before = AnchorOf(definition: mirror.Definition);

            fixture.Step();

            var host = fixture.Server.Definition;
            var phase = HostPhase(definition: host, engineTick: mirror.EngineTick, tick: mirror.Tick);
            var after = AnchorOf(definition: mirror.Definition);

            Assert.NotNull(@object: after);
            // Bit for bit: the recipient's prediction at the authoritative tick is the authority's phase.
            Assert.Equal(expected: phase, actual: after.Predict(engineTick: mirror.EngineTick));
            AssertPresentsAsTheHost(engineTick: mirror.EngineTick, host: host, recipient: mirror.Definition, tick: mirror.Tick);

            // An anchor arrives exactly where the prediction the recipient held missed.
            if ((before is null) || (before.Predict(engineTick: mirror.EngineTick) != phase)) {
                expected++;
                anchored[index] = true;
            } else {
                Assert.Equal(actual: after, expected: before);
            }

            if (after.Rate != 0L) {
                moving++;
            }
        }

        Assert.Equal(expected: expected, actual: work.Read(kind: WorldProjectionWork.Anchors));

        int Anchors(int from, int to) => anchored[from..to].Count(predicate: static sent => sent);

        // Each affine span anchors once, where it starts, with the rate it was proved to hold; a quantized advance and
        // an eased row re-anchor at every tick they move, a staircase at every step, a seek once, and a still row never.
        Assert.Equal(expected: 0, actual: Anchors(from: 0, to: 8));
        Assert.True(condition: (anchored[8] && anchored[24]));
        Assert.Equal(expected: 2, actual: Anchors(from: 8, to: 40));
        Assert.Equal(expected: 16, actual: Anchors(from: 40, to: 56));
        Assert.InRange(actual: Anchors(from: 57, to: 80), high: 8, low: 7);
        Assert.InRange(actual: Anchors(from: 80, to: 110), high: 30, low: 25);
        Assert.Equal(expected: 2, actual: Anchors(from: 110, to: 130));
        // Every tick of the two affine spans, and no other, presents a moving anchor.
        Assert.Equal(actual: moving, expected: 32);
        observation.Dispose();
    }
    [Fact]
    public void A_steady_sky_sends_nothing_whether_its_clock_holds_still_or_moves_along_a_proved_span() {
        foreach (var row in new[] { Row(raw: FixedQ4816.FromDouble(value: 0.5d).Value), Row(advance: PerTick(raw: 777L), raw: 0L) }) {
            using var fixture = Fixtures.FreshServer(definition: Document(row: row));

            var (observation, mirror) = Observe(fixture: fixture);

            fixture.Step();

            var work = new WorldProjectionWork();

            using (WorldProjectionWork.Attribute(work: work)) {
                for (var index = 0; (index < (2 * RateHz)); index++) {
                    fixture.Step();
                    Assert.Equal(
                        expected: HostPhase(definition: fixture.Server.Definition, engineTick: mirror.EngineTick, tick: mirror.Tick),
                        actual: AnchorOf(definition: mirror.Definition)!.Predict(engineTick: mirror.EngineTick)
                    );
                }
            }

            foreach (var kind in WorldProjectionWork.Kinds) {
                Assert.Equal(expected: 0L, actual: work.Read(kind: kind));
            }

            observation.Dispose();
        }
    }
    [Fact]
    public void A_late_join_hydrates_the_exact_current_phase() {
        // A quantized advance moves the phase every tick along no proved span, so no anchor composed earlier holds.
        using var fixture = Fixtures.FreshServer(definition: Document(row: Row(advance: new StateAdvance(PerSecondDenominator: 7L, PerSecondNumerator: 3L), raw: 0L)));

        for (var index = 0; (index < 47); index++) {
            fixture.Step();
        }

        var work = new WorldProjectionWork();
        var delivered = new List<WorldDefinition>();
        var mirror = new WorldSessionMirror(placeholder: WorldProjection.Undisclosed);

        mirror.DocumentDelivered += document => delivered.Add(item: document.Definition);

        WorldSessionObservation? observation;

        using (WorldProjectionWork.Attribute(work: work)) {
            observation = fixture.Server.TryObserveAsSession(refusal: out var refusal, sink: mirror, sourceAuthority: Viewer);
            Assert.True(condition: (observation is not null), userMessage: refusal);
        }

        // The whole projection the join hydrates already holds the exact phase: no correcting anchor follows it.
        Assert.Equal(expected: 1L, actual: work.Read(kind: WorldProjectionWork.Documents));
        Assert.Equal(expected: 0L, actual: work.Read(kind: WorldProjectionWork.Deltas));

        var time = fixture.Server.Time;
        var anchor = AnchorOf(definition: delivered[0]);

        Assert.NotNull(@object: anchor);
        Assert.Equal(expected: time.EngineTick, actual: anchor.Tick);
        Assert.Equal(
            expected: HostPhase(definition: fixture.Server.Definition, engineTick: time.EngineTick, tick: time.Tick),
            actual: anchor.Predict(engineTick: time.EngineTick)
        );
        Assert.NotEqual(expected: 0UL, actual: anchor.Phase);
        AssertPresentsAsTheHost(engineTick: time.EngineTick, host: fixture.Server.Definition, recipient: delivered[0], tick: time.Tick);
        observation!.Dispose();
    }
    [Fact]
    public void A_late_view_seeds_a_clock_that_reads_no_phase_from_its_load_validated_phase_while_an_early_view_holds_its_anchor() {
        var loaded = FixedQ4816.FromDouble(value: 0.25d).Value;

        using var fixture = Fixtures.FreshServer(definition: Document(row: Row(raw: loaded)));

        var (early, earlyMirror) = Observe(fixture: fixture);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(Principal: Principal.Console, Row: Row(raw: FixedQ4816.FromDouble(value: 0.625d).Value)));
        fixture.Step();
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(Principal: Principal.Console, Row: Row(raw: 0L, slot: false)));
        fixture.Step();
        fixture.Step();

        var (late, lateMirror) = Observe(fixture: fixture);

        // The early view keeps predicting the last phase it was sent; the late one seeds from the phase the world
        // loaded with, which its validation admitted.
        Assert.Equal(expected: WorldClockAnchor.PhaseOf(kind: CellKind.Fixed, raw: FixedQ4816.FromDouble(value: 0.625d).Value), actual: AnchorOf(definition: earlyMirror.Definition)!.Phase);
        Assert.Equal(expected: WorldClockAnchor.PhaseOf(kind: CellKind.Fixed, raw: loaded), actual: AnchorOf(definition: lateMirror.Definition)!.Phase);
        early.Dispose();
        late.Dispose();

        // A clock that read no number at load either seeds from its row's closed envelope, zero clamped into it.
        var closed = Document(row: Row(max: FixedQ4816.FromDouble(value: 0.75d).Value, min: FixedQ4816.FromDouble(value: 0.375d).Value, raw: 0L, slot: false));

        Assert.Equal(
            expected: WorldClockAnchor.PhaseOf(kind: CellKind.Fixed, raw: FixedQ4816.FromDouble(value: 0.375d).Value),
            actual: WorldClockAnchors.Seeds(definition: closed)[Clock]
        );
        Assert.False(condition: WorldClockAnchors.Seeds(definition: Document(row: Row(raw: 0L, slot: false))).ContainsKey(key: Clock));
    }
    [Fact]
    public void A_hidden_clock_refuses_before_any_derived_value_is_emitted() {
        var hidden = Document(row: Row(raw: FixedQ4816.FromDouble(value: 0.5d).Value, visibility: new StateVisibility(Readers: ["seat1"])));

        Laws.RefusalWithControl(
            lawId: "projection.hidden-clock-sends-no-derived-value",
            deniedOutcome: () => {
                using var fixture = Fixtures.FreshServer(definition: hidden);
                var mirror = new WorldSessionMirror(placeholder: WorldProjection.Undisclosed);
                var observation = fixture.Server.TryObserveAsSession(refusal: out var refusal, sink: mirror, sourceAuthority: Viewer);

                Assert.Contains(actualString: refusal, expectedSubstring: "may not read");
                Assert.Null(@object: AnchorOf(definition: mirror.Definition));
                Assert.Throws<WorldDisclosureException>(testCode: () => Fixtures.Project(authority: "boot", definition: hidden, revision: 1, tier: WorldDisclosureTier.Presentation));

                return (observation is not null);
            },
            controlOutcome: () => {
                using var fixture = Fixtures.FreshServer(definition: Document(row: Row(raw: FixedQ4816.FromDouble(value: 0.5d).Value)));

                var (observation, mirror) = Observe(fixture: fixture);
                var reaches = (AnchorOf(definition: mirror.Definition) is not null);

                observation.Dispose();

                return reaches;
            }
        );
    }
    [Fact]
    public void A_value_bound_to_state_the_recipient_may_not_read_refuses_by_name_before_anything_is_emitted() {
        static WorldDefinition Bound(StateVisibility? visibility) {
            var document = Document(row: Row(raw: 0L));

            return (document with {
                RenderRaw = new WorldRenderDefaults(Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: new BindableScalar(binding: "state.secret"), Name: "haze")])),
            }).WithWorldState(rows: [
                .. document.State,
                new WorldStateRow(
                    Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: 0.01d).Value))],
                    Kind: CellKind.Fixed,
                    Name: CellName.Parse(candidate: "secret"),
                    Visibility: visibility
                ),
            ]);
        }

        Laws.RefusalWithControl(
            lawId: "projection.hidden-binding-sends-no-derived-value",
            deniedOutcome: () => {
                var hidden = Bound(visibility: new StateVisibility(Readers: ["seat1"]));
                var thrown = Record.Exception(testCode: () => Fixtures.Project(authority: "boot", definition: hidden, revision: 1, tier: WorldDisclosureTier.Presentation));

                using var fixture = Fixtures.FreshServer(definition: hidden);
                var mirror = new WorldSessionMirror(placeholder: WorldProjection.Undisclosed);
                var observation = fixture.Server.TryObserveAsSession(refusal: out var refusal, sink: mirror, sourceAuthority: Viewer);

                if (thrown is null) {
                    observation?.Dispose();

                    return true;
                }

                // The observer is handed nothing: neither the value at its fallback nor any other member.
                Assert.Same(expected: WorldProjection.Undisclosed, actual: mirror.Definition);
                Assert.Equal(expected: "render.sky.layers[0].density binds state row 'secret' this recipient may not read; a hidden source sends no derived value.", actual: thrown.Message);
                Assert.Contains(actualString: refusal, expectedSubstring: "binds state row 'secret'");

                return (observation is not null);
            },
            controlOutcome: () => (Fixtures.Project(authority: "boot", definition: Bound(visibility: null), revision: 1, tier: WorldDisclosureTier.Presentation) is not null)
        );
    }
    [Fact]
    public void A_value_bound_to_a_row_the_recipient_may_read_crosses_and_follows_the_row_never_its_fallback() {
        var document = Document(row: Row(raw: 0L));
        var density = new BindableScalar(binding: "state.mist");

        static long Raw(double value) => FixedQ4816.FromDouble(value: value).Value;
        var definition = (document with {
            RenderRaw = new WorldRenderDefaults(Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: density, Name: "haze")])),
        }).WithWorldState(rows: [
            .. document.State,
            new WorldStateRow(
                Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: Raw(value: 0.02d)))],
                Kind: CellKind.Fixed,
                Name: CellName.Parse(candidate: "mist")
            ),
        ]);

        using var fixture = Fixtures.FreshServer(definition: definition);

        var (observation, mirror) = Observe(fixture: fixture);
        float Presented() => ClientFixtures.StateMirror(definition: mirror.Definition).Scalar(fallback: float.NaN, scalar: in density);

        // The row declares no policy, so any recipient may read it: it crosses, and the value reads it.
        Assert.Equal(expected: ((float)(Raw(value: 0.02d) / 65536d)), actual: Presented());

        // A write reaches the recipient as a delta of the row's value, never a fallback in between.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: Principal.Console, Row: "mist", Key: WorldStateRow.SlotKey.Value, Value: Raw(value: 0.05d), Kind: WorldDocumentWriteKind.Set));
        fixture.Step();
        Assert.Equal(expected: ((float)(Raw(value: 0.05d) / 65536d)), actual: Presented());
        observation.Dispose();
    }
    [Fact]
    public void The_last_anchor_per_recipient_per_clock_is_a_row_released_when_the_recipient_leaves_or_loses_disclosure() {
        var work = new WorldProjectionWork();

        using var attributed = WorldProjectionWork.Attribute(work: work);
        using var fixture = Fixtures.FreshServer(definition: Document(row: Row(advance: PerTick(raw: 512L), raw: 0L)));

        long Held() => (work.Read(kind: WorldProjectionWork.AnchorRowsRetained) - work.Read(kind: WorldProjectionWork.AnchorRowsReleased));

        var (first, _) = Observe(fixture: fixture);
        var (second, secondMirror) = Observe(fixture: fixture);

        fixture.Step();
        Assert.Equal(expected: 2L, actual: Held());

        // Losing disclosure releases the row; regaining it retains one again.
        var view = Assert.Single(
            collection: fixture.Server.GrantRows(principal: second.Session),
            predicate: static row => ((row.Capability == WorldCapability.Observe) && (row.Subject == GrantSubject.All))
        );

        fixture.Server.Revoke(actor: Principal.Console, grant: view);
        fixture.Step();
        Assert.Equal(expected: 1L, actual: Held());
        fixture.Server.Grant(actor: Principal.Console, grant: view);
        fixture.Step();
        Assert.Equal(expected: 2L, actual: Held());
        Assert.NotNull(@object: AnchorOf(definition: secondMirror.Definition));

        // Leaving releases it.
        first.Dispose();
        Assert.Equal(expected: 1L, actual: Held());
        second.Dispose();
        Assert.Equal(expected: 0L, actual: Held());
    }
    [Fact]
    public void Other_values_travel_as_deltas_of_the_members_that_changed_and_only_when_they_change() {
        var observed = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 1L))],
            Kind: CellKind.Int,
            Name: CellName.Parse(candidate: "score"),
            Visibility: new StateVisibility()
        );
        var quiet = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 1L))],
            Kind: CellKind.Int,
            Name: CellName.Parse(candidate: "quiet")
        );
        var document = Document(row: Row(raw: 0L));

        using var fixture = Fixtures.FreshServer(definition: document.WithWorldState(rows: [.. document.State, observed, quiet]));

        var (observation, mirror) = Observe(fixture: fixture);

        fixture.Step();

        var revision = mirror.DefinitionRevision;
        var work = new WorldProjectionWork();

        long Score() => Assert.Single(collection: mirror.Definition.State, predicate: static row => (row.Name.Value == "score")).Cells![0].Value.AsInt;

        using (WorldProjectionWork.Attribute(work: work)) {
            // A value the projection does not carry moves nothing the recipient holds: nothing is sent.
            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: Principal.Console, Row: "quiet", Key: WorldStateRow.SlotKey.Value, Value: 9L, Kind: WorldDocumentWriteKind.Set));
            fixture.Step();
            Assert.Equal(expected: 0L, actual: (work.Read(kind: WorldProjectionWork.Deltas) + work.Read(kind: WorldProjectionWork.Documents)));

            // A disclosed value travels alone, as values, into the definition the recipient already holds.
            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: Principal.Console, Row: "score", Key: WorldStateRow.SlotKey.Value, Value: 7L, Kind: WorldDocumentWriteKind.Set));
            fixture.Step();
        }

        Assert.Equal(expected: 1L, actual: work.Read(kind: WorldProjectionWork.Deltas));
        Assert.Equal(expected: 0L, actual: work.Read(kind: WorldProjectionWork.Documents));
        Assert.InRange(actual: work.Read(kind: WorldProjectionWork.Bytes), high: 1024L, low: 1L);
        Assert.Equal(expected: 7L, actual: Score());
        Assert.Equal(expected: revision, actual: mirror.DefinitionRevision);
        observation.Dispose();
    }
    [Fact]
    public void A_federation_recipient_merges_each_delta_over_the_projection_it_holds_and_takes_the_authoritys_anchors() {
        var row = Row(advance: new StateAdvance(PerSecondDenominator: 3L, PerSecondNumerator: 1L), raw: 0L);
        var definition = Document(row: row);
        var arena = Fixtures.Store(definition: definition);
        var feed = new WorldProjectionFeed(recipient: null);
        var hold = new WorldProjectionHold();
        var version = new WorldDocumentVersion(Activation: Guid.NewGuid(), Sequence: 1L);
        var step = Fixtures.StepTicksAt(rateHz: RateHz);

        ArenaTime At(ulong tick) => (ArenaTime.At(engineTick: (tick * step), tick: tick) with { TicksPerSecond = RateHz });

        // A delta before any whole projection has nothing to merge over.
        Assert.False(condition: WorldFederationCodec.TryDecodeProjectionDelta(
            body: WorldFederationCodec.EncodeProjectionDelta(delta: "{}"u8, engineTick: 0UL, tick: 0UL, version: version),
            definition: out _,
            failure: out var early,
            hold: hold,
            stamp: out _,
            valuesOnly: out _,
            version: out _
        ));
        Assert.Contains(expectedSubstring: "before any whole projection", actualString: early.Detail);

        var whole = feed.Compose(arena: arena, authority: "boot", definition: definition, revision: 1, time: At(tick: 3UL));

        Assert.Equal(expected: WorldProjectionDeliveryKind.Document, actual: whole.Kind);
        Assert.True(condition: WorldFederationCodec.TryDecodeDocument(
            body: WorldFederationCodec.DocumentLeaf(payload: whole.Payload, tier: WorldDisclosureTier.Presentation, version: version),
            definition: out var held,
            failure: out var failure,
            hold: hold,
            tier: out _,
            version: out _
        ), userMessage: failure.ToString());

        var deltas = 0;

        for (var tick = 4UL; (tick < 40UL); tick++) {
            var delivery = feed.Step(definition: definition, engineTick: (tick * step), tick: tick);

            if (delivery.Kind == WorldProjectionDeliveryKind.None) {
                continue;
            }

            Assert.Equal(expected: WorldProjectionDeliveryKind.Delta, actual: delivery.Kind);
            Assert.True(condition: delivery.TimelineOnly);
            Assert.True(condition: WorldFederationCodec.TryDecodeProjectionDelta(
                body: WorldFederationCodec.EncodeProjectionDelta(delta: delivery.Payload, engineTick: (tick * step), tick: tick, version: version),
                definition: out held,
                failure: out failure,
                hold: hold,
                stamp: out var stamp,
                valuesOnly: out var valuesOnly,
                version: out _
            ), userMessage: failure.ToString());
            Assert.True(condition: valuesOnly);
            Assert.False(condition: stamp.Everything);
            Assert.Equal(expected: (tick * step), actual: stamp.EngineTick);
            Assert.Equal(expected: AnchorOf(definition: (WorldProjection.TryToDefinition(definition: out var expected, projection: feed.Held!, reason: out _) ? expected : null!)), actual: AnchorOf(definition: held!));
            Assert.Equal(expected: HostPhase(definition: definition, engineTick: (tick * step), tick: tick), actual: AnchorOf(definition: held!)!.Predict(engineTick: (tick * step)));
            deltas++;
        }

        Assert.InRange(actual: deltas, high: 36, low: 30);
    }
    [Fact]
    public void Bytes_per_recipient_per_second_are_counted_for_a_steady_sky_a_busy_sky_a_nonlinear_clock_and_a_late_join() {
        var seconds = 2;
        var ticks = (seconds * RateHz);

        long Measure(WorldStateRow row, Action<WorldFixture, int>? perTick) {
            using var fixture = Fixtures.FreshServer(definition: Document(row: row));

            var (observation, _) = Observe(fixture: fixture);

            fixture.Step();

            var work = new WorldProjectionWork();

            using (WorldProjectionWork.Attribute(work: work)) {
                for (var index = 0; (index < ticks); index++) {
                    perTick?.Invoke(arg1: fixture, arg2: index);
                    fixture.Step();
                }
            }

            observation.Dispose();

            // The wire carries each delta behind its leaf header.
            return (work.Read(kind: WorldProjectionWork.Bytes) + (work.Read(kind: WorldProjectionWork.Deltas) * WorldFederationCodec.ProjectionDeltaHeaderBytes));
        }

        var steady = Measure(perTick: null, row: Row(advance: PerTick(raw: 1024L), raw: 0L));
        // A busy sky: its clock sought ten times a second, a new phase each time.
        var busy = Measure(
            perTick: static (fixture, index) => {
                if ((index % 3) == 0) {
                    fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: Principal.Console, Row: Clock, Key: WorldStateRow.SlotKey.Value, Value: (index * 4099L) & 0xFFFFL, Kind: WorldDocumentWriteKind.Set));
                }
            },
            row: Row(raw: 0L)
        );
        // A nonlinear clock: an eased follower re-anchors at every tick it moves.
        var nonlinear = Measure(perTick: null, row: Row(dynamics: new StateDynamics(Row: "chase"), raw: FixedQ4816.FromDouble(value: 0.875d).Value) with {
            Cells = [new StateCell(
                Clock: new StateCellClock(EpochTick: 0L, Y0: 0L, V0: 0L),
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: 0.875d).Value)
            )],
        });
        var work = new WorldProjectionWork();

        using (WorldProjectionWork.Attribute(work: work)) {
            using var fixture = Fixtures.FreshServer(definition: Document(row: Row(raw: 0L)));

            var (observation, _) = Observe(fixture: fixture);

            observation.Dispose();
        }

        var hydration = (work.Read(kind: WorldProjectionWork.Bytes) + WorldFederationCodec.DocumentHeaderBytes);
        var courtyardProjection = Fixtures.Project(
            authority: "boot",
            definition: AuthoredGameFixtures.Load(relativePath: "src/Puck.World/Assets/worlds/moth-courtyard.puck"),
            revision: 1,
            tier: WorldDisclosureTier.Presentation
        )!;
        var courtyardWire = WorldProjection.SerializeCompact(projection: courtyardProjection);
        var courtyard = (courtyardWire.Length + WorldFederationCodec.DocumentHeaderBytes);
        var courtyardCanonical = WorldProjection.Serialize(projection: courtyardProjection).Length;
        var prototypes = Puck.Abstractions.Documents.CanonicalJsonDocument.SerializeCompact(node: WorldProjectionDelta.Tree(utf8Json: courtyardWire)["prototypes"]!).Length;

        output.WriteLine(message: $"steady sky: {(steady / seconds)} bytes per recipient per second");
        output.WriteLine(message: $"busy sky: {(busy / seconds)} bytes per recipient per second");
        output.WriteLine(message: $"nonlinear clock: {(nonlinear / seconds)} bytes per recipient per second");
        output.WriteLine(message: $"late-join hydration: {hydration} bytes (fixture world), {courtyard} bytes (courtyard; {prototypes} of them its prototypes; {courtyardCanonical} indented)");

        Assert.Equal(actual: steady, expected: 0L);
        Assert.InRange(actual: busy, high: (nonlinear - 1L), low: 1L);
        Assert.InRange(actual: nonlinear, high: (hydration * ticks), low: (ticks * 50L));
        // The wire carries the compact form: no indentation.
        Assert.True(condition: (courtyardWire.Length < courtyardCanonical));
        Assert.DoesNotContain(collection: courtyardWire, expected: ((byte)'\n'));
    }
}
