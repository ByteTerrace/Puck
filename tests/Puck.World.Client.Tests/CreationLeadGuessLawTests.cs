using System.Numerics;

using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Physics.Motion;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>Pins where a seat's guess on a led driver (<see cref="CreationDriverDocument.Lead"/>) goes: into the stamp
/// pool's guess layer alone, whose rows only the seat's own view reads (<see cref="WorldCreationInstances.GuessOverlay"/>).
/// The shared pose every other reader resolves (anchors and cameras, carried screens, other bodies' effectors,
/// <c>body.rig</c>) reads its cell whatever the guess, a guess ends at its age on the presentation clock, and a view of the
/// shared pose cannot take a guess at all. The guess is faked here, so the laws leave unread when an edge is outstanding
/// (<see cref="WorldSeatInput.TryLead"/>) and who is offered one (<see cref="WorldClient.SeatGuessAt"/>).</summary>
public sealed class CreationLeadGuessLawTests {
    private const string Channel = "press";
    private const float Frame = (1f / 60f);
    private const float LeadSeconds = 0.21f;

    private static readonly PresentationRate Paced60 = new(DisplayHertz: 60, GridHertz: 60d);

    // A seat's guess the law switches by hand: whether an edge is outstanding, which edge it is, and the body it is for.
    private sealed class FakeGuess(int body) : IWorldSeatGuess {
        public bool InForce { get; set; }
        public ulong Edge { get; set; } = 1UL;

        public int Body { get; } = body;
        public int Slot => 0;

        public bool TryLead(string channel, out float level, out ulong edge) {
            var held = (InForce && (channel == Channel));

            level = (held ? 1f : 0f);
            edge = (held ? Edge : 0UL);

            return held;
        }
    }
    // The client the pool roots on, offering the fake as the presenting seat's guess; null offers none, as a session view or
    // a client with two seats does.
    private sealed class GuessingSource(WorldClient inner, IWorldSeatGuess? guess) : IWorldStampSource {
        public WorldDefinition Definition => inner.Definition;
        public WorldStateMirror StateMirror => inner.StateMirror;
        public SdfFieldEvaluator? StaticField => inner.StaticField;
        public IWorldSeatGuess? SeatGuess => guess;
        public WorldPoseCarries Carries => inner.Carries;
        public float? SolidScale(string placementId) => inner.SolidScale(placementId: placementId);
        public WorldEntityAddress EntityAddress(int index) => inner.EntityAddress(index: index);
        public BodyFacts Facts(int index) => inner.Facts(index: index);
        public bool IsActive(int index) => inner.IsActive(index: index);
        public WorldLook Look(int index) => inner.Look(index: index);
        public Quaternion Orientation(int index) => inner.Orientation(index: index);
        public string? PlacementId(int index) => inner.PlacementId(index: index);
        public int PoseEpoch(int index) => inner.PoseEpoch(index: index);
        public Vector3 Position(int index) => inner.Position(index: index);
        public bool TryInhabitantBody(string placementId, out int index) => inner.TryInhabitantBody(placementId: placementId, index: out index);
    }
    // One frame's reading: the shared pose's led driver, the guess layer's (none when the pool holds no layer), the
    // overlay, and the table the frame packed.
    private sealed record Reading((float Phase, string? Source) Shared, (float Phase, string? Source)? Guess, SdfPoseOverlay Overlay, DynamicTransform[] Table);

    private static WorldPrototype Creation() => CreationFixtures.Prototype(id: "rig", document: CreationFixtures.Rig(
        drivers: [new CreationDriverDocument(Cadence: 1f, Lead: Channel, LeadSeconds: LeadSeconds, Name: "press", Signal: "state.press", When: [CreationDriverDocument.WhenAlways])],
        CreationFixtures.SwingingLimb(swing: new ShapeSwingDocument(Amplitude: 0.5f, Axis: Vector3.UnitX, Driver: "press", Pivot: Vector3.Zero, Wave: CreationWave.Linear))
    ));
    private static (WorldClient Client, WorldCreationInstances Pool) Stage() {
        var creation = Creation();
        var definition = (CreationFixtures.RigWorld(creation: creation.Document, state: [StateFixtures.IntSlot(name: "press")]) with {
            LookRowsRaw = [new WorldLook(
                Name: "rig",
                Source: new WorldLookSource.Creation(PrototypeId: creation.Id),
                Scale: 1f,
                Motion: WorldLookMotion.Default
            )],
        });
        var client = ClientFixtures.Client(definition: definition);
        var pool = new WorldCreationInstances();

        pool.Reconcile(placementFrames: WorldPlacementFrameCompilation.Compile(placements: [], worldSeed: 0UL),
            bodyStamps: [new WorldCreationInstances.BodyStamp(BodyIndex: 0, Creation: creation, Scale: 1f, Look: WorldLook.Implicit)],
            creations: [creation],
            dynamics: [],
            placements: []
        );

        return (client, pool);
    }
    // One frame of the prepare and pack path with the body at rest.
    private static Reading Present(WorldClient client, IWorldSeatGuess? guess, WorldCreationInstances pool, ulong tick) {
        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Authority: "a",
            Entries: new[] { new EntitySnapshot(
                Active: true,
                BodyColor: Vector3.One,
                CatalogRig: 0,
                Continuity: EntityContinuity.Continuous,
                Generation: 1,
                Index: 0,
                Kit: 0,
                Look: 0,
                Orientation: Quaternion.Identity,
                Position: Vector3.Zero
            ) },
            Revision: 0,
            StepTicks: 1UL,
            Tick: tick
        ), stream: default);
        client.UpdateRenderPoses(alpha: 1f);
        pool.Tick(deltaSeconds: Frame);

        var source = new GuessingSource(inner: client, guess: guess);

        pool.PrepareGuess(source: source);

        // The table persists between frames, as a frame source's does, and only a new table is owed whole: a pack whose
        // every row is owed holds no pose.
        var held = Tables.GetOrCreateValue(key: pool);
        var fresh = (held.Rows.Length != pool.DynamicSlotCount);

        if (fresh) { held.Rows = new DynamicTransform[pool.DynamicSlotCount]; }
        var transforms = held.Rows;
        var moved = new SdfMovedTransforms();

        moved.Begin(everything: fresh, tableRows: transforms.Length);
        pool.PackTransforms(client: source, moved: moved, parkPosition: new Vector3(x: 0f, y: -1000f, z: 0f), slotBase: 0, transforms: transforms);
        Assert.True(condition: pool.TryBodyRig(bodyIndex: 0, state: out var rig));

        return new Reading(
            Guess: (pool.TryGuessRig(bodyIndex: 0, slot: out _, state: out var guessed) ? (guessed!.Drivers[0].Phase, guessed.Drivers[0].Source) : null),
            Overlay: pool.GuessOverlay,
            Shared: (rig!.Drivers[0].Phase, rig.Drivers[0].Source),
            Table: [.. transforms]
        );
    }
    private sealed class HeldTable {
        public DynamicTransform[] Rows = [];
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldCreationInstances, HeldTable> Tables = new();

    [Fact]
    public void AGuessIsDrawnByItsLayerAloneWhileTheSharedPoseReadsTheCell() {
        var (client, pool) = Stage();
        var guess = new FakeGuess(body: 0);
        var tick = 1UL;

        // The registrations' first frames are cuts that advance no driver.
        _ = Present(client: client, guess: guess, pool: pool, tick: tick++);
        var before = Present(client: client, guess: guess, pool: pool, tick: tick++);

        guess.InForce = true;
        var during = Present(client: client, guess: guess, pool: pool, tick: tick++);

        Assert.Equal(expected: (0f, WorldRigDriver.SourceCell), actual: during.Shared);
        Assert.Equal(expected: WorldRigDriver.SourceLead, actual: during.Guess!.Value.Source);
        Assert.True(condition: (during.Guess.Value.Phase > 0f), userMessage: $"phase {during.Guess.Value.Phase}");
        // The overlay reads the body's own slots from rows past every row the pool and its program name.
        var overlay = during.Overlay;

        Assert.Equal(expected: 0, actual: overlay.FirstSlot);
        Assert.Equal(expected: (pool.ArticulatedSlotCount + pool.Rigid.SlotCount), actual: overlay.OverlaySlot);
        Assert.True(condition: (overlay.SlotCount > 1));
        // The shared rows are what they were before the guess, bit for bit; only the layer's rows move.
        Assert.Equal(expected: before.Table.AsSpan(start: 0, length: overlay.OverlaySlot).ToArray(), actual: during.Table.AsSpan(start: 0, length: overlay.OverlaySlot).ToArray());
        Assert.NotEqual(expected: during.Table.AsSpan(start: overlay.FirstSlot, length: overlay.SlotCount).ToArray(), actual: during.Table.AsSpan(start: overlay.OverlaySlot, length: overlay.SlotCount).ToArray());
        // A body part an anchor or a part-anchored camera resolves reads the shared rows.
        Assert.True(condition: pool.TryBodyTransformSlot(bodyIndex: 0, slotCount: out var slots, transformSlot: out var root));
        Assert.Equal(expected: (overlay.FirstSlot, overlay.SlotCount), actual: (root, slots));

        guess.InForce = false;
        var after = Present(client: client, guess: guess, pool: pool, tick: tick++);

        Assert.Equal(expected: (0f, WorldRigDriver.SourceCell), actual: after.Guess!.Value);
        Assert.Equal(expected: after.Table.AsSpan(start: overlay.FirstSlot, length: overlay.SlotCount).ToArray(), actual: after.Table.AsSpan(start: overlay.OverlaySlot, length: overlay.SlotCount).ToArray());
    }
    // A refused press: the cell stays zero while the button is held, and a held button starts no new guess, so the layer's
    // finger is back at the cell's zero from the frame the answer is presented to the end of the hold.
    [Fact]
    public void ARefusedPressReturnsToTheCellAndTheFingerStaysAtZeroForTheRestOfTheHold() {
        var (client, pool) = Stage();
        var guess = new FakeGuess(body: 0);
        var tick = 1UL;

        _ = Present(client: client, guess: guess, pool: pool, tick: tick++);
        _ = Present(client: client, guess: guess, pool: pool, tick: tick++);
        guess.InForce = true;

        for (var frame = 0; (frame < 3); frame++) {
            Assert.Equal(expected: WorldRigDriver.SourceLead, actual: Present(client: client, guess: guess, pool: pool, tick: tick++).Guess!.Value.Source);
        }

        guess.InForce = false;

        for (var frame = 0; (frame < 60); frame++) {
            var reading = Present(client: client, guess: guess, pool: pool, tick: tick++);

            Assert.Equal(expected: (0f, WorldRigDriver.SourceCell), actual: reading.Guess!.Value);
            Assert.Equal(expected: (0f, WorldRigDriver.SourceCell), actual: reading.Shared);
        }
    }
    // An edge no answer reaches shows for its lead seconds of presented time and no longer; a new edge starts its own age.
    [Fact]
    public void AnUnansweredEdgeEndsAtItsLeadSecondsOnThePresentationClock() {
        var (client, pool) = Stage();
        var guess = new FakeGuess(body: 0);
        var tick = 1UL;

        _ = Present(client: client, guess: guess, pool: pool, tick: tick++);
        _ = Present(client: client, guess: guess, pool: pool, tick: tick++);
        guess.InForce = true;

        // Twelve frames past the first are 0.2 s, inside 0.21; the thirteenth is past it.
        for (var frame = 0; (frame <= 12); frame++) {
            Assert.Equal(expected: WorldRigDriver.SourceLead, actual: Present(client: client, guess: guess, pool: pool, tick: tick++).Guess!.Value.Source);
        }

        Assert.Equal(expected: WorldRigDriver.SourceCell, actual: Present(client: client, guess: guess, pool: pool, tick: tick++).Guess!.Value.Source);
        guess.Edge = 2UL;
        Assert.Equal(expected: WorldRigDriver.SourceLead, actual: Present(client: client, guess: guess, pool: pool, tick: tick++).Guess!.Value.Source);
    }
    // The guess belongs to the body its seat controls: a guess for another body, and no guess at all (a session view, a
    // second client, a client with two seats), make no layer and no overlay, and leave the table as it was.
    [InlineData(1)]
    [InlineData(-1)]
    [Theory]
    public void AGuessForAnotherBodyOrNoGuessMakesNoLayer(int body) {
        var (client, pool) = Stage();
        var rows = pool.DynamicSlotCount;
        IWorldSeatGuess? guess = ((body < 0) ? null : new FakeGuess(body: body) { InForce = true });

        for (var tick = 1UL; (tick <= 6UL); tick++) {
            var reading = Present(client: client, guess: guess, pool: pool, tick: tick);

            Assert.Equal(expected: (0f, WorldRigDriver.SourceCell), actual: reading.Shared);
            Assert.Null(@object: reading.Guess);
            Assert.Equal(expected: default(SdfPoseOverlay), actual: reading.Overlay);
            Assert.Equal(expected: rows, actual: reading.Table.Length);
        }
    }
    // The shared pose's view answers no lead and refuses a guess outright, so no reader of a pool registration can be
    // handed one; only a guess layer's view takes it.
    [Fact]
    public void AViewOfTheSharedPoseRefusesAGuess() {
        var rig = Creation().Rig;
        var lead = Assert.Single(collection: rig.Reads, predicate: static read => (read.Kind == CreationRigReadKind.Lead));
        var shared = new WorldRigReads(lease: new WorldStateLease(), rig: rig);

        shared.Bind(body: 0, client: null);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => shared.BindGuess(clockSeconds: 0f, guess: new FakeGuess(body: 0) { InForce = true }));
        Assert.False(condition: shared.TryLead(level: out _, read: lead, seconds: LeadSeconds));
        Assert.False(condition: shared.AnswersLeads);

        var layer = WorldRigReads.ForGuess(lease: new WorldStateLease(), rig: rig);

        layer.Bind(body: 0, client: null);
        layer.BindGuess(clockSeconds: 0f, guess: new FakeGuess(body: 0) { InForce = true });
        Assert.True(condition: layer.TryLead(level: out var level, read: lead, seconds: LeadSeconds));
        Assert.Equal(expected: 1f, actual: level);
    }
    // A body at rest that a rate tier holds: the shared pose holds as it would without a guess, and the guess layer still
    // poses the press in that frame.
    [Fact]
    public void ARateTierHoldsTheSharedPoseAndTheLayerStillPosesTheGuess() {
        var (client, pool) = Stage();
        var counts = SdfUpdateRateCounts.Counters();
        var guess = new FakeGuess(body: 0);
        var view = new SdfViewSnapshot(
            Camera: CameraSnapshot.LookAt(fieldOfViewRadians: (MathF.PI / 3f), position: new Vector3(x: 0f, y: 0.5f, z: -65f), target: new Vector3(x: 0f, y: 0.5f, z: 0f), viewportHeight: 900f, viewportWidth: 1280f),
            Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
        );

        pool.PresentAt(rate: Paced60, rates: SdfUpdateRates.All);
        pool.RateBy(counts: counts, height: 900U, intervals: null, views: [view]);

        // The 15 Hz tier packs the first frame and every fourth after it, so frame 3 is a held one.
        var posed = new List<long>();
        Reading? last = null;

        for (var tick = 1UL; (tick <= 3UL); tick++) {
            guess.InForce = (tick >= 2UL);
            last = Present(client: client, guess: guess, pool: pool, tick: tick);
            posed.Add(item: counts.Read(kind: SdfUpdateRateCounts.BodiesPosed));
        }

        Assert.Equal(expected: new long[] { 1L, 1L, 1L }, actual: posed.ToArray());
        Assert.Equal(expected: WorldRigDriver.SourceLead, actual: last!.Guess!.Value.Source);
    }
}
