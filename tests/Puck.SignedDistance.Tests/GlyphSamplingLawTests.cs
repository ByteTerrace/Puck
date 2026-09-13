using System.Numerics;
using Puck.Text;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class GlyphSamplingLawTests {
    private static FontAtlas Atlas(bool pixels) => new(FontAtlasKind.Mtsdf, "test://bilinear", 32, 8, 2, 2,
        default, [], [], pixels ? new FontAtlasImageData([0, 0, 0, 0, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 0], 2, 2) : null);

    private static SdfInstruction Glyph(FontAtlas atlas, float halfWidth, float halfHeight) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));
        return builder.Glyph(atlas, Vector2.Zero, Vector2.One, halfWidth, halfHeight, 0.1f, 8, material)
            .Build().Instructions.Single(i => i.Shape == (uint)SdfShapeType.Glyph && i.Op == SdfOp.ShapeBlend);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0.01f, 10)]
    [InlineData(10, 0.01f)]
    public void SamplingCorrectionBoundsActualBilinearDerivativesIncludingStretchedCells(float halfWidth, float halfHeight) {
        var atlas = Atlas(true);
        var correction = Glyph(atlas, halfWidth, halfHeight).Data1.W;
        Assert.InRange(correction, float.Epsilon, 1f);
        // Independent analytic derivatives of the checkerboard bilinear patch:
        // a(u,v)=u+v-2uv; UV spans two texels across the cell's full world extent.
        for (var yi = 0; yi <= 100; yi++) {
            for (var xi = 0; xi <= 100; xi++) {
                var dx = (1 - 2 * yi / 100d) * 8 / halfWidth;
                var dy = (1 - 2 * xi / 100d) * 8 / halfHeight;
                Assert.True(Math.Sqrt(dx * dx + dy * dy) * correction <= 1);
            }
        }
        Assert.True(atlas.ImageData!.AlphaGradientBound.X >= 1);
        Assert.True(atlas.ImageData.AlphaGradientBound.Y >= 1);
        Assert.True(Glyph(Atlas(false), halfWidth, halfHeight).Data1.W <= correction * 1.000001f);
    }

    [Fact]
    public void PackedGlyphRejectsInvalidCorrectionAndZeroExtents() {
        var glyph = Glyph(Atlas(true), 1, 1);
        foreach (var correction in new[] { 0f, -1f, 1.01f, float.NaN }) {
            Assert.Throws<ArgumentException>(() => new SdfProgram(
                [glyph with { Data1 = glyph.Data1 with { W = correction } }], [new SdfMaterial(Albedo: Vector3.One)]));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => Glyph(Atlas(true), 0, 1));
    }
}
