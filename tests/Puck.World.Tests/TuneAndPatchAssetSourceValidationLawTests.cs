using Puck.Testing;
using Xunit;


namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>WorldTune</c>/<c>WorldPatch</c> are name/source/hash reference rows — never embedded
/// documents (the same shape <c>WorldMusicRow</c> already uses). <c>WorldDefinitionValidator</c> loads each row's
/// <c>Source</c> off disk before it can validate/canonicalize/hash-pin anything, so a row naming a path that does
/// not resolve must refuse on that load, the same discipline <c>CheckMusic</c> already proves for music rows.
/// </summary>
public sealed class TuneAndPatchAssetSourceValidationLawTests {
    // Each arm pairs the denial (a row naming a source that does not resolve) with the control (the same section
    // naming a written, hash-pinned source).
    [InlineData("patches", "does-not-exist.puck.synthesizer-patch.v1.json")]
    [InlineData("tunes", "does-not-exist.puck.tune.v1.json")]
    [Theory]
    public void AnUnresolvableSourceRefusesByPathWhereAWrittenOnePasses(string section, string missing) {
        const string AbsentHash = "0000000000000000000000000000000000000000000000000000000000000000";

        using var directory = new TemporaryDirectory();
        var patches = (section == "patches");
        var refused = (patches
            ? Fixtures.BuildDocument() with { PatchesRaw = [new WorldPatch(Hash: AbsentHash, Name: "missing", Source: missing)] }
            : Fixtures.BuildDocument() with { TunesRaw = [new WorldTune(Hash: AbsentHash, Name: "missing", Source: missing)] });
        var admitted = (patches
            ? Fixtures.BuildDocument() with { PatchesRaw = [AudioAssetFixtures.Write(directory: directory, document: AudioAssetFixtures.Tone(name: "real-patch"))] }
            : Fixtures.BuildDocument() with { TunesRaw = [AudioAssetFixtures.Write(directory: directory, document: AudioAssetFixtures.SilentTune(name: "real-tune"))] });

        Assert.False(
            condition: WorldDefinitionValidator.TryValidate(
                definition: refused,
                neighbours: null,
                reason: out var reason
            ),
            userMessage: $"a {section} row naming an unresolvable source was expected to refuse"
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"{section}[0]"
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: missing
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: admitted,
                neighbours: null,
                reason: out var admittedReason
            ),
            userMessage: admittedReason
        );
    }
}
