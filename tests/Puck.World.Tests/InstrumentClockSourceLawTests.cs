using Xunit;

using Puck.Assets.Documents;
using Puck.World.Authoring;
using Puck.HumbleGamingBrick.Forge.Tune;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the diegetic-instrument clock fold (<c>WorldServer.InstrumentClockBoundary</c>) — holding a
/// <see cref="GrantSubjectKind.Screen"/> application onto a booted <c>tune-instrument</c> machine folds its authored
/// tempo into <c>music.state</c>'s boundary-derived fields, deterministically, and NEVER when unengaged. The fixture
/// world's sole music segment arms a <c>BeatEnd</c> transition off <c>seat.join</c> (fires at boot regardless of
/// engagement) against a WORLD tempo authored at the slowest legal beat (50400 ticks, the max divisor of the
/// engine's fixed tick base — one second) — far longer than this test's own step budget — so the transition commits
/// ONLY when the instrument's own much faster authored tempo (840 ticks, <c>AudioDocument</c>'s minimum) is folded
/// in, which happens if and only if the seat holds the screen application.
/// </summary>
public sealed class InstrumentClockSourceLawTests {
    private const int InstrumentScreenIndex = 1;
    private const int StepBudgetAfterJoin = 10;
    private const int WorldTicksPerBeat = 50400;

    [Fact]
    public void EngagingTheInstrumentCommitsTheTransition_UnengagedControlLeavesItArmed() {
        var engagedDirectory = Directory.CreateTempSubdirectory(prefix: "puck-instrument-clock-law-engaged-").FullName;
        var controlDirectory = Directory.CreateTempSubdirectory(prefix: "puck-instrument-clock-law-control-").FullName;

        try {
            var engaged = RunAndReadMusicState(assetDirectory: engagedDirectory, engage: true);
            var control = RunAndReadMusicState(assetDirectory: controlDirectory, engage: false);

            Assert.Contains(actualString: engaged, comparisonType: StringComparison.Ordinal, expectedSubstring: "segment=driven");
            Assert.Contains(actualString: control, comparisonType: StringComparison.Ordinal, expectedSubstring: "segment=idle");
            Assert.Contains(actualString: control, comparisonType: StringComparison.Ordinal, expectedSubstring: "pending=driven");
        } finally {
            Directory.Delete(path: engagedDirectory, recursive: true);
            Directory.Delete(path: controlDirectory, recursive: true);
        }
    }
    [Fact]
    public void TwoIndependentEngagedBootsReDeriveTheIdenticalMusicState() {
        var directoryA = Directory.CreateTempSubdirectory(prefix: "puck-instrument-clock-law-a-").FullName;
        var directoryB = Directory.CreateTempSubdirectory(prefix: "puck-instrument-clock-law-b-").FullName;

        try {
            var a = RunAndReadMusicState(assetDirectory: directoryA, engage: true);
            var b = RunAndReadMusicState(assetDirectory: directoryB, engage: true);

            Assert.Equal(actual: b, expected: a);
        } finally {
            Directory.Delete(path: directoryA, recursive: true);
            Directory.Delete(path: directoryB, recursive: true);
        }
    }

    private static string RunAndReadMusicState(string assetDirectory, bool engage) {
        using var fixture = Fixtures.FreshServer(
            definition: BuildDocument(assetDirectory: assetDirectory),
            engines: [new TuneInstrumentEngine()]
        );
        var seat = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: seat, Slot: seat.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        // Lands the seat.join sense edge (WorldEventFeed.Collect runs inside this Step call) — arms the transition
        // for BOTH runs identically, so the only discriminating fact below is engagement.
        fixture.Step();

        if (engage) {
            Assert.True(condition: fixture.Server.Engagement.Compose(
                actingPrincipal: seat,
                entityIndex: seat.Index,
                exclusive: true,
                target: GrantSubject.Screen(index: InstrumentScreenIndex),
                targetPrincipal: seat
            ));
        }

        for (var step = 0; (step < StepBudgetAfterJoin); step++) {
            fixture.Step();
        }

        return fixture.Server.Answer(query: new WorldQuery.MusicState(Index: 1)).Text;
    }
    /// <summary>Builds the fixture: <see cref="Fixtures.BuildDocument"/> plus a real <c>puck.music.v1</c>/
    /// <c>puck.audio.v1</c> pair written to <paramref name="assetDirectory"/> and referenced by absolute path, an
    /// engageable <c>tune-instrument</c> screen at <see cref="InstrumentScreenIndex"/>, and one music segment whose
    /// transition discriminates the fold.</summary>
    private static WorldDefinition BuildDocument(string assetDirectory) {
        var music = MusicCanonicalizer.Canonicalize(document: new MusicDocument(
            Schema: MusicDocument.CurrentSchema,
            Name: "instrument-clock-law",
            Tempo: new MusicTempoDocument(BeatsPerBar: 4, TicksPerBeat: WorldTicksPerBeat),
            Segments: [
                new MusicSegmentDocument(
                    Id: "idle",
                    Transitions: [new MusicTransitionDocument(At: MusicTransitionBoundary.BeatEnd, To: "driven", When: WorldAudioCue.SeatJoin)]
                ),
                new MusicSegmentDocument(Id: "driven", Transitions: null),
            ]
        ));
        // The instrument's own authored tempo — AudioDocument's minimum (1 frame/row @ 60 fps), the fastest an
        // instrument can author: 840 engine ticks/beat, far inside this law's step budget.
        var instrument = AudioCanonicalizer.Canonicalize(document: new AudioDocument(Effects: null, Name: "fast-instrument", Order: null, Patterns: null, Schema: AudioDocument.CurrentSchema, Tempo: 1));

        var musicPath = Path.Combine(path1: assetDirectory, path2: "instrument-clock-law.puck.music.v1.json");
        var instrumentPath = Path.Combine(path1: assetDirectory, path2: "fast-instrument.puck.audio.v1.json");

        File.WriteAllBytes(path: musicPath, bytes: music.Bytes);
        File.WriteAllBytes(path: instrumentPath, bytes: instrument.Bytes);

        var instrumentScreen = new WorldScreen(
            Index: InstrumentScreenIndex,
            Origin: new System.Numerics.Vector3(x: 0f, y: 1f, z: 0f),
            Right: new System.Numerics.Vector3(x: 1f, y: 0f, z: 0f),
            Up: new System.Numerics.Vector3(x: 0f, y: 1f, z: 0f),
            HalfWidth: 1f,
            HalfHeight: 1f,
            HalfDepth: 0.1f,
            Round: 0f,
            Source: new WorldScreenSource.Machine(Engine: "tune-instrument", ContentPath: instrumentPath, Options: null),
            Route: new WorldScreenRoute(Engageable: true, EngageRadius: 1000f)
        );
        var document = Fixtures.BuildDocument();

        return document with {
            Music = [new WorldMusicRow(Name: "instrument-clock-law", Source: musicPath, Hash: music.Hash)],
            ScreensRaw = [.. document.Screens, instrumentScreen],
        };
    }
}
