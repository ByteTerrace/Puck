using Xunit;

using Puck.Assets.Documents;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>WorldTune</c>/<c>WorldPatch</c> are name/source/hash reference rows — never embedded
/// documents (the same shape <c>WorldMusicRow</c> already uses). <c>WorldDefinitionValidator</c> loads each row's
/// <c>Source</c> off disk before it can validate/canonicalize/hash-pin anything, so a row naming a path that does
/// not resolve must refuse on that load, the same discipline <c>CheckMusic</c> already proves for music rows.
/// </summary>
public sealed class TuneAndPatchAssetSourceValidationLawTests {
    [Fact]
    public void MissingTuneSourceRefusesByPath() {
        var document = Fixtures.BuildDocument() with {
            TunesRaw = [new WorldTune(Hash: "0000000000000000000000000000000000000000000000000000000000000000", Name: "missing-tune", Source: "does-not-exist.puck.audio.v1.json")],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: "a tune row naming an unresolvable source was expected to refuse");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "tunes[0]");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "does-not-exist.puck.audio.v1.json");
    }
    [Fact]
    public void ValidTuneSourceControl() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-tune-source-law-").FullName;

        try {
            var document = Fixtures.BuildDocument() with {
                TunesRaw = [BuildTuneRow(assetDirectory: directory, name: "real-tune")],
            };

            Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: reason);
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }
    [Fact]
    public void MissingPatchSourceRefusesByPath() {
        var document = Fixtures.BuildDocument() with {
            PatchesRaw = [new WorldPatch(Hash: "0000000000000000000000000000000000000000000000000000000000000000", Name: "missing-patch", Source: "does-not-exist.puck.synth.v1.json")],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: "a patch row naming an unresolvable source was expected to refuse");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "patches[0]");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "does-not-exist.puck.synth.v1.json");
    }
    [Fact]
    public void ValidPatchSourceControl() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-patch-source-law-").FullName;

        try {
            var document = Fixtures.BuildDocument() with {
                PatchesRaw = [BuildPatchRow(assetDirectory: directory, name: "real-patch")],
            };

            Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: reason);
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }

    private static WorldTune BuildTuneRow(string assetDirectory, string name) {
        var tune = AudioCanonicalizer.Canonicalize(document: new AudioDocument(Effects: null, Name: name, Order: null, Patterns: null, Schema: AudioDocument.CurrentSchema, Tempo: null));
        var path = Path.Combine(path1: assetDirectory, path2: $"{name}.puck.audio.v1.json");

        File.WriteAllBytes(path: path, bytes: tune.Bytes);

        return new WorldTune(Name: name, Source: path, Hash: tune.Hash);
    }
    private static WorldPatch BuildPatchRow(string assetDirectory, string name) {
        var patch = SynthPatchCanonicalizer.Canonicalize(document: new SynthPatchDocument(Schema: SynthPatchDocument.CurrentSchema, Name: name, Oscillator: null, DutyThousandths: null, Polynomial: null, AttackFrames: null, DecayFrames: null, SustainThousandths: null, ReleaseFrames: null, PitchMillihertz: 440_000));
        var path = Path.Combine(path1: assetDirectory, path2: $"{name}.puck.synth.v1.json");

        File.WriteAllBytes(path: path, bytes: patch.Bytes);

        return new WorldPatch(Name: name, Source: path, Hash: patch.Hash);
    }
}
