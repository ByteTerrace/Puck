using Puck.Abstractions.Counting;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Games.Tests;

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
    public void ARigidBallComingToRestFoldsTheBytesItsCheckpointEntryEncodes() {
        var definition = AuthoredGameFixtures.Load(relativePath: "tests/Puck.World.Fixtures/minimal-paddleball-host.puck");
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
}
