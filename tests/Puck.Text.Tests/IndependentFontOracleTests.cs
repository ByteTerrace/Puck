using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace Puck.Text.Tests;

/// <summary>
/// Checks the managed reader against a small, checked-in snapshot produced by
/// fontTools. The snapshot is deliberately limited to table facts (mapping and
/// metrics); raster pixels remain an implementation detail and are not used as
/// an oracle.
/// </summary>
public sealed class IndependentFontOracleTests {
    [Fact]
    public void JetBrainsMonoMappingAndMetricsAgreeWithFontToolsSnapshot() {
        var snapshot = JsonSerializer.Deserialize<OracleSnapshot>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fonts", "jetbrains-mono-oracle.json")))!;
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "JetBrainsMono-Regular.ttf"));
        Assert.Equal(snapshot.Sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var atlas = new ManagedFontAtlasGenerator().Generate(new FontAtlasGenerationRequest {
            FontBytes = bytes,
            FontIdentifier = "oracle://JetBrainsMono-Regular.ttf",
            Options = new FontAtlasGenerationOptions {
                AllowedCharacters = " A",
                AllowedCodePointRanges = ["U+0020"],
                Columns = 2,
                DistanceRange = 4,
                FontPixelSize = 32,
                MaxAtlasDimension = 256,
                MaxAtlasPixels = 256 * 256,
                Padding = 4,
            },
        });

        Assert.True(atlas.TryGetGlyph(unicode: 'A', glyph: out var glyphA));
        Assert.True(atlas.TryGetGlyph(unicode: ' ', glyph: out var glyphSpace));
        Assert.Equal(snapshot.AdvanceA, glyphA.Advance, precision: 6);
        Assert.Equal(snapshot.AdvanceSpace, glyphSpace.Advance, precision: 6);
        Assert.Equal(snapshot.GlyphIdA, glyphA.GlyphId);
        Assert.Equal(snapshot.GlyphIdSpace, glyphSpace.GlyphId);
        Assert.Equal(snapshot.Ascender, atlas.Metrics.Ascender, precision: 6);
        Assert.Equal(snapshot.Descender, atlas.Metrics.Descender, precision: 6);
        Assert.Equal(snapshot.LineHeight, atlas.Metrics.LineHeight, precision: 6);
        Assert.Equal(snapshot.UnderlineY, atlas.Metrics.UnderlineY, precision: 6);
        Assert.Equal(snapshot.UnderlineThickness, atlas.Metrics.UnderlineThickness, precision: 6);
        Assert.True(atlas.TryGetGlyphById(glyphId: snapshot.GlyphIdA, glyph: out var byId));
        Assert.Same(glyphA, byId);
    }

    private sealed record OracleSnapshot(
        [property: JsonPropertyName("sha256")] string Sha256,
        int UnitsPerEm,
        int GlyphIdA,
        int GlyphIdSpace,
        float AdvanceA,
        float AdvanceSpace,
        float Ascender,
        float Descender,
        float LineHeight,
        float UnderlineY,
        float UnderlineThickness
    );
}
