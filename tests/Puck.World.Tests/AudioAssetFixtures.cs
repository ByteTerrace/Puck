using Puck.Assets.Documents;
using Puck.Testing;
using Puck.World.Authoring;

namespace Puck.World.Tests;

/// <summary>The suite's one construction of the audio asset rows a world references by name, source, and hash:
/// each document is canonicalized, written under a law's own <see cref="TemporaryDirectory"/>, and pinned by the hash
/// the validator re-derives when it loads the source.</summary>
internal static class AudioAssetFixtures {
    /// <summary>The tempo every fixture score authors unless a law needs its own.</summary>
    public const int TicksPerBeat = 2100;

    /// <summary>Builds a four-beat-bar score over <paramref name="segments"/>.</summary>
    /// <param name="name">The document name.</param>
    /// <param name="segments">The segments, in authored order.</param>
    /// <param name="ticksPerBeat">The engine ticks per beat.</param>
    /// <returns>The document.</returns>
    public static MusicDocument Score(string name, IReadOnlyList<MusicSegmentDocument> segments, int ticksPerBeat = TicksPerBeat) => new(
        Schema: MusicDocument.CurrentSchema,
        Name: name,
        Tempo: new MusicTempoDocument(
            BeatsPerBar: 4,
            TicksPerBeat: ticksPerBeat
        ),
        Segments: segments
    );
    /// <summary>Builds the world document whose only audio is a score over <paramref name="segment"/>, a silent
    /// <c>bed-tune</c>, and a 440 Hz <c>stinger</c> patch, all written under <paramref name="directory"/>.</summary>
    /// <param name="directory">The directory the sources are written to; it outlives every read of them.</param>
    /// <param name="musicName">The music row and document name.</param>
    /// <param name="segment">The one segment the score authors.</param>
    /// <returns>The code-built fixture document carrying the three rows.</returns>
    public static WorldDefinition ScoredDocument(TemporaryDirectory directory, string musicName, MusicSegmentDocument segment) => Fixtures.BuildDocument() with {
        Music = [Write(
            directory: directory,
            document: Score(
                name: musicName,
                segments: [segment]
            )
        )],
        PatchesRaw = [Write(
            directory: directory,
            document: Tone(name: "stinger")
        )],
        TunesRaw = [Write(
            directory: directory,
            document: SilentTune(name: "bed"),
            rowName: "bed-tune"
        )],
    };
    /// <summary>Builds a tune with no patterns, order, effects, or tempo.</summary>
    /// <param name="name">The document name.</param>
    /// <returns>The document.</returns>
    public static AudioDocument SilentTune(string name) => new(
        Effects: null,
        Name: name,
        Order: null,
        Patterns: null,
        Schema: AudioDocument.CurrentSchema,
        Tempo: null
    );
    /// <summary>Builds a patch that authors only its pitch.</summary>
    /// <param name="name">The document name.</param>
    /// <param name="pitchMillihertz">The pitch, in millihertz.</param>
    /// <returns>The document.</returns>
    public static SynthPatchDocument Tone(string name, int pitchMillihertz = 440_000) => new(
        Schema: SynthPatchDocument.CurrentSchema,
        Name: name,
        Oscillator: null,
        DutyThousandths: null,
        Polynomial: null,
        AttackFrames: null,
        DecayFrames: null,
        SustainThousandths: null,
        ReleaseFrames: null,
        PitchMillihertz: pitchMillihertz
    );
    /// <summary>Writes <paramref name="document"/> canonically and returns the music row naming it by path and hash.</summary>
    /// <param name="directory">The directory the source is written to.</param>
    /// <param name="document">The score.</param>
    /// <param name="rowName">The row name, or <see langword="null"/> for the document's own name.</param>
    /// <returns>The row.</returns>
    public static WorldMusicRow Write(TemporaryDirectory directory, MusicDocument document, string? rowName = null) => Write(
        canonical: MusicCanonicalizer.Canonicalize(document: document),
        directory: directory,
        extension: "puck.music.v1.json",
        name: (rowName ?? ((string)document.Name!)),
        row: static (name, source, hash) => new WorldMusicRow(
            Hash: hash,
            Name: name,
            Source: source
        )
    );
    /// <summary>Writes <paramref name="document"/> canonically and returns the tune row naming it by path and hash.</summary>
    /// <param name="directory">The directory the source is written to.</param>
    /// <param name="document">The tune.</param>
    /// <param name="rowName">The row name, or <see langword="null"/> for the document's own name.</param>
    /// <returns>The row.</returns>
    public static WorldTune Write(TemporaryDirectory directory, AudioDocument document, string? rowName = null) => Write(
        canonical: AudioCanonicalizer.Canonicalize(document: document),
        directory: directory,
        extension: "puck.tune.v1.json",
        name: (rowName ?? ((string)document.Name!)),
        row: static (name, source, hash) => new WorldTune(
            Hash: hash,
            Name: name,
            Source: source
        )
    );
    /// <summary>Writes <paramref name="document"/> canonically and returns the patch row naming it by path and hash.</summary>
    /// <param name="directory">The directory the source is written to.</param>
    /// <param name="document">The patch.</param>
    /// <param name="rowName">The row name, or <see langword="null"/> for the document's own name.</param>
    /// <returns>The row.</returns>
    public static WorldPatch Write(TemporaryDirectory directory, SynthPatchDocument document, string? rowName = null) => Write(
        canonical: SynthPatchCanonicalizer.Canonicalize(document: document),
        directory: directory,
        extension: "puck.synthesizer-patch.v1.json",
        name: (rowName ?? ((string)document.Name!)),
        row: static (name, source, hash) => new WorldPatch(
            Hash: hash,
            Name: name,
            Source: source
        )
    );

    private static TRow Write<TDocument, TRow>(CanonicalDocument<TDocument> canonical, TemporaryDirectory directory, string extension, string name, Func<string, string, string, TRow> row) => row(
        arg1: name,
        arg2: directory.WriteBytes(
            bytes: canonical.Bytes,
            name: $"{name}.{extension}"
        ),
        arg3: canonical.Hash
    );
}
