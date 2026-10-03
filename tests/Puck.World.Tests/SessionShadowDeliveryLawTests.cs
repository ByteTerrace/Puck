using Puck.Abstractions.Counting;
using Puck.Assets.Documents;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Session snapshots reduce shadow ownership at complete delivery, independently of frame reads.</summary>
public sealed class SessionShadowDeliveryLawTests {
    private static WorldStateRow Weights(long first, long second, long third) => new(
        Name: CellName.Parse(candidate: "weights"), Kind: CellKind.Int, Cells: [
            new StateCell(Key: CellName.Parse(candidate: "first"), Value: CellValue.Int(value: first)),
            new StateCell(Key: CellName.Parse(candidate: "second"), Value: CellValue.Int(value: second)),
            new StateCell(Key: CellName.Parse(candidate: "third"), Value: CellValue.Int(value: third)),
        ]);
    private static WorldRenderLight.Directional Light(string name, string binding) => new(
        Name: name, Shadow: WorldShadowMode.Auto, Weight: new BindableScalar(binding: binding));
    private static WorldDefinition Definition() => (Fixtures.BuildDocument() with {
        RenderRaw = new WorldRenderDefaults(ShadowLights: 2, Lighting: new WorldRenderLighting(Lights: [
            Light(binding: "state.weights.first", name: "first"), Light(binding: "state.weights.second", name: "second"), Light(binding: "state.weights.third", name: "third"),
        ])),
    }).WithWorldState([Weights(first: 3, second: 2, third: 1)]);
    private static WorldShadowSelection Selection(WorldDefinition definition) {
        var selection = new WorldShadowSelection();

        selection.SetSettings(settings: WorldShadowSettings.From(render: definition.Render));
        return selection;
    }
    private static WorldSnapshot Snapshot(ulong tick, FieldCellDelta[]? fields = null) => new(
        Tick: tick, EngineTick: tick, Revision: 0, StepTicks: 1,
        Entries: ReadOnlyMemory<EntitySnapshot>.Empty, FieldCells: (fields ?? []));
    private static void Deliver(WorldSessionMirror session, WorldDefinition definition, ulong tick, FieldCellDelta[]? fields = null) {
        session.DeliverState(definition, default, new WorldStateStamp(EngineTick: tick, Everything: true, MovedRows: default, Tick: tick));
        session.DeliverSnapshot(snapshot: Snapshot(fields: fields, tick: tick));
    }
    private static string[] Names(WorldShadowSelection selection, ulong tick, double fraction = 0) {
        var slots = new WorldShadowSlot[WorldShadowAllocator.MaxSlots];
        var count = selection.CopyTo(new PresentedTick(Fraction: fraction, Whole: tick), slots, new WorldShadowHandoff[2], new WorldShadowQueued[4]);

        Assert.Equal(0, count.FadeCount);
        Assert.Equal(0, count.QueuedCount);
        return slots[..count.StableCount].Select(selector: slot => slot.Candidate.Name).ToArray();
    }
    private static long Reads(IWorkCounterSource source) {
        Assert.True(condition: source.TryRead(kind: WorldStateMirror.Reads, value: out var reads));
        return reads;
    }
    private static WorldDefinition FieldDefinition() {
        var definition = Fixtures.WithLattice(Fixtures.BuildDocument(), new WorldFieldsSection(
            Lattice: new WorldFieldLatticeDefinition(Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: 1, Depth: 1),
            Fields: [new WorldFieldRow(Name: "power", Max: 4f)]));

        return definition with {
            RenderRaw = new WorldRenderDefaults(ShadowLights: 1, Lighting: new WorldRenderLighting(Lights: [
                Light(binding: "state.power.0", name: "field"),
                new WorldRenderLight.Directional(Name: "steady", Shadow: WorldShadowMode.Auto, Weight: new BindableScalar(literal: 1f)),
            ])),
        };
    }

    [Fact]
    public void BootAndReloadFieldCompletionInstallWithoutFadingWhileLiveSnapshotsFade() {
        var definition = FieldDefinition();

        definition = definition with {
            RenderRaw = definition.Render with {
                ShadowFadeSlots = 1,
                ShadowFadeTicks = 8,
                ShadowOverflow = WorldShadowOverflow.Queue,
            },
        };
        var session = new WorldSessionMirror(placeholder: definition);
        var selection = Selection(definition: definition);
        using var observer = session.ObserveDeliveredState(observer: selection.Advance);

        static FieldCellDelta[] Power(double power) => [new FieldCellDelta(Cell: 0, Field: 0, Raw: Puck.Maths.FixedQ4816.FromDouble(value: power).Value)];
        session.DeliverDefinition(definition: definition, version: default);
        Deliver(session, definition, 0, Power(power: 2));
        Assert.Equal(new[] { "field" }, Names(selection, 0));
        Deliver(session, definition, 1, Power(power: 0));
        var stable = new WorldShadowSlot[4];
        var handoffs = new WorldShadowHandoff[2];
        var queued = new WorldShadowQueued[4];
        var result = selection.CopyTo(new PresentedTick(Fraction: 0.5, Whole: 1), stable, handoffs, queued);

        Assert.Equal(new WorldShadowReadout(FadeCount: 1, QueuedCount: 0, StableCount: 1), result);
        Assert.Equal("field", stable[0].Candidate.Name);
        Assert.Equal("steady", handoffs[0].Incoming.Name);
        Assert.Equal(0.0625, handoffs[0].Weight);
        session.DeliverDefinition(definition: definition with { }, version: default);
        session.DeliverSnapshot(snapshot: Snapshot(1, Power(power: 3)));
        Assert.Equal(new[] { "field" }, Names(selection, 1));
        Assert.Equal(new[] { "field" }, Names(selection, 0));
    }
    [Fact]
    public void CompletedSessionDeliveriesPreserveIntermediateOwnershipWithoutFrames() {
        var definition = Definition();
        var sparse = new WorldSessionMirror(placeholder: definition);
        var frequent = new WorldSessionMirror(placeholder: definition);
        var sparseSelection = Selection(definition: definition);
        var frequentSelection = Selection(definition: definition);
        using var sparseObserver = sparse.ObserveDeliveredState(observer: sparseSelection.Advance);
        using var frequentObserver = frequent.ObserveDeliveredState(observer: frequentSelection.Advance);

        foreach (var (tick, first, second, third) in new (ulong, long, long, long)[] {
            (10, 3, 2, 1), (11, 1, 3, 4), (12, 3, 1, 4),
        }) {
            var delivered = definition.WithWorldState([Weights(first: first, second: second, third: third)]);

            Deliver(sparse, delivered, tick);
            Deliver(frequent, delivered, tick);
            frequent.FollowState().Apply(fraction: 1f);
            _ = Names(frequentSelection, tick);
        }

        Assert.Equal(new[] { "third", "first" }, Names(sparseSelection, 12));
        Assert.Equal(Names(frequentSelection, 12), Names(sparseSelection, 12));
        Assert.Equal(new[] { "third", "second" }, Names(fraction: 0.5, selection: sparseSelection, tick: 11));
    }
    [Fact]
    public void SnapshotFieldCellsReachSelectionAfterTheSameTicksStateDelivery() {
        var definition = FieldDefinition();
        var session = new WorldSessionMirror(placeholder: definition);
        var selection = Selection(definition: definition);
        var callbacks = 0;
        using var observer = session.ObserveDeliveredState(observer: (document, revision, mirror) => {
            callbacks++;
            selection.Advance(definition: document, mirror: mirror, revision: revision);
        });

        Deliver(session, definition, 10);
        Assert.Equal(new[] { "steady" }, Names(selection, 10));
        var before = callbacks;

        session.DeliverState(definition, default, new WorldStateStamp(EngineTick: 11, Everything: true, MovedRows: default, Tick: 11));
        Assert.Equal(actual: callbacks, expected: before);
        session.DeliverSnapshot(snapshot: Snapshot(11, [new FieldCellDelta(Cell: 0, Field: 0, Raw: (2L * 65536L))]));

        Assert.Equal(actual: callbacks, expected: (before + 1));
        Assert.Equal(new[] { "field" }, Names(selection, 11));
    }
    [Fact]
    public void ObserverSamplesStaySeparateAndDisposalEndsTheirCountedReads() {
        var definition = Definition();
        var session = new WorldSessionMirror(placeholder: definition);

        Deliver(session, definition, 10);
        var presentation = session.FollowState();
        WorldStateMirror? observed = null;
        var callbacks = 0;
        var observer = session.ObserveDeliveredState(observer: (_, _, mirror) => {
            observed = mirror;
            callbacks++;
        });

        Assert.NotSame(actual: observed, expected: presentation);
        Deliver(session, definition.WithWorldState([Weights(first: 1, second: 3, third: 4)]), 11);
        Assert.Equal(10UL, presentation.EngineTick);
        Assert.Equal(11UL, observed!.EngineTick);
        var before = callbacks;

        observer.Dispose();
        var reads = Reads(source: observer.Work);

        Deliver(session, definition.WithWorldState([Weights(first: 3, second: 1, third: 4)]), 12);

        Assert.Equal(actual: callbacks, expected: before);
        Assert.Equal(reads, Reads(source: observer.Work));
        Assert.Equal(12UL, session.FollowState().EngineTick);
    }
    [Fact]
    public void ALateObserverSeedsFromAlreadyDeliveredFieldStorage() {
        var definition = FieldDefinition();
        var session = new WorldSessionMirror(placeholder: definition);

        Deliver(session, definition, 10, [new FieldCellDelta(Cell: 0, Field: 0, Raw: (2L * 65536L))]);
        var selection = Selection(definition: definition);
        using var observer = session.ObserveDeliveredState(observer: selection.Advance);

        Assert.Equal(new[] { "field" }, Names(selection, 10));
        Assert.True(condition: (Reads(source: observer.Work) > 0));
    }
    [Fact]
    public void StructuralReplacementInstallsWhilePausedAndCompletesSameTickFields() {
        var replacement = FieldDefinition();
        var original = replacement with {
            RenderRaw = replacement.Render with {
                Lighting = new WorldRenderLighting(Lights: [
                    new WorldRenderLight.Directional(Name: "retired", Shadow: WorldShadowMode.Always),
                ]),
            },
        };
        var session = new WorldSessionMirror(placeholder: original);
        var selection = Selection(definition: original);
        using var observer = session.ObserveDeliveredState(observer: selection.Advance);

        Deliver(session, original, 10);
        Assert.Equal(new[] { "retired" }, Names(selection, 10));

        session.DeliverDefinition(definition: replacement, version: default);

        Assert.Equal(new[] { "steady" }, Names(selection, 10));
        Assert.Equal(new[] { "steady" }, Names(selection, 9));
        session.DeliverSnapshot(snapshot: Snapshot(10, [new FieldCellDelta(Cell: 0, Field: 0, Raw: (2L * 65536L))]));
        Assert.Equal(new[] { "field" }, Names(selection, 10));
    }
    [Fact]
    public void AReorderKeepsHeldSlotsThroughSameTickSnapshotCompletion() {
        var definition = Definition();
        var session = new WorldSessionMirror(placeholder: definition);
        var selection = Selection(definition: definition);
        using var observer = session.ObserveDeliveredState(observer: selection.Advance);

        Deliver(session, definition, 10);
        definition = definition.WithWorldState([Weights(first: 2, second: 3, third: 1)]);
        Deliver(session, definition, 11);
        var reordered = definition with {
            RenderRaw = definition.Render with {
                Lighting = new WorldRenderLighting(Lights: [
                    Light(binding: "state.weights.second", name: "second"), Light(binding: "state.weights.first", name: "first"), Light(binding: "state.weights.third", name: "third"),
                ]),
            },
        };

        session.DeliverDefinition(definition: reordered, version: default);
        Assert.Equal(new[] { "first", "second" }, Names(selection, 11));
        session.DeliverSnapshot(snapshot: Snapshot(11));
        Assert.Equal(new[] { "first", "second" }, Names(selection, 11));

        Deliver(session, reordered.WithWorldState([Weights(first: 3, second: 2, third: 1)]), 12);

        Assert.Equal(new[] { "first", "second" }, Names(selection, 12));
    }
    [Fact]
    public void CoalescedFrameReadsReportTheSelectionsActualDeliveredInterval() {
        var definition = Definition();
        var session = new WorldSessionMirror(placeholder: definition);
        var selection = Selection(definition: definition);
        using var observer = session.ObserveDeliveredState(observer: selection.Advance);

        Deliver(session, definition, 10);
        var presentation = session.FollowState();

        presentation.Apply(fraction: 1f);
        Deliver(session, definition.WithWorldState([Weights(first: 1, second: 3, third: 4)]), 11);
        var delivered = definition.WithWorldState([Weights(first: 3, second: 1, third: 4)]);

        Deliver(session, delivered, 12);
        presentation = session.FollowState();
        presentation.Apply(fraction: 0.5f);
        Assert.Equal(new PresentedTick(Fraction: 0, Whole: 11), presentation.Presented);

        var slots = new WorldShadowSlot[WorldShadowAllocator.MaxSlots];
        var count = selection.CopyPresented(presentation.PresentationFraction, slots, [], [], out var tick, out var settings);

        Assert.Equal(new PresentedTick(Fraction: 0.5, Whole: 11), tick);
        Assert.Equal(WorldShadowSettings.From(render: delivered.Render), settings);
        Assert.Equal(new[] { "third", "second" }, slots[..count.StableCount].Select(selector: slot => slot.Candidate.Name));
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());
        var frame = resolver.Resolve(delivered, session.DefinitionRevision, presentation, shadowSelection: selection);

        Assert.Equal(2, frame.Lights.ShadowSlots[0]);
        var report = resolver.DescribeShadowSlots(definition: delivered);

        Assert.Contains(actualString: report, expectedSubstring: "shadowSlots tick=11+0.5");
        Assert.Contains(actualString: report, expectedSubstring: "shadow[1] light=second");

        presentation.Apply(fraction: 1f);
        count = selection.CopyPresented(presentation.PresentationFraction, slots, [], [], out tick, out settings);
        Assert.Equal(new PresentedTick(Fraction: 0, Whole: 12), tick);
        Assert.Equal(new[] { "third", "first" }, slots[..count.StableCount].Select(selector: slot => slot.Candidate.Name));
        _ = resolver.Resolve(delivered, session.DefinitionRevision, presentation, shadowSelection: selection);
        report = resolver.DescribeShadowSlots(definition: delivered);
        Assert.Contains(actualString: report, expectedSubstring: "shadowSlots tick=12+0");
        Assert.Contains(actualString: report, expectedSubstring: "shadow[1] light=first");
    }
}
