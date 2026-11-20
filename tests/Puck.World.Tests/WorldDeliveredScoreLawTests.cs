using System.Text.Json;
using Puck.Assets.Documents;
using Puck.Audio.Simulation;
using Puck.Testing;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the score a listener hears follows the cues a rule emits. The debug world's press score
/// (<c>worlds/debug/score.music.json</c>) is stepped through <see cref="WorldAudioDirector.PresentLocalCues"/> with
/// the press's cues on a real <see cref="MusicClock"/> at the world's 240 Hz step, and <c>music.state</c>'s score half
/// reports the run segment, with every layer joined on a bar line of that clock.
/// </summary>
public sealed class WorldDeliveredScoreLawTests {
    private const ulong StepTicks = 210UL;

    private static WorldDefinition PressScoreDocument() {
        var directory = RepositoryPaths.Resolve(relativePath: $"{ShippedWorldDocuments.WorldDirectory}/debug");
        var score = JsonSerializer.Deserialize<MusicDocument>(
            json: File.ReadAllText(path: Path.Combine(path1: directory, path2: "score.music.json")),
            options: DocumentJsonOptions.Shared
        )!;

        return Fixtures.BuildDocument() with {
            DocumentDirectory = directory,
            Music = [new WorldMusicRow(Name: "score", Source: "score.music.json", Hash: MusicCanonicalizer.Canonicalize(document: score).Hash)],
        };
    }

    [Fact]
    public void ARunAtThePressMovesTheListenersScoreToTheRunSegmentWithEveryLayerOnABarLine() {
        var definition = PressScoreDocument();
        var director = new WorldAudioDirector(client: null, animator: null);
        var clock = new MusicClock(ticksPerBeat: 25200, beatsPerBar: 4);
        var bars = new HashSet<ulong>();

        // A run starts at tick 10, the second page turns at tick 500, and the run of set words holds from 600.
        for (var tick = 1UL; (tick <= 1100UL); tick++) {
            var boundary = clock.Advance(stepTicks: StepTicks);
            var cues = new List<WorldPresentationCue>();

            if (boundary.HasFlag(flag: MusicClockBoundary.Bar)) { _ = bars.Add(item: tick); }
            if (tick == 10UL) { cues.Add(item: Cue(name: "typing.start", tick: tick)); }
            if (tick == 500UL) { cues.Add(item: Cue(name: "typing.wave", tick: tick)); }
            if (tick >= 600UL) { cues.Add(item: Cue(name: "typing.heat1", tick: tick)); }

            director.PresentLocalCues(definition: definition, tick: new WorldPresentationCueTick(
                Tick: tick,
                MusicBoundary: (byte)boundary,
                MusicSenses: 0,
                Cues: [.. cues]
            ));
        }

        var state = director.DescribeMusicState();

        Assert.Contains(expectedSubstring: "segment=press-run", actualString: state);
        Assert.Contains(expectedSubstring: "transitions=2", actualString: state);

        var layers = state.Split(separator: "layers=")[1].Split(separator: ' ')[0].Split(separator: ',');

        Assert.Equal(expected: ["press-bed", "press-heat-1"], actual: layers.Select(selector: static layer => layer.Split(separator: '@')[0]));
        foreach (var layer in layers) {
            Assert.Contains(expected: ulong.Parse(s: layer.Split(separator: '@')[1], provider: System.Globalization.CultureInfo.InvariantCulture), collection: bars);
        }
    }

    private static WorldPresentationCue Cue(string name, ulong tick) => new(
        Cue: new WorldGameplayCue(Name: name, Payload: null, Body: null, Tick: tick),
        Site: null
    );
}
