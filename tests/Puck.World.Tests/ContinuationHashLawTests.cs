using Puck.Maths;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The authoritative hash folds every body's simulation continuation from views of the live slots
/// (<see cref="WorldPopulation.AppendContinuationHash"/>), and the views fold exactly the bytes the checkpoint's own
/// entries encode (<see cref="WorldAuthorityCheckpointCodec.AppendPopulationEntries"/> over
/// <see cref="WorldPopulation.Capture"/>): over a carried body sweeping against a wall behind a walking carrier, and
/// over a rigid ball that comes to rest, tick by tick. A steady fold allocates nothing.
/// </summary>
public sealed class ContinuationHashLawTests {
    private static void AssertStreamedFoldsTheCapturedEntries(WorldPopulation population, string context) {
        var streamed = Fnv1aHash.Create();
        var captured = Fnv1aHash.Create();

        population.AppendContinuationHash(hash: ref streamed);
        WorldAuthorityCheckpointCodec.AppendPopulationEntries(
            entries: population.Capture().Entries,
            hash: ref captured
        );

        Assert.True(
            condition: (streamed.Value == captured.Value),
            userMessage: $"{context}: the streamed continuation {streamed.Value:X16} differs from the captured entries' {captured.Value:X16}"
        );
    }

    [Fact]
    public void ACarriedBodySweepingAgainstAWallFoldsTheBytesItsCheckpointEntryEncodes() {
        using var fixture = Fixtures.FreshServer(definition: WorldCarryTangibilityLawTests.WallCarryDocument(
            includeOtherBody: true,
            includeWall: true
        ));

        _ = fixture.JoinSeat();
        Assert.True(
            condition: fixture.Server.Population.TryBeginCarry(
                carrierIndex: WorldCarryTangibilityLawTests.CarrierIndex,
                reason: out var reason,
                targetIndex: WorldCarryTangibilityLawTests.BallIndex
            ),
            userMessage: reason
        );

        var carrier = fixture.Server.Body(index: WorldCarryTangibilityLawTests.CarrierIndex)!;
        var ball = fixture.Server.Body(index: WorldCarryTangibilityLawTests.BallIndex)!;

        for (var step = 0; (step < 60); step++) {
            carrier.Pose(
                pitchRadians: 0f,
                rollRadians: 0f,
                x: 0f,
                y: 0f,
                yawRadians: 0f,
                z: (-0.02f * step)
            );
            fixture.Step();
            Assert.Equal(expected: WorldCarryTangibilityLawTests.CarrierIndex, actual: ball.CarriedBy);
            AssertStreamedFoldsTheCapturedEntries(context: $"carry step {step}", population: fixture.Server.Population);
        }

        // The wall held the carried ball back: its sweep ran against the wall rather than following the carrier.
        Assert.True(condition: (ball.FixedPosition.Z > FixedQ4816.FromDouble(value: -1.5d)));
    }
    [Fact]
    public void ARigidBallComingToRestFoldsTheBytesItsCheckpointEntryEncodes() {
        var definition = AuthoredGameFixtures.Load(relativePath: "tests/Puck.World.Tests/Fixtures/minimal-paddleball-host.world.json");
        using var fixture = Fixtures.FreshServer(definition: definition);
        var ordinal = definition.Placements.ToList().FindIndex(match: static row => (row.Id == "paddleballBall"));
        var ball = fixture.Server.Body(index: fixture.Server.Population.BodyForPlacementOrdinal(ordinal: ordinal))!;
        var moved = false;

        for (var tick = 0; (tick < 240); tick++) {
            fixture.Step();
            moved |= !ball.Resting;
            AssertStreamedFoldsTheCapturedEntries(context: $"paddleball tick {tick}", population: fixture.Server.Population);
        }

        Assert.True(condition: moved, userMessage: "the ball never moved, so no tick folded a body in flight");
        Assert.True(condition: ball.Resting);
    }
    [Fact]
    public void ASteadyFoldAllocatesNothing() {
        using var fixture = Fixtures.FreshServer(definition: WorldCarryTangibilityLawTests.WallCarryDocument(
            includeOtherBody: true,
            includeWall: true
        ));

        _ = fixture.JoinSeat();
        Assert.True(
            condition: fixture.Server.Population.TryBeginCarry(
                carrierIndex: WorldCarryTangibilityLawTests.CarrierIndex,
                reason: out var reason,
                targetIndex: WorldCarryTangibilityLawTests.BallIndex
            ),
            userMessage: reason
        );

        var population = fixture.Server.Population;
        var warm = Fnv1aHash.Create();

        fixture.Step();
        population.AppendContinuationHash(hash: ref warm);

        var allocated = 0L;

        for (var tick = 0; (tick < 30); tick++) {
            fixture.Step();

            var hash = Fnv1aHash.Create();
            var before = GC.GetAllocatedBytesForCurrentThread();

            population.AppendContinuationHash(hash: ref hash);
            allocated += (GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(actual: allocated, expected: 0L);
    }
}
