using System.Text.Json;
using Puck.Assets.Documents;
using Xunit;

namespace Puck.Assets.Tests;

/// <summary>A canonical document hashes alike on every operating system. The canonical writer is indented, and an
/// indented <see cref="System.Text.Json"/> writer breaks lines with <see cref="Environment.NewLine"/> unless its
/// options name a newline, so a document canonicalized on Windows once hashed its CRLF bytes and the same document on
/// Linux its LF bytes. The first law fails without the pin only where the platform's newline is not LF (Windows); the
/// source law <c>JsonNewlineSpellingLawTests</c> in <c>tests/Puck.Cli.Tests</c> holds every indented writer to the pin
/// on every platform. The second is the control: a CRLF writer moves the hash.</summary>
public sealed class CanonicalNewlineLawTests {
    // The shipped stinger patch, whose canonical LF bytes hash to the value the worlds that load it record.
    private static readonly SynthPatchDocument Stinger = new(
        AttackFrames: 120,
        DecayFrames: 2400,
        DutyThousandths: null,
        Name: "stinger",
        Oscillator: SynthOscillator.Sine,
        PitchMillihertz: 880000,
        Polynomial: null,
        ReleaseFrames: 1200,
        Schema: SynthPatchDocument.CurrentSchema,
        SustainThousandths: 0
    );

    [Fact]
    public void TheCanonicalWriterBreaksLinesWithALineFeedWhateverThePlatformsNewline() {
        Assert.Equal(
            expected: "\n",
            actual: DocumentJsonOptions.Shared.NewLine
        );
        Assert.True(condition: DocumentJsonOptions.Shared.WriteIndented);

        var canonical = SynthPatchCanonicalizer.Canonicalize(document: Stinger);

        Assert.DoesNotContain(
            collection: canonical.Bytes,
            expected: ((byte)'\r')
        );
        Assert.Equal(
            expected: "5d8f53b2714ddc863bf96e4817055838e3b72d705cba99bb050ccd6a00c472b0",
            actual: canonical.Hash
        );
    }
    // The control: the same document written with Windows line breaks hashes to the value the worlds recorded before
    // the newline was pinned, so the setting above is what decides the hash.
    [Fact]
    public void AWriterThatBreaksLinesWithCrlfHashesTheSameDocumentDifferently() {
        var crlf = DocumentCanonicalizer.Canonicalize(
            document: SynthPatchCanonicalizer.Normalize(document: Stinger),
            options: new JsonSerializerOptions(options: DocumentJsonOptions.Shared) { NewLine = "\r\n" }
        );

        Assert.Equal(
            expected: "5126a83fc5816863b6ecf981ef238ef346653c028a589520d766ec4ab82260ca",
            actual: crlf.Hash
        );
        Assert.NotEqual(
            expected: SynthPatchCanonicalizer.Canonicalize(document: Stinger).Hash,
            actual: crlf.Hash
        );
    }
}
