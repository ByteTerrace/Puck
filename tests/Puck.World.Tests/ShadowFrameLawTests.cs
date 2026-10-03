using System.Numerics;

using Puck.Assets.Documents;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>CPU laws for delivered-state shadow selection, frame plumbing and the named slot report.</summary>
public sealed class ShadowFrameLawTests {
    private static WorldRenderLight.Directional Light(string name, float weight, WorldShadowMode mode = WorldShadowMode.Auto) => new(
        Name: name,
        Color: new BindableColor(Raw: "#FFFFFF"),
        Weight: new BindableScalar(literal: weight),
        Shadow: mode
    );
    private static WorldDefinition Definition(int slots, params WorldRenderLight[] lights) => Fixtures.BuildDocument() with {
        RenderRaw = new WorldRenderDefaults(ShadowLights: slots, Lighting: new WorldRenderLighting(Lights: lights)),
        TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock(Name: "day", PeriodSeconds: 1d)]),
    };
    private static BindableScalar Weights(float start, float quarter, float half) => new(keys: new WorldKeyTrack<float>(clock: "day", keys: [
        new WorldKey<float>(At: 0d, Ease: WorldEase.Linear, Value: start),
        new WorldKey<float>(At: 0.25d, Ease: WorldEase.Linear, Value: quarter),
        new WorldKey<float>(At: 0.5d, Ease: WorldEase.Linear, Value: half),
    ]));
    private static WorldShadowSlot[] Slots(WorldEnvironmentResolve resolver, ulong tick) {
        var slots = new WorldShadowSlot[WorldShadowAllocator.MaxSlots];
        var readout = resolver.ShadowSlots.CopyTo(tick: new PresentedTick(Fraction: 0, Whole: tick), stable: slots, handoffs: new WorldShadowHandoff[2], queued: new WorldShadowQueued[4]);

        Assert.Equal(expected: 0, actual: readout.FadeCount);
        Assert.Equal(expected: 0, actual: readout.QueuedCount);

        return slots[..readout.StableCount];
    }

    [Fact]
    public void AFadingFrameCarriesEveryStableOwnerAndOnlyActiveIncomingMarches() {
        var definition = FadingDefinition();
        var mirror = ClientFixtures.StateMirror(definition: definition);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        _ = resolver.Resolve(definition: definition, revision: 0, mirror: mirror);
        var crossing = (EngineTicks.PerSecond / 4UL);

        mirror.Advance(engineTick: crossing, tick: 1);
        mirror.Advance(engineTick: (crossing + 8), tick: 2);
        mirror.Apply(fraction: 1f);
        for (var frameIndex = 0; (frameIndex < 8); frameIndex++) {
            var frame = resolver.Resolve(definition: definition, revision: 0, mirror: mirror);

            Assert.Equal(0, frame.Lights.ShadowSlots[0]);
            Assert.Equal(1, frame.Lights.ShadowSlots[1]);
            Assert.Equal(2, frame.Lights.ShadowSlots.SlotCount);
            Assert.Equal(1, frame.Lights.ShadowSlots.FadeCapacity);
            Assert.Equal(new SdfShadowHandoff(Incoming: 2, Outgoing: 0, Slot: 0, Weight: 0.5f),
                Assert.Single(collection: frame.Lights.ShadowSlots.Handoffs.ToArray()));
            for (var index = 0; (index < frame.Lights.Count); index++) {
                Assert.Equal(((index < 3) ? 1u : 0u), frame.Lights[index].Shadows);
            }
            var report = resolver.DescribeShadowSlots(definition: definition);

            Assert.Contains(actualString: report, expectedSubstring: "active=3 fades=1 queued=1");
            Assert.Contains(actualString: report, expectedSubstring: "shadow[0] light=a index=0 reason=auto rank=3");
            Assert.Contains(actualString: report, expectedSubstring: "fade[0] slot=0 outgoing=a index=0 incoming=b index=2");
            Assert.Contains(actualString: report, expectedSubstring: $"crossing={crossing} duration=16 weight=0.5 reason=selectionChanged");
            Assert.Contains(actualString: report, expectedSubstring: "queued[0] slot=1 incoming=d index=3 reason=FadeCapacity");
        }
        mirror.Advance(engineTick: (crossing + 16), tick: 3);
        mirror.Apply(fraction: 1f);
        Assert.Equal(2, resolver.Resolve(definition: definition, revision: 0, mirror: mirror).Lights.ShadowSlots[0]);
        Assert.Contains("slot=1 outgoing=c index=1 incoming=d index=3", resolver.DescribeShadowSlots(definition: definition));
        Assert.Contains("weight=0 reason=selectionChanged fromQueue", resolver.DescribeShadowSlots(definition: definition));
    }
    [Fact]
    public void ARetiredFadeRemainsInTheEarlierFrameIntervalAcrossANameReorder() {
        var definition = FadingDefinition();
        var mirror = ClientFixtures.StateMirror(definition: definition);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        _ = resolver.Resolve(definition: definition, revision: 0, mirror: mirror);
        var crossing = (EngineTicks.PerSecond / 4UL);

        mirror.Advance(engineTick: crossing, tick: 1);
        mirror.Advance(engineTick: (crossing + 16), tick: 2);
        mirror.Apply(fraction: 0.5f);
        var lights = definition.Render.Lighting!.Lights!;
        var reordered = definition with {
            RenderRaw = definition.Render with {
                Lighting = new WorldRenderLighting(Lights: [lights[2], lights[0], lights[3], lights[1]]),
            },
        };

        for (var frameIndex = 0; (frameIndex < 8); frameIndex++) {
            var frame = resolver.Resolve(definition: reordered, revision: 1, mirror: mirror);

            Assert.Equal(1, frame.Lights.ShadowSlots[0]);
            var report = resolver.DescribeShadowSlots(definition: reordered);

            Assert.Contains(actualString: report, expectedSubstring: "active=3 fades=1 queued=1");
            Assert.Contains(actualString: report, expectedSubstring: "slot=0 outgoing=a index=1 incoming=b index=0");
            Assert.Contains(actualString: report, expectedSubstring: "duration=16 weight=0.5");
            Assert.Contains(actualString: report, expectedSubstring: "incoming=d index=2 reason=FadeCapacity");
        }
    }

    private static WorldDefinition FadingDefinition() {
        var definition = Definition(2,
            Light("a", 0f) with { Weight = Weights(half: 1f, quarter: 1f, start: 4f) },
            Light("c", 0f) with { Weight = Weights(half: 0f, quarter: 0f, start: 3f) },
            Light("b", 0f) with { Weight = Weights(half: 4f, quarter: 4f, start: 0f) },
            Light("d", 0f) with { Weight = Weights(half: 3f, quarter: 3f, start: 0f) });

        return definition with {
            RenderRaw = definition.Render with {
                ShadowFadeSlots = 1,
                ShadowFadeTicks = 16,
                ShadowOverflow = WorldShadowOverflow.Queue,
            },
        };
    }

    [Fact]
    public void KeyedWeightSelectsAtDeliveryWhileTheFrameStillPresentsThePreviousTick() {
        var definition = Definition(1,
            Light("rising", 0f) with { Weight = Weights(half: 2f, quarter: 1f, start: 0f) },
            Light("steady", 1f));
        var mirror = ClientFixtures.StateMirror(definition: definition);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        Assert.Equal(expected: 1, actual: resolver.Resolve(definition: definition, revision: 0, mirror: mirror).Lights.ShadowSlots[0]);
        mirror.Apply(fraction: 0f);
        mirror.Advance(engineTick: (EngineTicks.PerSecond / 2UL), tick: 1UL);

        Assert.Equal(expected: "rising", actual: Assert.Single(collection: Slots(resolver: resolver, tick: (EngineTicks.PerSecond / 2UL))).Candidate.Name);
        mirror.Apply(fraction: 0.25f);
        Assert.Equal(expected: 1, actual: resolver.Resolve(definition: definition, revision: 0, mirror: mirror).Lights.ShadowSlots[0]);
        mirror.Apply(fraction: 1f);
        Assert.Equal(expected: 0, actual: resolver.Resolve(definition: definition, revision: 0, mirror: mirror).Lights.ShadowSlots[0]);
    }
    [Fact]
    public void KeyedColorSelectsAtDeliveryWithoutReadingThePresentedColor() {
        var color = new BindableColor(keys: new WorldKeyTrack<BindableColor>(clock: "day", keys: [
            new WorldKey<BindableColor>(At: 0d, Value: new BindableColor(Raw: "#000000"), Ease: WorldEase.Linear),
            new WorldKey<BindableColor>(At: 0.5d, Value: new BindableColor(Raw: "#FFFFFF"), Ease: WorldEase.Linear),
        ]));
        var definition = Definition(1, Light("brightening", 1f) with { Color = color }, Light("steady", 0.5f));
        var mirror = ClientFixtures.StateMirror(definition: definition);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        _ = resolver.Resolve(definition: definition, revision: 0, mirror: mirror);
        mirror.Apply(fraction: 0f);
        mirror.Advance(engineTick: (EngineTicks.PerSecond / 2UL), tick: 1UL);

        Assert.Equal(expected: new Vector4(w: 1f, x: 0f, y: 0f, z: 0f), actual: mirror.Color(color: color, fallback: Vector4.One));
        Assert.Equal(expected: Vector4.One, actual: mirror.Color(color: color, fallback: Vector4.Zero, delivered: true));
        Assert.Equal(expected: "brightening", actual: Assert.Single(collection: Slots(resolver: resolver, tick: (EngineTicks.PerSecond / 2UL))).Candidate.Name);
        mirror.Apply(fraction: 1f);
        Assert.Equal(expected: 0, actual: resolver.Resolve(definition: definition, revision: 0, mirror: mirror).Lights.ShadowSlots[0]);
    }
    [Fact]
    public void BoundColorAndAdvancingWeightUseCurrentDeliveredSamplesForLuminance() {
        var power = new WorldStateRow(
            Name: CellName.Parse(candidate: "power"),
            Kind: CellKind.Int,
            Min: 0,
            Max: 10,
            Advance: new StateAdvance(PerSecondDenominator: 1, PerSecondNumerator: 2),
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 0))]
        );
        var tint = new WorldStateRow(
            Name: CellName.Parse(candidate: "tint"),
            Kind: CellKind.Text,
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Text(value: "#FF0000"))]
        );
        var weight = new BindableScalar(binding: "state.power");
        var definition = Definition(1,
            Light("bound", 0f) with { Color = new BindableColor(Raw: "state.tint"), Weight = weight },
            Light("steady", 0.5f)).WithWorldState(rows: [power, tint]);
        var mirror = ClientFixtures.StateMirror(definition: definition);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        _ = resolver.Resolve(definition: definition, revision: 0, mirror: mirror);
        mirror.Apply(fraction: 0f);
        mirror.Advance(engineTick: EngineTicks.PerSecond, tick: 1UL);

        Assert.Equal(expected: "steady", actual: Assert.Single(collection: Slots(resolver: resolver, tick: EngineTicks.PerSecond)).Candidate.Name);
        mirror.Advance(engineTick: (2UL * EngineTicks.PerSecond), tick: 2UL);
        mirror.Apply(fraction: 0f);

        Assert.Equal(expected: 2f, actual: mirror.Scalar(scalar: weight, fallback: -1f));
        Assert.Equal(expected: 4f, actual: mirror.Scalar(delivered: true, fallback: -1f, scalar: weight));
        var selected = Assert.Single(collection: Slots(resolver: resolver, tick: (2UL * EngineTicks.PerSecond)));

        Assert.Equal(expected: "bound", actual: selected.Candidate.Name);
        Assert.Equal(expected: (4d * 0.2126d), actual: selected.Candidate.Luminance, precision: 12);
        mirror.Apply(fraction: 1f);
        Assert.Equal(expected: 0, actual: resolver.Resolve(definition: definition, revision: 0, mirror: mirror).Lights.ShadowSlots[0]);
    }
    [Fact]
    public void DeliveriesWithoutFramesPreserveTheSlotAcquiredAtTheIntermediateTick() {
        var definition = Definition(2,
            Light("first", 0f) with { Weight = Weights(half: 2f, quarter: 0f, start: 3f) },
            Light("second", 0f) with { Weight = Weights(half: 0f, quarter: 3f, start: 2f) },
            Light("third", 0f) with { Weight = Weights(half: 3f, quarter: 2f, start: 0f) });
        var mirror = ClientFixtures.StateMirror(definition: definition);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        _ = resolver.Resolve(definition: definition, revision: 0, mirror: mirror);
        mirror.Advance(engineTick: (EngineTicks.PerSecond / 4UL), tick: 1UL);
        mirror.Advance(engineTick: (EngineTicks.PerSecond / 2UL), tick: 2UL);
        mirror.Apply(fraction: 1f);
        var frame = resolver.Resolve(definition: definition, revision: 0, mirror: mirror);
        var selected = Slots(resolver: resolver, tick: (EngineTicks.PerSecond / 2UL));

        Assert.Equal(expected: ["third", "first"], actual: selected.Select(selector: slot => slot.Candidate.Name).ToArray());
        Assert.Equal(expected: [0, 1], actual: selected.Select(selector: slot => slot.Slot).ToArray());
        Assert.Equal(expected: 2, actual: frame.Lights.ShadowSlots[0]);
    }
    [Fact]
    public void ReorderingUpdatesEveryFrameSlotAndTheFullNamedReport() {
        var first = Light(mode: WorldShadowMode.Always, name: "first", weight: 1f);
        var second = Light(mode: WorldShadowMode.Always, name: "second", weight: 1f);
        var automatic = Light("automatic", 4f);
        var never = Light(mode: WorldShadowMode.Never, name: "never", weight: 100f);
        var ambient = new WorldRenderLight.Hemisphere();
        var definition = Definition(3, ambient, first, second, automatic, never);
        var mirror = ClientFixtures.StateMirror(definition: definition);
        var policy = new WorldShadowSettings(FadeSlots: 1, FadeTicks: 100UL, Overflow: WorldShadowOverflow.Queue, Slots: 3);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        Assert.Equal(expected: 1, actual: resolver.Resolve(definition: definition, revision: 0, mirror: mirror, shadows: policy).Lights.ShadowSlots[0]);
        var reordered = definition with {
            RenderRaw = definition.Render with { Lighting = new WorldRenderLighting(Lights: [second, automatic, never, ambient, first]) },
        };
        var frame = resolver.Resolve(definition: reordered, revision: 1, mirror: mirror, shadows: policy);
        var selected = Slots(resolver: resolver, tick: 0UL);

        Assert.Equal(expected: ["first", "second", "automatic"], actual: selected.Select(selector: slot => slot.Candidate.Name).ToArray());
        Assert.Equal(expected: [4, 0, 1], actual: selected.Select(selector: slot => slot.Candidate.LightIndex).ToArray());
        Assert.Equal(expected: 4, actual: frame.Lights.ShadowSlots[0]);
        Assert.Equal(expected: 0, actual: frame.Lights.ShadowSlots[1]);
        Assert.Equal(expected: 1, actual: frame.Lights.ShadowSlots[2]);
        for (var index = 0; (index < frame.Lights.Count); index++) {
            Assert.Equal(expected: ((index is 0 or 1 or 4) ? 1u : 0u), actual: frame.Lights[index].Shadows);
        }

        var report = resolver.DescribeShadowSlots(definition: reordered);

        Assert.NotNull(@object: report);
        Assert.Contains(actualString: report, expectedSubstring: "K=3 F=1 fadeTicks=100 overflow=queue");
        Assert.Contains(actualString: report, expectedSubstring: "active=3 fades=0 queued=0");
        Assert.Contains(actualString: report, expectedSubstring: "shadow[0] light=first index=4 reason=always");
        Assert.Contains(actualString: report, expectedSubstring: "shadow[1] light=second index=0 reason=always");
        Assert.Contains(actualString: report, expectedSubstring: "shadow[2] light=automatic index=1 reason=auto rank=3");
        Assert.Null(@object: resolver.DescribeShadowSlots(definition: definition));
    }
    [Fact]
    public void StateOnlyDefinitionReplacementChangesRanksWithoutResettingNamedSlots() {
        static WorldStateRow WeightsRow(long first, long second) => new(
            Name: CellName.Parse(candidate: "weights"),
            Kind: CellKind.Int,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "first"), Value: CellValue.Int(value: first)),
                new StateCell(Key: CellName.Parse(candidate: "second"), Value: CellValue.Int(value: second)),
            ]
        );

        var definition = Definition(2,
            Light("first", 0f) with { Weight = new BindableScalar(binding: "state.weights.first") },
            Light("second", 0f) with { Weight = new BindableScalar(binding: "state.weights.second") }
        ).WithWorldState(rows: [WeightsRow(first: 3, second: 2)]);
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));

        mirror.Install(tick: 0UL, engineTick: 0UL);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        Assert.Equal(expected: 0, actual: resolver.Resolve(definition: definition, revision: 0, mirror: mirror).Lights.ShadowSlots[0]);
        definition = definition.WithWorldState(rows: [WeightsRow(first: 2, second: 3)]);
        Assert.True(condition: definition.StateCatalog.TryResolve(handle: out var weights, lane: StateLane.Document, name: "weights"));
        mirror.Refresh(stamp: new WorldStateStamp(Tick: 1UL, EngineTick: 1UL, MovedRows: new[] { weights.Ordinal }, Everything: false));
        mirror.Apply(fraction: 1f);
        var frame = resolver.Resolve(definition: definition, revision: 0, mirror: mirror);
        var selected = Slots(resolver: resolver, tick: 1UL);

        Assert.Equal(expected: ["first", "second"], actual: selected.Select(selector: slot => slot.Candidate.Name).ToArray());
        Assert.Equal(expected: [0, 1], actual: selected.Select(selector: slot => slot.Candidate.LightIndex).ToArray());
        Assert.Equal(expected: [2, 1], actual: selected.Select(selector: slot => slot.Rank).ToArray());
        Assert.Equal(expected: 0, actual: frame.Lights.ShadowSlots[0]);
    }
    [Fact]
    public void BootShadowSelectionWaitsForTheSnapshotsFieldCellsAfterStateDelivery() {
        var weight = new BindableScalar(binding: "state.heat.0");
        var definition = Fixtures.WithLattice(
            definition: Definition(1, Light("field", 0f) with { Weight = weight }, Light("steady", 1f)),
            composite: new WorldFieldsSection(
                Lattice: new WorldFieldLatticeDefinition(
                    Origin: new DocumentVector3(value: Vector3.Zero),
                    CellSize: 1f,
                    Width: 1,
                    Depth: 1
                ),
                Fields: [new WorldFieldRow(Name: "heat", Max: 4f)]
            )
        );
        var client = ClientFixtures.Client(definition: definition);

        client.DeliverDefinition(definition: definition, version: default);
        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Tick: 0UL, EngineTick: 0UL, Revision: 0, StepTicks: 1UL, Entries: ReadOnlyMemory<EntitySnapshot>.Empty));
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        Assert.Equal(expected: 1, actual: resolver.Resolve(definition: definition, revision: 0, mirror: client.StateMirror).Lights.ShadowSlots[0]);
        client.DeliverState(
            definition: definition,
            version: default,
            stamp: new WorldStateStamp(Tick: 1UL, EngineTick: 1UL, MovedRows: ReadOnlyMemory<int>.Empty, Everything: true)
        );

        Assert.Equal(expected: 0f, actual: client.StateMirror.Scalar(delivered: true, fallback: -1f, scalar: weight));
        Assert.Equal(expected: "steady", actual: Assert.Single(collection: Slots(resolver: resolver, tick: 1UL)).Candidate.Name);
        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Tick: 1UL,
            EngineTick: 1UL,
            Revision: 0,
            StepTicks: 1UL,
            Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
            FieldCells: new[] { new FieldCellDelta(Cell: 0, Field: 0, Raw: Puck.Maths.FixedQ4816.FromDouble(value: 2d).Value) }
        ));

        Assert.Equal(expected: 2f, actual: client.StateMirror.Scalar(delivered: true, fallback: -1f, scalar: weight));
        Assert.Equal(expected: "field", actual: Assert.Single(collection: Slots(resolver: resolver, tick: 1UL)).Candidate.Name);
        client.StateMirror.Apply(fraction: 1f);
        Assert.Equal(expected: 0, actual: resolver.Resolve(definition: definition, revision: 0, mirror: client.StateMirror).Lights.ShadowSlots[0]);
    }
    [Fact]
    public void BootAndSameTickReloadInstallFieldSelectionOnceBeforeNormalAdvancesResume() {
        var definition = Fixtures.WithLattice(
            definition: Definition(1,
                Light("field", 0f) with { Weight = new BindableScalar(binding: "state.heat.0") },
                Light("steady", 1f)),
            composite: new WorldFieldsSection(
                Lattice: new WorldFieldLatticeDefinition(
                    Origin: new DocumentVector3(value: Vector3.Zero), CellSize: 1f, Width: 1, Depth: 1),
                Fields: [new WorldFieldRow(Name: "heat", Max: 4f)]
            )
        );
        var client = ClientFixtures.Client(definition: definition);

        client.DeliverDefinition(definition: definition, version: default);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        Assert.Equal(expected: 1, actual: resolver.Resolve(definition: definition, revision: 0, mirror: client.StateMirror).Lights.ShadowSlots[0]);

        void Snapshot(ulong tick, double weight) => client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Tick: tick, EngineTick: tick, Revision: 0, StepTicks: 1UL,
            Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
            FieldCells: new[] { new FieldCellDelta(Cell: 0, Field: 0, Raw: Puck.Maths.FixedQ4816.FromDouble(value: weight).Value) }
        ));

        Snapshot(tick: 0UL, weight: 2d);
        Assert.Equal(expected: "field", actual: Assert.Single(collection: Slots(resolver: resolver, tick: 0UL)).Candidate.Name);
        var bootInstalls = client.StateMirror.Installs;

        Snapshot(tick: 1UL, weight: 3d);
        Assert.Equal(expected: bootInstalls, actual: client.StateMirror.Installs);

        client.DeliverDefinition(definition: definition with { }, version: default);
        Assert.Equal(expected: "field", actual: Assert.Single(collection: Slots(resolver: resolver, tick: 1UL)).Candidate.Name);
        Snapshot(tick: 1UL, weight: 0d);
        Assert.Equal(expected: "steady", actual: Assert.Single(collection: Slots(resolver: resolver, tick: 1UL)).Candidate.Name);
        var reloadInstalls = client.StateMirror.Installs;

        Snapshot(tick: 2UL, weight: 2d);
        Assert.Equal(expected: reloadInstalls, actual: client.StateMirror.Installs);
        Assert.Equal(expected: "field", actual: Assert.Single(collection: Slots(resolver: resolver, tick: 2UL)).Candidate.Name);
    }
    [Fact]
    public void SunDiscFollowsPresentedSlotZeroPreservesExplicitLightAndFallsBackWithoutSlots() {
        var definition = Definition(1,
            Light("first", 0f) with { Weight = Weights(half: 0f, quarter: 0f, start: 2f) },
            Light("second", 0f) with { Weight = Weights(half: 3f, quarter: 3f, start: 0f) });

        definition = definition with {
            RenderRaw = definition.Render with {
                Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.SunDisc(Intensity: new BindableScalar(literal: 1f))]),
            },
        };
        var mirror = ClientFixtures.StateMirror(definition: definition);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());
        var initial = resolver.Resolve(definition: definition, revision: 0, mirror: mirror);

        Assert.Equal(expected: 0, actual: initial.Lights.ShadowSlots[0]);
        Assert.Equal(expected: 0, actual: initial.Sky.First<SdfSkyDisc>().Light);
        mirror.Advance(engineTick: (EngineTicks.PerSecond / 4UL), tick: 1UL);
        mirror.Apply(fraction: 1f);
        var crossed = resolver.Resolve(definition: definition, revision: 0, mirror: mirror);

        Assert.Equal(expected: 1, actual: crossed.Lights.ShadowSlots[0]);
        Assert.Equal(expected: 1, actual: crossed.Sky.First<SdfSkyDisc>().Light);
        Assert.Equal(expected: 1, actual: resolver.Resolve(definition: definition, revision: 0, mirror: mirror).Sky.First<SdfSkyDisc>().Light);

        var explicitDisc = definition with {
            RenderRaw = definition.Render with {
                Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.SunDisc(Light: 0, Intensity: new BindableScalar(literal: 1f))]),
            },
        };
        var explicitFrame = resolver.Resolve(definition: explicitDisc, revision: 1, mirror: mirror);

        Assert.Equal(expected: 1, actual: explicitFrame.Lights.ShadowSlots[0]);
        Assert.Equal(expected: 0, actual: explicitFrame.Sky.First<SdfSkyDisc>().Light);
        var disabled = resolver.Resolve(definition: definition, revision: 2, mirror: mirror,
            shadows: new WorldShadowSettings(FadeSlots: 0, FadeTicks: 0UL, Overflow: WorldShadowOverflow.Instant, Slots: 0));

        Assert.Equal(expected: -1, actual: disabled.Lights.ShadowSlots[0]);
        Assert.Equal(expected: 0, actual: disabled.Sky.First<SdfSkyDisc>().Light);
    }
    [Fact]
    public void ReconstructedSectionKeysKeepNamedSlotsAcrossAReorder() {
        static WorldRenderLighting Lighting(bool reordered) => new(
            Lights: (reordered ? [Light("second", 0f), Light("first", 0f)] : [Light("first", 0f), Light("second", 0f)]),
            Clock: "day",
            Keys: [
                new WorldRenderLightingKey(At: 0d, Lights: new Dictionary<string, WorldRenderLight> {
                    ["first"] = new WorldRenderLight.Directional(Weight: new BindableScalar(literal: 2f)),
                    ["second"] = new WorldRenderLight.Directional(Weight: new BindableScalar(literal: 1f)),
                }),
                new WorldRenderLightingKey(At: 0.25d, Lights: new Dictionary<string, WorldRenderLight> {
                    ["first"] = new WorldRenderLight.Directional(Weight: new BindableScalar(literal: 1f)),
                    ["second"] = new WorldRenderLight.Directional(Weight: new BindableScalar(literal: 3f)),
                }),
            ]
        );
        var definition = Definition(2);

        definition = definition with { RenderRaw = definition.Render with { Lighting = Lighting(reordered: false) } };
        var mirror = ClientFixtures.StateMirror(definition: definition);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        _ = resolver.Resolve(definition: definition, revision: 0, mirror: mirror);
        mirror.Advance(engineTick: (EngineTicks.PerSecond / 4UL), tick: 1UL);
        mirror.Apply(fraction: 1f);
        _ = resolver.Resolve(definition: definition, revision: 0, mirror: mirror);
        var reordered = definition with { RenderRaw = definition.Render with { Lighting = Lighting(reordered: true) } };
        var frame = resolver.Resolve(definition: reordered, revision: 1, mirror: mirror);

        Assert.Equal(expected: 1, actual: frame.Lights.ShadowSlots[0]);
        var slots = Slots(resolver: resolver, tick: (EngineTicks.PerSecond / 4UL));

        Assert.Equal(expected: new[] { "first", "second" }, actual: slots.Select(selector: slot => slot.Candidate.Name));
        Assert.Equal(expected: new[] { 2, 1 }, actual: slots.Select(selector: slot => slot.Rank));
    }
    [Fact]
    public void DuplicateUnnamedLightsDoNotDisguiseAReplacementAsAReorder() {
        var definition = Definition(1, new WorldRenderLight.Hemisphere(), new WorldRenderLight.Hemisphere(), Light("old", 1f));
        var mirror = ClientFixtures.StateMirror(definition: definition);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        Assert.Equal(expected: 2, actual: resolver.Resolve(definition: definition, revision: 0, mirror: mirror).Lights.ShadowSlots[0]);
        var replacement = Definition(1, new WorldRenderLight.Hemisphere(), Light("new", 2f), Light("old", 1f));
        var frame = resolver.Resolve(definition: replacement, revision: 1, mirror: mirror);

        Assert.Equal(expected: 1, actual: frame.Lights.ShadowSlots[0]);
        Assert.Equal(expected: "new", actual: Assert.Single(collection: Slots(resolver: resolver, tick: 0UL)).Candidate.Name);
    }
    [Fact]
    public void LivePolicyCanRemoveAndRestoreTheMarchWithoutMovingTheDefinitionOrTick() {
        var definition = Definition(1, Light("first", 2f), Light("second", 1f));
        var mirror = ClientFixtures.StateMirror(definition: definition);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        Assert.Equal(expected: 0, actual: resolver.Resolve(definition: definition, revision: 0, mirror: mirror).Lights.ShadowSlots[0]);
        var disabled = resolver.Resolve(definition: definition, revision: 0, mirror: mirror,
            shadows: new WorldShadowSettings(FadeSlots: 0, FadeTicks: 0UL, Overflow: WorldShadowOverflow.Instant, Slots: 0));

        Assert.Equal(expected: -1, actual: disabled.Lights.ShadowSlots[0]);
        Assert.Empty(collection: Slots(resolver: resolver, tick: 0UL));
        var restored = resolver.Resolve(definition: definition, revision: 0, mirror: mirror,
            shadows: new WorldShadowSettings(FadeSlots: 0, FadeTicks: 0UL, Overflow: WorldShadowOverflow.Instant, Slots: 2));

        Assert.Equal(expected: 0, actual: restored.Lights.ShadowSlots[0]);
        Assert.Equal(expected: 2, actual: Slots(resolver: resolver, tick: 0UL).Length);
    }
}
