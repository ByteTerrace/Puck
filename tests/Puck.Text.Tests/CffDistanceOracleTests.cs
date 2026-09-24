using System.Security.Cryptography;
using System.Text.Json;

namespace Puck.Text.Tests;

public sealed class CffDistanceOracleTests {
    [InlineData("SourceSerif4-Regular.otf", "CFF ")]
    [InlineData("SourceSerif4Variable-Roman.otf", "CFF2")]
    [Theory]
    public void RealCubicFontsAgreeWithIndependentFilledBoundarySamples(string name, string outlineTable) {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path: Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Fonts",
            path3: "source-serif-oracle.json"
        )))!;
        var expected = snapshot.Fonts.Single(predicate: font => (font.Font == name));
        var bytes = File.ReadAllBytes(path: Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Fonts",
            path3: name
        ));

        Assert.Equal(
            expected.Sha256,
            Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes))
        );
        Assert.Equal(
            outlineTable,
            expected.OutlineTable
        );
        var atlas = new ManagedFontAtlasGenerator().Generate(request: new() {
            FontBytes = bytes,
            FontIdentifier = name,
            Options = new() { AllowedCharacters = "AgO", AllowedCodePointRanges = [], FontPixelSize = 32 },
        });
        var face = OpenTypeFontFace.Parse(
            bytes,
            0
        );

        foreach (var row in expected.Glyphs) {
            Assert.True(condition: atlas.TryGetGlyph(
                row.Unicode,
                out var glyph
            ));
            Assert.Equal(
                row.GlyphId,
                glyph.GlyphId
            );
            Assert.Equal(
                row.Advance,
                glyph.Advance,
                6
            );
            var geometry = face.LoadGlyphGeometry(
                glyphId: ((ushort)row.GlyphId),
                scale: (32f / face.UnitsPerEm)
            ).Geometry;
            float[] actualBounds = [geometry.Left, geometry.Top, geometry.Right, geometry.Bottom];

            for (var i = 0; (i < 4); i++) {
                Assert.InRange(
                Math.Abs(value: (actualBounds[i] - row.Bounds[i])),
                0,
                0.04
            );
            }
            var plane = glyph.PlaneBounds!.Value;
            var cell = glyph.AtlasBounds!.Value;

            foreach (var sample in row.Samples) {
                var x = ((int)Math.Round(a: (((cell.Left + sample[0]) - (plane.Left * 32)) - 0.5)));
                var y = ((int)Math.Round(a: (((cell.Top + sample[1]) + (plane.Top * 32)) - 0.5)));

                Assert.InRange(
                    x,
                    ((int)cell.Left),
                    (((int)cell.Right) - 1)
                );
                Assert.InRange(
                    y,
                    ((int)cell.Top),
                    (((int)cell.Bottom) - 1)
                );
                var actual = (((atlas.ImageData!.RgbaPixels[((((y * atlas.Width) + x) * 4) + 3)] / 255d) - 0.5) * 8);
                var distance = Math.Clamp(
                    sample[2],
                    -4,
                    4
                );

                Assert.True(
                    condition: (Math.Abs(value: (actual - distance)) <= 0.06),
                    userMessage: $"{name} U+{row.Unicode:X4} ({sample[0]}, {sample[1]}): expected {distance}, actual {actual}"
                );
            }
        }
    }

    private sealed record Snapshot(FontOracle[] Fonts);
    private sealed record FontOracle(string Font, string Sha256, string OutlineTable, GlyphOracle[] Glyphs);
    private sealed record GlyphOracle(int Unicode, int GlyphId, float Advance, double[] Bounds, double[][] Samples);
}
