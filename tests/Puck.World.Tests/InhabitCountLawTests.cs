using System.Numerics;
using Puck.World.Authoring;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>An inhabit facet's <c>count</c> may name a live Int cell instead of an authored literal: the population
/// admits and retires bodies to track the cell's value, bounded by the world's peer capacity and the placement's
/// own distribution sample count (the tighter of the two), and never retires a body a seat currently drives. A
/// cell-driven placement starts with zero live bodies through every structural install, including boot — only
/// <see cref="Server.WorldPopulation.ReconcileInhabitCounts"/> grows or shrinks it, and only when the cell's raw
/// value has actually moved since the last call.</summary>
public sealed class InhabitCountLawTests {
    private const string CountRow = "courtSize";
    private const string CourtCreation = "court-npc";
    private const string CourtPlacementId = "court";
    private const int DistributionSampleCount = 4;
    private const int ExtraPeerSlots = 6;
    private static readonly Vector3 CourtCenter = new(x: 5f, y: 0f, z: 5f);
    private static readonly WorldSequence Fill = new(Name: WorldSequence.Additive, Offset: 0, Step: 0.618034f);

    private static WorldPrototype Creation() {
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: CourtCreation,
            Palette: null,
            Shapes: [
                new ShapeDocument(
                    Id: 0,
                    Name: null,
                    Type: SdfSolidPrimitive.Sphere,
                    Position: Vector3.Zero,
                    Rotation: Quaternion.Identity,
                    Scale: new Vector3(value: 1f),
                    Material: 0,
                    Blend: SdfBlendOp.Union,
                    Smooth: 0f,
                    Group: 0
                ),
            ],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: CourtCreation);

        return new WorldPrototype(Id: CourtCreation, Document: canonical.Document, HashRaw: canonical.Hash);
    }
    // A court whose inhabit count reads the "courtSize" Int slot cell (initial value initial), fanned over a
    // radius-2 Disc distribution sampled at DistributionSampleCount — a bound tighter than the authored peer
    // capacity (ExtraPeerSlots), so a law can drive the cell past either bound independently.
    private static WorldDefinition Document(int initial) {
        var document = Fixtures.BuildDocument();

        return (document with {
            CreationsRaw = [Creation()],
            PlacementRowsRaw = [
                new WorldPlacement(
                    Id: CourtPlacementId,
                    PrototypeId: CourtCreation,
                    Position: new Puck.Assets.Documents.DocumentVector3(value: CourtCenter),
                    YawDegrees: 0f,
                    Scale: 1f,
                    Inhabit: new WorldPlacementInhabit(
                        Kit: Fixtures.SeatKitName,
                        Look: null,
                        Source: IntentSource.Idle,
                        Count: new WorldPlacementInhabitCount(Row: CountRow),
                        Distribution: new WorldDistribution(
                            Region: new WorldDistributionRegion.Disc(Radius: 2f, SampleCount: DistributionSampleCount),
                            Fill: Fill
                        )
                    )
                ),
            ],
            PopulationRaw = (document.Population with {
                CapacityRaw = (WorldBodiesLimits.LocalSeatCount + ExtraPeerSlots),
                // Zero disables the park-with-grace window: the seat-driven-inhabitant law needs a disconnect to
                // tear the slot down immediately so re-admitting it lands cleanly, never merely parked.
                ReconnectGraceSeconds = 0f,
            }),
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(Name: CellName.Parse(candidate: CountRow), Kind: CellKind.Int, Min: 0, Max: 100, Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: initial)]),
            ]),
        });
    }
    // The exact fixed-point offset InhabitantSpawn (WorldPopulation.Step.cs) materializes for one ordinal of a Disc
    // region — reproduced here (not exposed by the production type) so a law can assert an admitted body landed
    // exactly where the region's own deterministic fill sequence says it must, never merely "somewhere new". The
    // region's SampleCount is authored (DistributionSampleCount), so it governs the fan regardless of how many
    // bodies are actually live — ordinal k's offset never moves as the live count grows or shrinks.
    private static FixedVector3 ExpectedOffset(int ordinal) {
        var radius = FixedQ4816.FromDouble(value: 2.0);
        var fraction = (FixedQ4816.FromInteger(value: ((2L * ordinal) + 1L)) / FixedQ4816.FromInteger(value: (2L * DistributionSampleCount)));
        var angle = WorldSequenceSampling.FixedAngle(sequence: Fill, index: ordinal);
        var r = (radius * FixedQ4816.Sqrt(value: fraction));
        var (sin, cos) = FixedQ4816.SinCos(angle: angle);
        var center = FixedVector3.FromVector3(value: CourtCenter);

        return new FixedVector3(X: (center.X + (r * cos)), Y: center.Y, Z: (center.Z + (r * sin)));
    }
    // Drives WorldPopulation.ReconcileInhabitCounts directly against a COPY of the fixture's own document whose
    // count cell reads `value` — the server's own live definition and mutation pipeline are never touched, so this
    // exercises the reconcile primitive in isolation (no rule frame, no tick advance, no unrelated allocation) —
    // exactly the shape a rule-frame fold's end-of-tick install hands it in the live game.
    private static WorldDefinition WithCellValue(WorldFixture fixture, int value) => (fixture.Server.Definition with {
        StateRaw = (fixture.Server.Definition.StateRaw! with {
            World = [
                new WorldStateRow(Name: CellName.Parse(candidate: CountRow), Kind: CellKind.Int, Min: 0, Max: 100, Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: value)]),
            ],
        }),
    });
    private static void Reconcile(WorldFixture fixture, int cellValue, List<WorldPeerEventEntry>? admitted = null, List<WorldPeerEventEntry>? disconnected = null) =>
        fixture.Server.Population.ReconcileInhabitCounts(definition: WithCellValue(fixture: fixture, value: cellValue), tick: fixture.Server.NextInputTick, admitted: admitted, disconnected: disconnected);
    // The court's own inhabitants, in ADMISSION order (highest body index first — see HighestFreeSlot's own
    // remarks: the first body admitted claims the highest free slot, so admission order is descending index order).
    private static int[] Inhabitants(WorldFixture fixture) {
        var population = fixture.Server.Population;
        var found = new List<int>();

        for (var index = (population.Capacity - 1); (index >= population.LocalSeatCount); index--) {
            if (string.Equals(a: population.InhabitantPlacementId(index: index), b: CourtPlacementId, comparisonType: StringComparison.Ordinal)) {
                found.Add(item: index);
            }
        }

        return [.. found];
    }
    // Compares the PLANAR offset only (X/Z) — what the distribution's fan actually places — never Y: a freshly
    // admitted body's first advanced tick settles its exact vertical rest by the ordinary motion program, a fact
    // about the kit's contact resolution, not the region's fill sequence this law is proving.
    private static void AssertPlanarOffset(FixedVector3 expected, FixedVector3 actual) {
        Assert.Equal(expected: expected.X, actual: actual.X);
        Assert.Equal(expected: expected.Z, actual: actual.Z);
    }

    [Fact]
    public void ANewWorldAdmitsACellDrivenPlacementFromItsCellAtBoot() {
        using var fixture = Fixtures.FreshServer(definition: Document(initial: 3));

        // The structural install (ReconcileInhabitants) skips a cell-driven facet; the boot's own ReconcileInhabitCounts
        // then resolves it from the cell's authored value, so the world never waits for a first write to fill it.
        Assert.Equal(expected: 3, actual: Inhabitants(fixture: fixture).Length);
    }
    [Fact]
    public void RaisingTheCellAdmitsBodiesAtTheDistributionsNextOffsets() {
        using var fixture = Fixtures.FreshServer(definition: Document(initial: 0));

        Reconcile(fixture: fixture, cellValue: 2);

        var afterTwo = Inhabitants(fixture: fixture);

        Assert.Equal(expected: 2, actual: afterTwo.Length);
        AssertPlanarOffset(expected: ExpectedOffset(ordinal: 0), actual: fixture.Server.Population.EntryBody(index: afterTwo[0])!.FixedPosition);
        AssertPlanarOffset(expected: ExpectedOffset(ordinal: 1), actual: fixture.Server.Population.EntryBody(index: afterTwo[1])!.FixedPosition);

        // Raising again: the two existing inhabitants keep the position their own ordinal already resolved to, and
        // the newly admitted third lands at the region's next ordinal (2) — never a re-fan across the two survivors.
        Reconcile(fixture: fixture, cellValue: 3);

        var afterThree = Inhabitants(fixture: fixture);

        Assert.Equal(expected: 3, actual: afterThree.Length);
        AssertPlanarOffset(expected: ExpectedOffset(ordinal: 0), actual: fixture.Server.Population.EntryBody(index: afterThree[0])!.FixedPosition);
        AssertPlanarOffset(expected: ExpectedOffset(ordinal: 1), actual: fixture.Server.Population.EntryBody(index: afterThree[1])!.FixedPosition);
        AssertPlanarOffset(expected: ExpectedOffset(ordinal: 2), actual: fixture.Server.Population.EntryBody(index: afterThree[2])!.FixedPosition);
    }
    [Fact]
    public void LoweringTheCellRetiresTheLastAdmittedFirst() {
        using var fixture = Fixtures.FreshServer(definition: Document(initial: 3));

        Reconcile(fixture: fixture, cellValue: 3);

        var before = Inhabitants(fixture: fixture);

        Assert.Equal(expected: 3, actual: before.Length);

        Reconcile(fixture: fixture, cellValue: 2);

        var after = Inhabitants(fixture: fixture);

        // The last-admitted inhabitant is the lowest index (HighestFreeSlot claims downward, so the first admission
        // sits at the highest index and each later one at a lower one) — before[2] (lowest of the three) retires;
        // the two earlier (higher-index) inhabitants stand untouched.
        Assert.Equal(expected: 2, actual: after.Length);
        Assert.Equal(expected: before[0], actual: after[0]);
        Assert.Equal(expected: before[1], actual: after[1]);
        Assert.False(condition: fixture.Server.Population.IsActive(index: before[2]));
    }
    [Fact]
    public void ASeatDrivenBodyIsNeverRetired() {
        using var fixture = Fixtures.FreshServer(definition: Document(initial: 3));

        Reconcile(fixture: fixture, cellValue: 3);

        var inhabitants = Inhabitants(fixture: fixture);
        var lastAdmitted = inhabitants[2];
        var population = fixture.Server.Population;

        // Marks the last-admitted inhabitant as a connected remote human's own body: a hard disconnect (grace 0,
        // authored above) fully tears the slot down, and re-admitting it as IntentSource.Live at the same index sets
        // Entry.IsRemoteHuman — the same field IsHumanOccupied reads for a live federated seat's own claimed body —
        // while restoring its inhabited PlacementId, so the population still counts it toward the court's census.
        Assert.True(condition: population.TryCaptureTransferredEntity(index: lastAdmitted, peer: out var captured));

        population.ApplyPeerDisconnected(peer: captured, tick: fixture.Server.NextInputTick);

        Assert.False(condition: population.IsActive(index: lastAdmitted));

        population.ApplyPeerAdmitted(peer: (captured with { Source = IntentSource.Live, PlacementId = CourtPlacementId }), grantTemplates: []);

        Assert.True(condition: population.IsHumanOccupied(bodyIndex: lastAdmitted));
        Assert.Equal(expected: CourtPlacementId, actual: population.InhabitantPlacementId(index: lastAdmitted));

        // Asking for zero: every non-occupied inhabitant retires, but the seat-driven one survives — the shrink
        // stops short of the resolved count (0) rather than evicting a live occupant.
        Reconcile(fixture: fixture, cellValue: 0);

        var remaining = Inhabitants(fixture: fixture);

        Assert.Equal(expected: (int[])[lastAdmitted], actual: remaining);
        Assert.True(condition: population.IsActive(index: lastAdmitted));
    }
    [Fact]
    public void TheClampEchoesOnTheWorldPlacementChannel() {
        using var fixture = Fixtures.FreshServer(definition: Document(initial: 0));
        var sink = new RecordingNarrationSink();

        using var lease = fixture.Server.AttachNarrationSink(sink: sink);

        // Above both the peer-capacity ceiling (ExtraPeerSlots = 6) and the distribution's own SampleCount (4) —
        // the tighter of the two (4) governs, and the narration names it.
        Reconcile(fixture: fixture, cellValue: 10);

        Assert.Equal(expected: DistributionSampleCount, actual: Inhabitants(fixture: fixture).Length);
        Assert.Contains(collection: sink.Narrations, filter: narration =>
            (narration.Channel == "world.placement") &&
            narration.Text.Contains(value: $"count {DistributionSampleCount} of {CountRow}", comparisonType: StringComparison.Ordinal) &&
            narration.Text.Contains(value: $"clamped by {DistributionSampleCount}", comparisonType: StringComparison.Ordinal)
        );
    }
    [Fact]
    public void AnUnchangedCellAllocatesNothingOnTheQuietSweep() {
        using var fixture = Fixtures.FreshServer(definition: Document(initial: 2));
        // Built once and reused across every call below: Reconcile's own WithCellValue constructs a fresh document
        // per call (the test harness's own cost, not the reconcile primitive's), so isolating the measured
        // allocation to ReconcileInhabitCounts itself means calling it directly against one unchanging reference.
        var definition = WithCellValue(fixture: fixture, value: 2);

        // Warms the memo (first call always resolves and, here, admits) and lets the JIT settle before measuring.
        fixture.Server.Population.ReconcileInhabitCounts(definition: definition, tick: fixture.Server.NextInputTick);
        fixture.Server.Population.ReconcileInhabitCounts(definition: definition, tick: fixture.Server.NextInputTick);

        var before = GC.GetAllocatedBytesForCurrentThread();

        fixture.Server.Population.ReconcileInhabitCounts(definition: definition, tick: fixture.Server.NextInputTick);

        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(expected: before, actual: after);
    }
}
