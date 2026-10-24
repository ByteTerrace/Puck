using System.Numerics;
using Puck.Overlays;
using Puck.Text;
using Xunit;

namespace Puck.World.Client.Tests;

public sealed class DiagnosticOverlayBudgetTests {
    private sealed class Source : IDiagnosticOverlaySource {
        public DiagnosticOverlayFrame Snapshot() => new(
            Enumerable.Range(0, 20).Select(i => new DiagnosticOverlayPin(new string('1', 40), new(0.5f, 0.5f),
                new(0.4f, 0.4f, 0.1f, 0.1f), false, false, false, 0, new Vector2(0.5f, 0.6f), Vector2.UnitY)).ToArray(),
            Enumerable.Range(0, AnnotationWriter.MaxDiagnosticRegions + 36).Select(i => new Vector4((i % 10) * 0.1f, (i / 10 % 10) * 0.1f, 0.02f, 0.02f)).ToArray(),
            new(0, 0, 1, 1), new string('1', 160), true, false);
    }

    [Fact]
    public void OverflowEvidenceAndTheFullPinSetFitTheirOwnReservation() {
        var glyphs = OverlayGlyphSdfPack.TryCreate(new FontAtlas(kind: FontAtlasKind.Sdf,
            imagePath: "diagnostic.png", size: 1, distanceRange: 1, width: 1, height: 1, metrics: default,
            glyphs: [new FontAtlasGlyph(unicode: '1', advance: 1,
                planeBounds: new(Bottom: 0, Left: 0, Right: 1, Top: 1), atlasBounds: new(Bottom: 1, Left: 0, Right: 1, Top: 0))],
            kerningPairs: [], imageData: new FontAtlasImageData(height: 1, width: 1, rgbaPixels: [255, 255, 255, 255])));
        Assert.NotNull(glyphs);
        var builder = new OverlayFrameBuilder(glyphs: glyphs, width: 800, height: 600, theme: OverlayThemeValues.Zero,
            leases: new OverlayChannelLeases(WorldOverlayCapacity.FromSchema()));
        builder.BeginFrame();
        builder.BeginChannel(OverlayChannel.Diagnostic);
        AnnotationWriter.EmitDiagnostics(builder, new Source());
        builder.EndChannel();
        Assert.True(builder.Refused(OverlayChannel.Diagnostic).TextWords > 0); // The long banner is deliberately clipped.
        Assert.Equal(default, builder.Dropped(OverlayChannel.Diagnostic));
        Assert.InRange(builder.ElementCount, 1, builder.ReservationOf(OverlayChannel.Diagnostic).Elements);
        Assert.InRange(builder.TextWordCount, 1, builder.ReservationOf(OverlayChannel.Diagnostic).TextWords);
        Assert.Equal(default, builder.Written(OverlayChannel.Annotation));
    }
}
