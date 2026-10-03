using System.Buffers.Binary;
using Puck.Assets;
using Puck.Overlays;
using Puck.Testing;
using Xunit;

namespace Puck.Launcher.Tests;

public sealed class OverlayGlyphCacheTests {
    private const string Layout = """
        {"atlas":{"type":"mtsdf","size":32,"distanceRange":8,"width":4,"height":2},
         "metrics":{},"glyphs":[
          {"unicode":33,"advance":1,"atlasBounds":{"left":0.5,"top":0.5,"right":1.5,"bottom":1.5}},
          {"unicode":57344,"advance":1,"atlasBounds":{"left":2.5,"top":0.5,"right":3.5,"bottom":1.5}}]}
        """;

    private static void WriteSources(string directory, byte red = 11) {
        var pixels = new byte[32];

        for (var i = 0; (i < 8); i++) {
            pixels[(i * 4)] = ((byte)(red + i));
            pixels[((i * 4) + 3)] = 255;
        }
        PngEncoder.Write(Path.Combine(path1: directory, path2: "puck-fonts-mtsdf.png"), pixels, 4, 2);
        File.WriteAllText(Path.Combine(path1: directory, path2: "jetbrains-mono-regular.json"), Layout);
    }

    [Fact]
    public void AlternatingRepertoiresRoundTripWithoutRebuildingAndSourcesInvalidate() {
        using var directory = new TemporaryDirectory(prefix: "puck-glyph-cache-");

        WriteSources(directory.RootPath);
        var ascii = new OverlayGlyphAtlasSet(directory.RootPath).LoadOverlayPack();
        var icons = new OverlayGlyphAtlasSet(directory.RootPath).LoadOverlayPack(extraCodePoints: [0xe000]);

        Assert.NotNull(@object: ascii);
        Assert.NotNull(@object: icons);
        Assert.Equal(2, icons.AtlasCellWidth);
        Assert.Equal(2, icons.AtlasCellHeight);
        // Every source texel, including the right/bottom center endpoints, is copied in row order.
        Assert.Equal(new uint[] { 0xff00000d, 0xff00000e, 0xff000011, 0xff000012 },
            icons.SdfWords[(95 * 4)..].ToArray());
        var files = Directory.GetFiles(path: directory.RootPath, searchPattern: "*.pack");

        Assert.Equal(2, files.Length);
        var stamp = new DateTime(day: 1, hour: 0, kind: DateTimeKind.Utc, minute: 0, month: 1, second: 0, year: 2001);

        foreach (var file in files) { File.SetLastWriteTimeUtc(lastWriteTimeUtc: stamp, path: file); }
        Assert.Equal(ascii.SdfWords.ToArray(), new OverlayGlyphAtlasSet(directory.RootPath).LoadOverlayPack()!.SdfWords.ToArray());
        Assert.Equal(icons.SdfWords.ToArray(), new OverlayGlyphAtlasSet(directory.RootPath).LoadOverlayPack(extraCodePoints: [0xe000])!.SdfWords.ToArray());
        Assert.All(files, file => Assert.Equal(stamp, File.GetLastWriteTimeUtc(path: file)));
        WriteSources(directory: directory.RootPath, red: 31);
        var changed = new OverlayGlyphAtlasSet(directory.RootPath).LoadOverlayPack(extraCodePoints: [0xe000]);

        Assert.NotNull(@object: changed);
        Assert.Equal(0xff000021u, changed.SdfWords[(95 * 4)]);
        var layoutPath = Path.Combine(path1: directory.RootPath, path2: "jetbrains-mono-regular.json");

        File.WriteAllText(layoutPath, Layout.Replace(newValue: "\"distanceRange\":6", oldValue: "\"distanceRange\":8"));
        Assert.Equal(6, new OverlayGlyphAtlasSet(directory.RootPath).LoadOverlayPack(extraCodePoints: [0xe000])!.DistanceRange);
    }
    [InlineData("short")]
    [InlineData("shape")]
    [InlineData("version")]
    [InlineData("dimensions")]
    [InlineData("range")]
    [Theory]
    public void MalformedCacheRebuildsFromSource(string fault) {
        using var directory = new TemporaryDirectory(prefix: "puck-glyph-corrupt-");

        WriteSources(directory.RootPath);
        var expected = new OverlayGlyphAtlasSet(directory.RootPath).LoadOverlayPack(extraCodePoints: [0xe000]);

        Assert.NotNull(@object: expected);
        var path = Assert.Single(collection: Directory.GetFiles(path: directory.RootPath, searchPattern: "*.pack"));
        var bytes = File.ReadAllBytes(path: path);
        var original = bytes.ToArray();

        switch (fault) {
            case "short": bytes = bytes[..120]; break;
            // The same magic and version under another shape fingerprint: the layout after it is unknown to this build.
            case "shape": System.Text.Encoding.ASCII.GetBytes(bytes: bytes.AsSpan(start: 8), chars: "0000000000000000"); break;
            case "version": BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes.AsSpan(start: 4), value: 2); break;
            case "dimensions": BinaryPrimitives.WriteInt32LittleEndian(destination: bytes.AsSpan(start: 24), value: int.MaxValue); break;
            case "range": BinaryPrimitives.WriteSingleLittleEndian(destination: bytes.AsSpan(start: 32), value: float.NaN); break;
        }
        File.WriteAllBytes(bytes: bytes, path: path);
        var actual = new OverlayGlyphAtlasSet(directory.RootPath).LoadOverlayPack(extraCodePoints: [0xe000]);

        Assert.NotNull(@object: actual);
        Assert.Equal(expected.SdfWords.ToArray(), actual.SdfWords.ToArray());
        // The refused cache was not served: the rebuild rewrote the file as this build writes it.
        Assert.Equal(original, File.ReadAllBytes(path: path));
    }
    [Fact]
    public void ThePackCarriesTheShapeTheLedgerRecords() {
        using var directory = new TemporaryDirectory(prefix: "puck-glyph-shape-");

        WriteSources(directory.RootPath);
        _ = new OverlayGlyphAtlasSet(directory.RootPath).LoadOverlayPack(extraCodePoints: [0xe000]);

        var bytes = File.ReadAllBytes(path: Assert.Single(collection: Directory.GetFiles(path: directory.RootPath, searchPattern: "*.pack")));

        Assert.Equal(
            FormatLedgerShapes.Of(id: "OverlayGlyphSdfPack.PackVersion"),
            System.Text.Encoding.ASCII.GetString(bytes: bytes.AsSpan(length: 16, start: 8))
        );
    }
    [Fact]
    public void ShippedAtlasCellsCanBePacked() {
        var fonts = Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Fonts");
        var atlas = new OverlayGlyphAtlasSet(fonts).MonoFont;

        Assert.NotNull(@object: atlas);
        var pack = OverlayGlyphSdfPack.TryCreate(extraCodePoints: [0x2190, 0x2192], monoFont: atlas);

        Assert.NotNull(@object: pack);
        Assert.Equal(47, pack.AtlasCellWidth);
        Assert.Equal(83, pack.AtlasCellHeight);
        Assert.Contains(collection: pack.PackedSdf, filter: word => (word != 0));
    }
}
