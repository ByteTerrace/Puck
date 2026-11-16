using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>THE LAW: a whole-image read of a session mirror (<see cref="WorldSessionMirror.CopySnapshotTo"/>,
/// <see cref="WorldSessionMirror.TryInput"/>, <see cref="WorldSessionMirror.SnapshotArrivalIntervals"/>) never sees a
/// torn image and never waits on a writer. A delivery or route seed stalled inside its own critical section leaves the
/// read free to take the last complete image, and a copy racing both writers always reads one image a writer
/// finished.</summary>
public sealed class SessionMirrorImageLawTests {
    private const int Bodies = 8;
    private const int SeedIndex = 20;
    private const int Capacity = WorldBodiesLimits.CapacityCeiling;

    // Every body of one snapshot carries the same value in its position, color and generation, so an image mixing two
    // deliveries shows two values.
    private static WorldSnapshot Snapshot(int value) {
        var entries = new EntitySnapshot[Bodies];

        for (var index = 0; (index < Bodies); index++) {
            entries[index] = new EntitySnapshot(
                Active: true,
                BodyColor: new Vector3(value: value),
                CatalogRig: 0,
                Continuity: default,
                Generation: value,
                Index: index,
                Kit: 0,
                Look: 0,
                Orientation: Quaternion.Identity,
                Position: new Vector3(x: value, y: 0f, z: 0f)
            );
        }
        return new WorldSnapshot(Authority: "a", Entries: entries, Revision: value, StepTicks: 1UL, Tick: ((ulong)value));
    }
    private static WorldAuthorityRouteDescription Route(int value) => new(
        BodyColor: Vector3.One,
        CatalogRig: 0,
        Definition: WorldProjection.Undisclosed,
        Endpoint: "127.0.0.1:1",
        Entity: new WorldEntityAddress(Authority: "a", Generation: value, Index: SeedIndex),
        Kit: 0,
        Look: 0,
        Orientation: FixedQuaternion.Identity,
        PlacementId: null,
        Position: new FixedVector3(X: FixedQ4816.FromDouble(value: value), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
        Tick: ((ulong)value),
        TravelTurn: FixedQ4816.Zero,
        Version: default
    );

    private sealed class Copy {
        public readonly bool[] Active = new bool[Capacity];
        public readonly WorldEntityAddress[] Addresses = new WorldEntityAddress[Capacity];
        public readonly int[] Epochs = new int[Capacity];
        public readonly string?[] Placements = new string?[Capacity];
        public readonly Vector3[] PreviousPositions = new Vector3[Capacity];
        public readonly Quaternion[] PreviousOrientations = new Quaternion[Capacity];
        public readonly Vector3[] Positions = new Vector3[Capacity];
        public readonly Quaternion[] Orientations = new Quaternion[Capacity];
        public readonly Vector3[] Colors = new Vector3[Capacity];
        public readonly WorldLook[] Looks = new WorldLook[Capacity];
        public readonly byte[] Rigs = new byte[Capacity];
        public readonly FixedWorldCollider?[] Colliders = new FixedWorldCollider?[Capacity];
        public readonly WorldBodyContactMode[] Contacts = new WorldBodyContactMode[Capacity];
        public ulong Tick;
        public int Revision;

        public void From(WorldSessionMirror mirror) => mirror.CopySnapshotTo(
            active: Active,
            addresses: Addresses,
            arrivalTimestamp: out _,
            bodyContacts: Contacts,
            catalogRigs: Rigs,
            colliders: Colliders,
            colors: Colors,
            currentOrientations: Orientations,
            currentPositions: Positions,
            looks: Looks,
            placementIds: Placements,
            poseEpochs: Epochs,
            previousOrientations: PreviousOrientations,
            previousPositions: PreviousPositions,
            revision: out Revision,
            stepSeconds: out _,
            tick: out Tick
        );
        // The value every body of the copied snapshot shares, or a failure naming the first body that disagrees.
        public int CommonValue() {
            var value = ((int)Positions[0].X);

            for (var index = 0; (index < Bodies); index++) {
                Assert.True(condition: Active[index], userMessage: $"body {index} inactive");
                Assert.Equal(expected: value, actual: ((int)Positions[index].X));
                Assert.Equal(expected: value, actual: ((int)Colors[index].X));
                Assert.Equal(expected: value, actual: Addresses[index].Generation);
            }
            return value;
        }
    }

    // A copy that does not finish within the bound waited on a writer.
    private static Task CopyWithin(Copy copy, WorldSessionMirror mirror, CancellationToken token) =>
        Task.Run(action: () => copy.From(mirror: mirror), cancellationToken: token).WaitAsync(cancellationToken: token, timeout: TimeSpan.FromSeconds(seconds: 30));

    [Fact]
    public async Task ACopyNeverWaitsOnAWriterStalledInsideItsCriticalSection() {
        var token = TestContext.Current.CancellationToken;
        var mirror = new WorldSessionMirror(placeholder: WorldProjection.Undisclosed);
        var copy = new Copy();
        using var stalled = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var stall = false;

        mirror.DeliverSnapshot(snapshot: Snapshot(value: 1));
        // A completed-delivery observer runs inside the writer's gate; this one holds the writer there until released.
        using var observation = mirror.ObserveDeliveredState(observer: (_, _, _) => {
            if (Volatile.Read(location: ref stall)) {
                stalled.Set();
                release.Wait(cancellationToken: token);
            }
        });

        Volatile.Write(location: ref stall, value: true);
        try {
            // A snapshot delivery stalled in its tail: its image is complete before the tail, so the copy reads it.
            var delivery = Task.Run(action: () => mirror.DeliverSnapshot(snapshot: Snapshot(value: 2)), cancellationToken: token);

            Assert.True(condition: stalled.Wait(cancellationToken: token, timeout: TimeSpan.FromSeconds(seconds: 30)));
            await CopyWithin(copy: copy, mirror: mirror, token: token);
            Assert.Equal(expected: 2, actual: copy.CommonValue());
            // The inputs and the arrival history come from the same image, so they never wait on the stalled tail either.
            var (input, intervals) = await Task.Run(function: () => (
                mirror.TryInput(owner: Principal.Seat(slot: 0), publication: out _),
                mirror.SnapshotArrivalIntervals(milliseconds: new double[4])
            ), cancellationToken: token).WaitAsync(cancellationToken: token, timeout: TimeSpan.FromSeconds(seconds: 30));

            Assert.False(condition: input);
            Assert.Equal(expected: 1, actual: intervals);
            release.Set();
            await delivery;

            // A route seed stalled before its image is published: the copy reads the last complete snapshot.
            stalled.Reset();
            release.Reset();
            var seed = Task.Run(action: () => mirror.SeedRoute(route: Route(value: 3)), cancellationToken: token);

            Assert.True(condition: stalled.Wait(cancellationToken: token, timeout: TimeSpan.FromSeconds(seconds: 30)));
            await CopyWithin(copy: copy, mirror: mirror, token: token);
            Assert.Equal(expected: 2, actual: copy.CommonValue());
            Assert.False(condition: copy.Active[SeedIndex]);
            Volatile.Write(location: ref stall, value: false);
            release.Set();
            await seed;
        } finally {
            // A failed assertion still lets a stalled writer, and any copy queued behind it, finish.
            Volatile.Write(location: ref stall, value: false);
            release.Set();
        }

        copy.From(mirror: mirror);
        Assert.True(condition: copy.Active[SeedIndex]);
        Assert.Equal(expected: 3f, actual: copy.Positions[SeedIndex].X);
        Assert.Equal(expected: 3UL, actual: copy.Tick);
    }
    [Fact]
    public async Task ACopyRacingBothWritersAlwaysReadsOneFinishedImage() {
        var token = TestContext.Current.CancellationToken;
        const int Rounds = 2000;
        var mirror = new WorldSessionMirror(placeholder: WorldProjection.Undisclosed);
        var copy = new Copy();
        var done = 0;

        mirror.DeliverSnapshot(snapshot: Snapshot(value: 1));
        var snapshots = Task.Run(action: () => {
            for (var value = 2; (value <= Rounds); value++) { mirror.DeliverSnapshot(snapshot: Snapshot(value: value)); }
        }, cancellationToken: token);
        var seeds = Task.Run(action: () => {
            for (var value = 1; (value <= Rounds); value++) { mirror.SeedRoute(route: Route(value: value)); }
        }, cancellationToken: token);

        _ = Task.WhenAll(snapshots, seeds).ContinueWith(continuationAction: _ => Volatile.Write(location: ref done, value: 1), scheduler: TaskScheduler.Default);

        var last = 0;
        var copies = 0;

        while ((Volatile.Read(location: ref done) == 0) || (copies == 0)) {
            copy.From(mirror: mirror);
            var value = copy.CommonValue();

            // Images only move forward for the reader.
            Assert.True(condition: (value >= last), userMessage: $"image went back from {last} to {value}");
            last = value;
            copies++;
        }
        await Task.WhenAll(snapshots, seeds);
        copy.From(mirror: mirror);
        Assert.Equal(expected: Rounds, actual: copy.CommonValue());
    }
}
