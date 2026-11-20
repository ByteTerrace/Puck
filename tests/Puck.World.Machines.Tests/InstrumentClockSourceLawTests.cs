using Puck.Commands;
using Xunit;
using System.Text.Json;

using Puck.Assets.Documents;
using Puck.World.Authoring;
using Puck.HumbleGamingBrick.Forge.Tune;
using Puck.World.Protocol;
using Puck.Testing;

namespace Puck.World.Machines.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the diegetic-instrument clock fold (<c>WorldServer.InstrumentClockBoundary</c>) — holding a
/// <see cref="GrantSubjectKind.Screen"/> application onto a booted <c>tune-instrument</c> machine folds its authored
/// tempo's beat into the boundary delivered with each tick, which every listener's score steps on, and NEVER when
/// unengaged. The WORLD tempo is authored at the slowest legal beat (50400 ticks, one second), so over one instrument
/// beat (840 ticks, <c>AudioDocument</c>'s minimum) only the instrument can contribute a beat.
/// </summary>
public sealed class InstrumentClockSourceLawTests {
    private const int InstrumentScreenIndex = 1;
    private const ulong InstrumentTicksPerBeat = 840UL;
    private const int WorldTicksPerBeat = 50400;

    /// <summary>Builds the fixture: <see cref="Fixtures.BuildDocument"/> plus a real <c>puck.music.v1</c>/
    /// <c>puck.tune.v1</c> pair written to <paramref name="assetDirectory"/> and referenced by absolute path, and an
    /// engageable <c>tune-instrument</c> screen at <see cref="InstrumentScreenIndex"/>.</summary>
    private static WorldDefinition BuildDocument(string assetDirectory) {
        var music = MusicCanonicalizer.Canonicalize(document: new MusicDocument(
            Schema: MusicDocument.CurrentSchema,
            Name: "instrument-clock-law",
            Tempo: new MusicTempoDocument(
                BeatsPerBar: 4,
                TicksPerBeat: WorldTicksPerBeat
            ),
            Segments: [new MusicSegmentDocument(Id: "idle", Transitions: null)]
        ));
        // The instrument's own authored tempo — AudioDocument's minimum (1 frame/row @ 60 fps), the fastest an
        // instrument can author: 840 engine ticks/beat, far inside this law's step budget.
        var instrument = AudioCanonicalizer.Canonicalize(document: new AudioDocument(
            Effects: null,
            Name: "fast-instrument",
            Order: null,
            Patterns: null,
            Schema: AudioDocument.CurrentSchema,
            Tempo: 1
        ));

        var musicPath = Path.Combine(
            path1: assetDirectory,
            path2: "instrument-clock-law.puck.music.v1.json"
        );
        var instrumentPath = Path.Combine(
            path1: assetDirectory,
            path2: "fast-instrument.puck.tune.v1.json"
        );

        File.WriteAllBytes(
            path: musicPath,
            bytes: music.Bytes
        );
        File.WriteAllBytes(
            path: instrumentPath,
            bytes: instrument.Bytes
        );

        var instrumentScreen = new WorldScreen(
            Index: InstrumentScreenIndex,
            Origin: new System.Numerics.Vector3(
                x: 0f,
                y: 1f,
                z: 0f
            ),
            Right: new System.Numerics.Vector3(
                x: 1f,
                y: 0f,
                z: 0f
            ),
            Up: new System.Numerics.Vector3(
                x: 0f,
                y: 1f,
                z: 0f
            ),
            HalfWidth: 1f,
            HalfHeight: 1f,
            HalfDepth: 0.1f,
            Round: 0f,
            Source: new WorldScreenSource.Machine(
                Instance: "instrument",
                Output: "video"
            ),
            Route: new WorldScreenRoute(
                Engageable: true,
                EngageRadius: 1000f
            )
        );
        var document = Fixtures.BuildDocument();

        return document with {
            Music = [new WorldMusicRow(
                Name: "instrument-clock-law",
                Source: musicPath,
                Hash: music.Hash
            )],
            MachinesRaw = [.. document.Machines, new WorldMachine(
                "instrument",
                "tune-instrument",
                JsonSerializer.SerializeToElement(new { schema = "puck.tune-instrument.configuration.v1", content = new { path = instrumentPath } })
            )],
            SpeakersRaw = [new WorldSpeaker.Fixed(
                "instrument-speaker",
                new DocumentVector3(
                    x: 0f,
                    y: 0f,
                    z: 0f
                ),
                new WorldSpeakerFeed(
                    new WorldSpeakerSource.Machine(
                        Instance: "instrument",
                        Output: "audio"
                    ),
                    WorldSpeakerFeed.ChannelMix,
                    1f
                )
            )],
            ScreensRaw = [.. document.Screens, instrumentScreen],
        };
    }
    // The beat the engaged instrument folds into the boundary every score steps on, over one instrument beat of the
    // world clock: the boundary a listener's transition or gridded layer waits for.
    private static Puck.Audio.Simulation.MusicClockBoundary FoldedBoundary(string assetDirectory, bool engage) {
        using var fixture = Fixtures.FreshServer(
            definition: BuildDocument(assetDirectory: assetDirectory),
            engines: [new TuneInstrumentEngine()]
        );
        var seat = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: seat,
            Slot: seat.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
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

        fixture.Step();

        return fixture.Server.InstrumentClockBoundary(previousElapsedTicks: 0UL, currentElapsedTicks: InstrumentTicksPerBeat);
    }

    [Fact]
    public void EngagingTheInstrumentFoldsItsBeat_UnengagedControlFoldsNothing() {
        using var engagedDirectory = new TemporaryDirectory(prefix: "puck-instrument-clock-law-engaged-");
        using var controlDirectory = new TemporaryDirectory(prefix: "puck-instrument-clock-law-control-");

        Assert.Equal(
            expected: Puck.Audio.Simulation.MusicClockBoundary.Beat,
            actual: FoldedBoundary(assetDirectory: engagedDirectory.RootPath, engage: true)
        );
        Assert.Equal(
            expected: Puck.Audio.Simulation.MusicClockBoundary.None,
            actual: FoldedBoundary(assetDirectory: controlDirectory.RootPath, engage: false)
        );
    }
}
