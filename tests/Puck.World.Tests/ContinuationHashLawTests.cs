using Puck.Abstractions.Counting;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The authoritative hash folds every body's simulation continuation from views of the live slots
/// (<see cref="WorldPopulation.AppendContinuationHash"/>), and the views fold exactly the bytes the checkpoint's own
/// entries encode (<see cref="WorldAuthorityCheckpointCodec.AppendPopulationEntries"/> over
/// <see cref="WorldPopulation.Capture"/>): over a carried body sweeping against a wall behind a walking carrier, and
/// over a rigid ball that comes to rest, tick by tick, and over a seat driven by an owned identity carrying facts and
/// records. A steady fold allocates nothing, a profiled seat's included.
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class ContinuationHashLawTests {
    // Returns the streamed fold after asserting it equals the captured entries' fold, and the fold of the same entries
    // with every profile's records and facts copied into new instances, which the projection wire has never encoded:
    // an encoding remembered from an earlier write cannot stand in for the one these values write.
    private static ulong AssertStreamedFoldsTheCapturedEntries(WorldPopulation population, string context) {
        var streamed = Fnv1aHash.Create();
        var captured = Fnv1aHash.Create();
        var copied = Fnv1aHash.Create();
        var entries = population.Capture().Entries;

        population.AppendContinuationHash(hash: ref streamed);
        WorldAuthorityCheckpointCodec.AppendPopulationEntries(
            entries: entries,
            hash: ref captured
        );
        WorldAuthorityCheckpointCodec.AppendPopulationEntries(
            entries: [.. entries.Select(selector: static entry => ((entry.Profile is { } profile)
                ? (entry with {
                    Profile = profile with {
                        Facts = ((profile.Facts is { } facts) ? (facts with { Cells = [.. (facts.Cells ?? [])] }) : null),
                        Records = ((profile.Records is { } records) ? (records with { }) : null),
                    },
                })
                : entry))],
            hash: ref copied
        );

        Assert.True(
            condition: (streamed.Value == captured.Value),
            userMessage: $"{context}: the streamed continuation {streamed.Value:X16} differs from the captured entries' {captured.Value:X16}"
        );
        Assert.True(
            condition: (streamed.Value == copied.Value),
            userMessage: $"{context}: the streamed continuation {streamed.Value:X16} differs from freshly encoded profiles' {copied.Value:X16}"
        );

        return streamed.Value;
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
        var definition = AuthoredGameFixtures.Load(relativePath: "tests/Puck.World.Tests/Fixtures/minimal-paddleball-host.puck");
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
    // THE LAW: a seat driven by an owned identity folds the bytes its checkpoint entry encodes, its facts and records
    // included, and a fact or record written on that identity moves the fold. The identity carries one fact and one
    // written record field; each write lands on the identity alone, between two folds with no step between them. The
    // red leg is a hash path that skips the profile to save its encoding: the streamed fold no longer equals the
    // captured entry's, and a fact write leaves it unmoved.
    [Fact]
    public void AProfiledSeatFoldsTheBytesItsCheckpointEntryEncodes() {
        using var fixture = Fixtures.FreshServer();
        var owned = CrossingIdentityPrivacyLawTests.Owned();

        _ = fixture.JoinSeat();
        fixture.Server.Population.SetSeatProfile(profile: owned, slot: 0);
        fixture.Step();

        var population = fixture.Server.Population;
        var steady = AssertStreamedFoldsTheCapturedEntries(context: "profiled seat", population: population);

        Assert.True(condition: owned.TrySetFact(changed: out _, key: CellName.Parse(candidate: "laterFact"), reason: out var reason, value: 9), userMessage: reason);
        var afterFact = AssertStreamedFoldsTheCapturedEntries(context: "after a fact write", population: population);

        Assert.NotEqual(actual: afterFact, expected: steady);
        Assert.True(condition: owned.TryWriteRecord(field: CellName.Parse(candidate: "score"), reason: out reason, record: CellName.Parse(candidate: "stats"), value: CellValue.Int(value: 42)), userMessage: reason);
        var afterRecord = AssertStreamedFoldsTheCapturedEntries(context: "after a record write", population: population);

        Assert.NotEqual(actual: afterRecord, expected: afterFact);
        for (var step = 0; (step < 10); step++) {
            fixture.Step();
            _ = AssertStreamedFoldsTheCapturedEntries(context: $"profiled step {step}", population: population);
        }
    }
    // THE LAW: a profiled seat's steady fold allocates nothing. Seat 0 joins as the catalog's boot identity with one
    // integer fact, and seat 1 is driven by an owned identity carrying a fact and records. Once warm, folding with
    // nothing changed allocates nothing. The red leg validates the facts row and serializes the records on every
    // write, and projects a fresh facts row for an identity that has none.
    [Fact]
    public void AProfiledSteadyFoldAllocatesNothing() {
        using var fixture = Fixtures.FreshServer();
        var server = fixture.Server;
        var catalog = server.Profiles;
        var boot = catalog.BootProfile;

        Assert.True(condition: server.ApplySession(request: new SessionRequest.Join(
            IdentityName: boot.Name,
            Principal: Principal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
        Assert.True(condition: catalog.TrySetFact(changed: out _, identity: boot, key: CellName.Parse(candidate: "score"), reason: out var reason, value: 4), userMessage: reason);
        Assert.Same(expected: boot, actual: server.Population.EntryBody(index: 0)!.Profile);
        _ = fixture.JoinSeat(slot: 1);
        server.Population.SetSeatProfile(profile: CrossingIdentityPrivacyLawTests.Owned(), slot: 1);
        fixture.Step();

        var population = server.Population;
        var warm = Fnv1aHash.Create();

        population.AppendContinuationHash(hash: ref warm);
        Assert.Equal(
            actual: AllocationWindow.Least(window: () => {
                var hash = Fnv1aHash.Create();

                population.AppendContinuationHash(hash: ref hash);
            }),
            expected: 0L
        );
    }
}
