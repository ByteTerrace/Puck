using Xunit;

using Puck.Forge.Authoring;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="MusicDocument"/>/<see cref="MusicCanonicalizer"/> and
/// <see cref="JudgeDocument"/>/<see cref="JudgeCanonicalizer"/> — the validate→normalize→canonicalize boundary every
/// <c>puck.music.v1</c>/<c>puck.judge.v1</c> document crosses, mirroring the law <see cref="AudioCanonicalizer"/>/
/// <see cref="SynthPatchCanonicalizer"/> already establish for their own families.
/// </summary>
public sealed class MusicJudgeDocumentLawTests {
    private static MusicDocument Score() => new(
        Schema: MusicDocument.CurrentSchema,
        Name: "nexus-ambient",
        Tempo: new MusicTempoDocument(BeatsPerBar: 4, TicksPerBeat: 2100),
        Segments: [
            new MusicSegmentDocument(Id: "calm", Transitions: [
                new MusicTransitionDocument(At: MusicTransitionBoundary.BarEnd, To: "alert", When: "region.enter"),
            ]),
            new MusicSegmentDocument(Id: "alert", Transitions: [
                new MusicTransitionDocument(At: MusicTransitionBoundary.BarEnd, To: "calm", When: "region.exit"),
            ]),
        ]
    );
    private static JudgeDocument Windows() => new(
        Schema: JudgeDocument.CurrentSchema,
        Name: "nexus-drum-easy",
        Windows: [
            new JudgeWindowDocument(Grade: "perfect", ToleranceTicks: 105),
            new JudgeWindowDocument(Grade: "good", ToleranceTicks: 315),
        ]
    );

    [Fact]
    public void ValidMusicDocumentValidatesClean() {
        Assert.Empty(collection: MusicCanonicalizer.Validate(document: Score()));
    }
    [Fact]
    public void MusicMissingTempoIsRefused() {
        var violations = MusicCanonicalizer.Validate(document: (Score() with { Tempo = null! }));

        Assert.Contains(collection: violations, filter: violation => (violation.Path == "tempo"));
    }
    [Fact]
    public void MusicNonPositiveTicksPerBeatIsRefused() {
        var violations = MusicCanonicalizer.Validate(document: (Score() with { Tempo = new MusicTempoDocument(BeatsPerBar: 4, TicksPerBeat: 0) }));

        Assert.Contains(collection: violations, filter: violation => (violation.Path == "tempo.ticksPerBeat"));
    }
    [Fact]
    public void MusicDuplicateSegmentIdIsRefused() {
        var duplicated = (Score() with {
            Segments = [.. Score().Segments, new MusicSegmentDocument(Id: "calm", Transitions: null)],
        });

        Assert.Contains(collection: MusicCanonicalizer.Validate(document: duplicated), filter: violation => violation.Message.Contains(value: "duplicated"));
    }
    [Fact]
    public void MusicTransitionToUnknownSegmentIsRefused() {
        var broken = (Score() with {
            Segments = [
                new MusicSegmentDocument(Id: "calm", Transitions: [
                    new MusicTransitionDocument(At: MusicTransitionBoundary.BarEnd, To: "nowhere", When: "region.enter"),
                ]),
            ],
        });

        Assert.Contains(collection: MusicCanonicalizer.Validate(document: broken), filter: violation => violation.Message.Contains(value: "does not resolve"));
    }
    [Fact]
    public void MusicCanonicalizeIsIdempotentOverItsOwnNormalForm() {
        var first = MusicCanonicalizer.Canonicalize(document: Score());
        var second = MusicCanonicalizer.Canonicalize(document: first.Document);

        Assert.Equal(expected: first.Hash, actual: second.Hash);
        Assert.Equal(expected: first.Bytes, actual: second.Bytes);
    }
    [Fact]
    public void MusicCanonicalizeAppliesDefaultsThenStaysFixed() {
        var sparse = new MusicDocument(
            Schema: MusicDocument.CurrentSchema,
            Name: null,
            Tempo: new MusicTempoDocument(BeatsPerBar: null, TicksPerBeat: 2100),
            Segments: [new MusicSegmentDocument(Id: "calm", Transitions: null)]
        );
        var canonical = MusicCanonicalizer.Canonicalize(document: sparse);

        Assert.Equal(expected: "score", actual: canonical.Document.Name);
        Assert.Equal(expected: 4, actual: canonical.Document.Tempo.BeatsPerBar);

        var reCanonical = MusicCanonicalizer.Canonicalize(document: canonical.Document);

        Assert.Equal(expected: canonical.Hash, actual: reCanonical.Hash);
    }
    [Fact]
    public void ValidJudgeDocumentValidatesClean() {
        Assert.Empty(collection: JudgeCanonicalizer.Validate(document: Windows()));
    }
    [Fact]
    public void JudgeEmptyWindowsIsRefused() {
        var violations = JudgeCanonicalizer.Validate(document: (Windows() with { Windows = [] }));

        Assert.Contains(collection: violations, filter: violation => (violation.Path == "windows"));
    }
    [Fact]
    public void JudgeNegativeToleranceIsRefused() {
        var violations = JudgeCanonicalizer.Validate(document: (Windows() with {
            Windows = [new JudgeWindowDocument(Grade: "perfect", ToleranceTicks: -1)],
        }));

        Assert.Contains(collection: violations, filter: violation => (violation.Path == "windows[0].toleranceTicks"));
    }
    [Fact]
    public void JudgeDuplicateGradeIsRefused() {
        var violations = JudgeCanonicalizer.Validate(document: (Windows() with {
            Windows = [.. Windows().Windows, new JudgeWindowDocument(Grade: "perfect", ToleranceTicks: 500)],
        }));

        Assert.Contains(collection: violations, filter: violation => violation.Message.Contains(value: "duplicated"));
    }
    [Fact]
    public void JudgeCanonicalizeIsIdempotentOverItsOwnNormalForm() {
        var first = JudgeCanonicalizer.Canonicalize(document: Windows());
        var second = JudgeCanonicalizer.Canonicalize(document: first.Document);

        Assert.Equal(expected: first.Hash, actual: second.Hash);
        Assert.Equal(expected: first.Bytes, actual: second.Bytes);
    }
}
