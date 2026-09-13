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
        var snapshot = JsonSerializer.Deserialize<OracleSnapshot>(File.ReadAllText(path: Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Fonts",
            path3: "jetbrains-mono-oracle.json"
        )))!;
        var bytes = File.ReadAllBytes(path: Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Fonts",
            path3: "JetBrainsMono-Regular.ttf"
        ));

        Assert.Equal(
            snapshot.Sha256,
            Convert.ToHexString(inArray: SHA256.HashData(source: bytes)).ToLowerInvariant()
        );
        var atlas = new ManagedFontAtlasGenerator().Generate(request: new FontAtlasGenerationRequest {
            FontBytes = bytes,
            FontIdentifier = "oracle://JetBrainsMono-Regular.ttf",
            Options = new FontAtlasGenerationOptions {
                AllowedCharacters = " A",
                AllowedCodePointRanges = ["U+0020"],
                Columns = 2,
                DistanceRange = 4,
                FontPixelSize = 32,
                MaxAtlasDimension = 256,
                MaxAtlasPixels = (256 * 256),
                Padding = 4,
            },
        });

        Assert.True(condition: atlas.TryGetGlyph(
            glyph: out var glyphA,
            unicode: 'A'
        ));
        Assert.True(condition: atlas.TryGetGlyph(
            glyph: out var glyphSpace,
            unicode: ' '
        ));
        Assert.Equal(
            snapshot.AdvanceA,
            glyphA.Advance,
            precision: 6
        );
        Assert.Equal(
            snapshot.AdvanceSpace,
            glyphSpace.Advance,
            precision: 6
        );
        Assert.Equal(
            snapshot.GlyphIdA,
            glyphA.GlyphId
        );
        Assert.Equal(
            snapshot.GlyphIdSpace,
            glyphSpace.GlyphId
        );
        Assert.Equal(
            snapshot.Ascender,
            atlas.Metrics.Ascender,
            precision: 6
        );
        Assert.Equal(
            snapshot.Descender,
            atlas.Metrics.Descender,
            precision: 6
        );
        Assert.Equal(
            snapshot.LineHeight,
            atlas.Metrics.LineHeight,
            precision: 6
        );
        Assert.Equal(
            snapshot.UnderlineY,
            atlas.Metrics.UnderlineY,
            precision: 6
        );
        Assert.Equal(
            snapshot.UnderlineThickness,
            atlas.Metrics.UnderlineThickness,
            precision: 6
        );
        Assert.True(condition: atlas.TryGetGlyphById(
            glyphId: snapshot.GlyphIdA,
            glyph: out var byId
        ));
        Assert.Same(
            actual: byId,
            expected: glyphA
        );
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
