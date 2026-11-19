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

/// <summary>Pins what the pose pass does with a seat's guess on a led driver (<see cref="CreationDriverDocument.Lead"/>):
/// the driver reads <c>lead</c> while a guess is in force for the wearing body and <c>cell</c> otherwise, a guess in force
/// is never held by a rate tier, and a guess for another body or none changes nothing. The guess is faked here, so the
/// laws leave unread when a guess is in force (<see cref="WorldSeatInput.TryLead"/>) and who is offered one
/// (<see cref="WorldClient.SeatGuess"/>).</summary>
public sealed class CreationLeadGuessLawTests {
    private const string Channel = "press";
    private const float Frame = (1f / 60f);

    private static readonly PresentationRate Paced60 = new(DisplayHertz: 60, GridHertz: 60d);

    // A seat's guess the law switches by hand: its channel level, and the body it is for.
    private sealed class FakeGuess(int body) : IWorldSeatGuess {
        public bool InForce { get; set; }

        public int Body { get; } = body;

        public bool TryLead(string channel, float leadSeconds, out float level) {
            level = (InForce ? 1f : 0f);

            return (InForce && (channel == Channel));
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

    private static (WorldClient Client, WorldCreationInstances Pool, DynamicTransform[] Transforms) Stage() {
        var creation = CreationFixtures.Prototype(id: "rig", document: CreationFixtures.Rig(
            drivers: [new CreationDriverDocument(Cadence: 1f, Lead: Channel, Name: "press", Signal: "state.press", When: [CreationDriverDocument.WhenAlways])],
            CreationFixtures.SwingingLimb(swing: new ShapeSwingDocument(Amplitude: 0.5f, Axis: Vector3.UnitX, Driver: "press", Pivot: Vector3.Zero, Wave: CreationWave.Linear))
        ));
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

        return (client, pool, new DynamicTransform[pool.DynamicSlotCount]);
    }
    // One frame of the pack path with the body at rest: the led driver's phase and what it read.
    private static (float Phase, string? Source) Present(WorldClient client, IWorldSeatGuess? guess, WorldCreationInstances pool, DynamicTransform[] transforms, ulong tick) {
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

        var moved = new SdfMovedTransforms();

        moved.Begin(everything: false, tableRows: transforms.Length);
        pool.PackTransforms(client: new GuessingSource(inner: client, guess: guess), moved: moved, parkPosition: new Vector3(x: 0f, y: -1000f, z: 0f), slotBase: 0, transforms: transforms);
        Assert.True(condition: pool.TryBodyRig(bodyIndex: 0, state: out var rig));

        return (rig!.Drivers[0].Phase, rig.Drivers[0].Source);
    }

    [Fact]
    public void AGuessInForceReadsLeadWithAPhaseAboveZeroAndTheCellOnceItEnds() {
        var (client, pool, transforms) = Stage();
        var guess = new FakeGuess(body: 0);

        Assert.Equal(expected: (0f, WorldRigDriver.SourceCell), actual: Present(client: client, guess: guess, pool: pool, tick: 1UL, transforms: transforms));
        guess.InForce = true;

        var (phase, source) = Present(client: client, guess: guess, pool: pool, tick: 2UL, transforms: transforms);

        Assert.Equal(expected: WorldRigDriver.SourceLead, actual: source);
        Assert.True(condition: (phase > 0f), userMessage: $"phase {phase}");
        guess.InForce = false;
        Assert.Equal(expected: (0f, WorldRigDriver.SourceCell), actual: Present(client: client, guess: guess, pool: pool, tick: 3UL, transforms: transforms));
    }
    // A refused press: the cell stays zero while the button is held, and a held button starts no new guess, so the finger
    // is back at the cell's zero from the frame the answer is presented to the end of the hold.
    [Fact]
    public void ARefusedPressReturnsToTheCellAndTheFingerStaysAtZeroForTheRestOfTheHold() {
        var (client, pool, transforms) = Stage();
        var guess = new FakeGuess(body: 0);
        var tick = 1UL;

        // The registration's first frame is a cut that advances no driver.
        _ = Present(client: client, guess: guess, pool: pool, tick: tick++, transforms: transforms);
        guess.InForce = true;

        for (var frame = 0; (frame < 3); frame++) {
            Assert.Equal(expected: WorldRigDriver.SourceLead, actual: Present(client: client, guess: guess, pool: pool, tick: tick++, transforms: transforms).Source);
        }

        guess.InForce = false;

        for (var frame = 0; (frame < 60); frame++) {
            Assert.Equal(expected: (0f, WorldRigDriver.SourceCell), actual: Present(client: client, guess: guess, pool: pool, tick: tick++, transforms: transforms));
        }
    }
    // The guess belongs to the body its seat controls: a guess for another body, and no guess at all (a session view, a
    // second client, a client with two seats), leave the driver on its cell throughout.
    [InlineData(1)]
    [InlineData(-1)]
    [Theory]
    public void AGuessForAnotherBodyOrNoGuessLeavesTheDriverOnItsCell(int body) {
        var (client, pool, transforms) = Stage();
        IWorldSeatGuess? guess = ((body < 0) ? null : new FakeGuess(body: body) { InForce = true });

        for (var tick = 1UL; (tick <= 6UL); tick++) {
            Assert.Equal(expected: (0f, WorldRigDriver.SourceCell), actual: Present(client: client, guess: guess, pool: pool, tick: tick, transforms: transforms));
        }
    }
    // A body at rest that a rate tier holds is evaluated in full in the frame its guess starts, where without a guess the
    // same frame is held.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ABodyAtRestUnderARateTierIsEvaluatedInTheFrameItsGuessStarts(bool guessed) {
        var (client, pool, transforms) = Stage();
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

        for (var tick = 1UL; (tick <= 3UL); tick++) {
            guess.InForce = (guessed && (tick == 3UL));
            _ = Present(client: client, guess: guess, pool: pool, tick: tick, transforms: transforms);
            posed.Add(item: counts.Read(kind: SdfUpdateRateCounts.BodiesPosed));
        }

        Assert.Equal(expected: 1L, actual: posed[0]);
        Assert.Equal(expected: 1L, actual: posed[1]);
        Assert.Equal(expected: (guessed ? 2L : 1L), actual: posed[2]);
    }
}
