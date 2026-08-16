using System.Buffers.Binary;

namespace Puck.Text.Tests;

public sealed class OpenTypeOutlineTests {
    private static FontAtlas Generate(ReadOnlyMemory<byte> bytes, int faceIndex = 0) =>
        new ManagedFontAtlasGenerator().Generate(request: new FontAtlasGenerationRequest {
            FontBytes = bytes,
            FontIdentifier = "test://synthetic-opentype",
            Options = new FontAtlasGenerationOptions {
                AllowedCharacters = "A",
                AllowedCodePointRanges = [],
                Columns = 1,
                DistanceRange = 4,
                FaceIndex = faceIndex,
                FontPixelSize = 32,
                MaxAtlasDimension = 256,
                MaxAtlasPixels = (256 * 256),
                Padding = 4,
            },
        });

    [Fact]
    public void CffCubicOutlineGeneratesMarchableAtlasCoverage() {
        var atlas = Generate(bytes: SyntheticCffFont.Build(cff2: false));

        AssertCubicGlyph(atlas: atlas);
    }
    [Fact]
    public void Cff2CubicOutlineGeneratesMarchableAtlasCoverage() {
        var atlas = Generate(bytes: SyntheticCffFont.Build(cff2: true));

        AssertCubicGlyph(atlas: atlas);
    }
    [Fact]
    public void CollectionFaceIndexSelectsTheRequestedFace() {
        var collection = SyntheticOpenTypeCollection.Build(
            SyntheticTrueTypeFont.Build(advanceWidth: 1000),
            SyntheticTrueTypeFont.Build(advanceWidth: 750)
        );

        var first = Generate(bytes: collection, faceIndex: 0);
        var second = Generate(bytes: collection, faceIndex: 1);

        Assert.True(first.TryGetGlyph(unicode: 'A', glyph: out var firstGlyph));
        Assert.True(second.TryGetGlyph(unicode: 'A', glyph: out var secondGlyph));
        Assert.Equal(1f, firstGlyph.Advance, precision: 5);
        Assert.Equal(0.75f, secondGlyph.Advance, precision: 5);
    }
    [Fact]
    public void CollectionFaceIndexRefusesOutOfRangeAndStandaloneSelections() {
        var collection = SyntheticOpenTypeCollection.Build(SyntheticTrueTypeFont.Build());

        var collectionError = Assert.Throws<ArgumentException>(() => Generate(bytes: collection, faceIndex: 1));
        var standaloneError = Assert.Throws<ArgumentException>(() => Generate(bytes: SyntheticTrueTypeFont.Build(), faceIndex: 1));

        Assert.Contains(expectedSubstring: "face index 1 is out of range", actualString: collectionError.Message, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "not a collection", actualString: standaloneError.Message, comparisonType: StringComparison.Ordinal);
    }

    private static void AssertCubicGlyph(FontAtlas atlas) {
        Assert.True(atlas.TryGetGlyph(unicode: 'A', glyph: out var glyph));
        Assert.NotNull(glyph.AtlasBounds);
        Assert.NotNull(atlas.ImageData);
        Assert.True(atlas.TryGetGlyphById(glyphId: 1, glyph: out var glyphById));
        Assert.Same(expected: glyph, actual: glyphById);

        var bounds = glyph.AtlasBounds.Value;
        var centerX = ((int)((bounds.Left + bounds.Right) * 0.5f));
        var centerY = ((int)((bounds.Top + bounds.Bottom) * 0.5f));
        var centerAlpha = atlas.ImageData.RgbaPixels[((((centerY * atlas.Width) + centerX) * 4) + 3)];

        Assert.True((centerAlpha > 127));
    }
}

internal static class SyntheticCffFont {
    public static byte[] Build(bool cff2) {
        var charString = BuildRoundedCharString(cff2: cff2);
        var cff = (cff2 ? BuildCff2(charString: charString) : BuildCff1(charString: charString));
        var head = new byte[54];

        BinaryPrimitives.WriteUInt16BigEndian(destination: head.AsSpan(start: 18), value: 1000);
        var hhea = new byte[36];

        BinaryPrimitives.WriteInt16BigEndian(destination: hhea.AsSpan(start: 4), value: 800);
        BinaryPrimitives.WriteInt16BigEndian(destination: hhea.AsSpan(start: 6), value: -200);
        BinaryPrimitives.WriteUInt16BigEndian(destination: hhea.AsSpan(start: 34), value: 2);
        var hmtx = new byte[8];

        BinaryPrimitives.WriteUInt16BigEndian(destination: hmtx.AsSpan(start: 0), value: 1000);
        BinaryPrimitives.WriteUInt16BigEndian(destination: hmtx.AsSpan(start: 4), value: 1000);
        var maxp = new byte[6];

        BinaryPrimitives.WriteUInt32BigEndian(destination: maxp, value: (cff2 ? 0x00005000u : 0x00010000u));
        BinaryPrimitives.WriteUInt16BigEndian(destination: maxp.AsSpan(start: 4), value: 2);

        return AssembleSfnt(
            cff2: cff2,
            tables: [
                ((cff2 ? "CFF2" : "CFF "), cff),
                ("cmap", BuildCmap()),
                ("head", head),
                ("hhea", hhea),
                ("hmtx", hmtx),
                ("maxp", maxp),
            ]
        );
    }

    private static void AppendDictInteger(List<byte> output, int value) {
        output.Add(item: 29);
        AppendUInt32(output: output, value: unchecked((uint)value));
    }
    private static void AppendIndex(List<byte> output, bool cff2, params byte[][] objects) {
        if (cff2) {
            AppendUInt32(output: output, value: checked((uint)objects.Length));
        } else {
            AppendUInt16(output: output, value: checked((ushort)objects.Length));
        }

        if (objects.Length == 0) {
            return;
        }

        output.Add(item: 2);
        var offset = 1;

        foreach (var bytes in objects) {
            AppendUInt16(output: output, value: checked((ushort)offset));
            offset = checked((offset + bytes.Length));
        }

        AppendUInt16(output: output, value: checked((ushort)offset));

        foreach (var bytes in objects) {
            output.AddRange(collection: bytes);
        }
    }
    private static void AppendNumber(List<byte> output, short value) {
        output.Add(item: 28);
        AppendUInt16(output: output, value: unchecked((ushort)value));
    }
    private static void AppendUInt16(List<byte> output, ushort value) {
        output.Add(item: ((byte)(value >> 8)));
        output.Add(item: ((byte)value));
    }
    private static void AppendUInt32(List<byte> output, uint value) {
        output.Add(item: ((byte)(value >> 24)));
        output.Add(item: ((byte)(value >> 16)));
        output.Add(item: ((byte)(value >> 8)));
        output.Add(item: ((byte)value));
    }
    private static byte[] AssembleSfnt(bool cff2, IReadOnlyList<(string Tag, byte[] Bytes)> tables) {
        var output = new List<byte>();

        AppendUInt32(output: output, value: (cff2 ? 0x4F54544Fu : 0x4F54544Fu));
        AppendUInt16(output: output, value: checked((ushort)tables.Count));
        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: 0);
        var dataOffset = checked((12 + (tables.Count * 16)));
        var data = new List<byte>();

        foreach (var (tag, bytes) in tables) {
            foreach (var character in tag) {
                output.Add(item: ((byte)character));
            }

            AppendUInt32(output: output, value: 0);
            AppendUInt32(output: output, value: checked((uint)(dataOffset + data.Count)));
            AppendUInt32(output: output, value: checked((uint)bytes.Length));
            data.AddRange(collection: bytes);

            while ((data.Count % 4) != 0) {
                data.Add(item: 0);
            }
        }

        output.AddRange(collection: data);
        return output.ToArray();
    }
    private static byte[] BuildCff1(byte[] charString) {
        var output = new List<byte> { 1, 0, 4, 4 };

        AppendIndex(output: output, cff2: false, "Test"u8.ToArray());
        var localSubroutine = charString[7..^1].Append(element: ((byte)11)).ToArray();
        byte[] globalSubroutine = [32, 10, 11];
        var globalSubroutines = new List<byte>();
        var localSubroutines = new List<byte>();
        var privateDictionary = new List<byte>();

        AppendIndex(output: globalSubroutines, cff2: false, globalSubroutine);
        AppendIndex(output: localSubroutines, cff2: false, localSubroutine);
        AppendDictInteger(output: privateDictionary, value: 6);
        privateDictionary.Add(item: 19);
        var mainCharString = charString[..7].Concat(second: new byte[] { 32, 29, 14 }).ToArray();
        var topDictionary = new List<byte>();
        const int TopIndexSize = 24;
        var privateOffset = checked((((output.Count + TopIndexSize) + 2) + globalSubroutines.Count));
        var charStringsOffset = checked(((privateOffset + privateDictionary.Count) + localSubroutines.Count));

        AppendDictInteger(output: topDictionary, value: charStringsOffset);
        topDictionary.Add(item: 17);
        AppendDictInteger(output: topDictionary, value: privateDictionary.Count);
        AppendDictInteger(output: topDictionary, value: privateOffset);
        topDictionary.Add(item: 18);
        AppendIndex(output: output, cff2: false, topDictionary.ToArray());
        AppendIndex(output: output, cff2: false);
        output.AddRange(collection: globalSubroutines);
        output.AddRange(collection: privateDictionary);
        output.AddRange(collection: localSubroutines);
        AppendIndex(output: output, cff2: false, [14], mainCharString);
        return output.ToArray();
    }
    private static byte[] BuildCff2(byte[] charString) {
        const int HeaderSize = 5;
        const int TopDictionarySize = 19;
        const int GlobalSubroutinesOffset = (HeaderSize + TopDictionarySize);
        const int VariationStoreOffset = (GlobalSubroutinesOffset + 4);
        const int FontDictionariesOffset = (VariationStoreOffset + 32);
        const int CharStringsOffset = (FontDictionariesOffset + 9);
        var output = new List<byte> { 2, 0, HeaderSize };

        AppendUInt16(output: output, value: TopDictionarySize);
        AppendDictInteger(output: output, value: CharStringsOffset);
        output.Add(item: 17);
        AppendDictInteger(output: output, value: VariationStoreOffset);
        output.Add(item: 24);
        AppendDictInteger(output: output, value: FontDictionariesOffset);
        output.Add(item: 12);
        output.Add(item: 36);
        AppendIndex(output: output, cff2: true);
        // VariationStore: one ItemVariationData row with one positive-axis region. At the default design the
        // region scalar is zero, so the charstring's default operand must survive unchanged.
        AppendUInt16(output: output, value: 30);
        AppendUInt16(output: output, value: 1);
        AppendUInt32(output: output, value: 20);
        AppendUInt16(output: output, value: 1);
        AppendUInt32(output: output, value: 12);
        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: 1);
        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: 1);
        AppendUInt16(output: output, value: 1);
        AppendUInt16(output: output, value: 0);
        AppendUInt16(output: output, value: 16_384);
        AppendUInt16(output: output, value: 16_384);
        AppendIndex(output: output, cff2: true, Array.Empty<byte>());
        AppendIndex(output: output, cff2: true, [], charString);
        return output.ToArray();
    }
    private static byte[] BuildCmap() {
        var subtable = new List<byte>();

        AppendUInt16(output: subtable, value: 4);
        AppendUInt16(output: subtable, value: 32);
        AppendUInt16(output: subtable, value: 0);
        AppendUInt16(output: subtable, value: 4);
        AppendUInt16(output: subtable, value: 4);
        AppendUInt16(output: subtable, value: 1);
        AppendUInt16(output: subtable, value: 2);
        AppendUInt16(output: subtable, value: 'A');
        AppendUInt16(output: subtable, value: ushort.MaxValue);
        AppendUInt16(output: subtable, value: 0);
        AppendUInt16(output: subtable, value: 'A');
        AppendUInt16(output: subtable, value: ushort.MaxValue);
        AppendUInt16(output: subtable, value: unchecked((ushort)(1 - 'A')));
        AppendUInt16(output: subtable, value: 1);
        AppendUInt16(output: subtable, value: 0);
        AppendUInt16(output: subtable, value: 0);
        var cmap = new List<byte>();

        AppendUInt16(output: cmap, value: 0);
        AppendUInt16(output: cmap, value: 1);
        AppendUInt16(output: cmap, value: 3);
        AppendUInt16(output: cmap, value: 1);
        AppendUInt32(output: cmap, value: 12);
        cmap.AddRange(collection: subtable);
        return cmap.ToArray();
    }
    private static byte[] BuildRoundedCharString(bool cff2) {
        var output = new List<byte>();

        AppendNumber(output: output, value: 500);

        if (cff2) {
            AppendNumber(output: output, value: 200);
            AppendNumber(output: output, value: 100);
            AppendNumber(output: output, value: 1);
            output.Add(item: 16);
        } else {
            AppendNumber(output: output, value: 200);
        }
        output.Add(item: 21);
        short[] curveDeltas = [
            165, 0, 135, 135, 0, 165,
            0, 165, -135, 135, -165, 0,
            -165, 0, -135, -135, 0, -165,
            0, -165, 135, -135, 165, 0,
        ];

        foreach (var value in curveDeltas) {
            AppendNumber(output: output, value: value);
        }

        output.Add(item: 8);

        if (!cff2) {
            output.Add(item: 14);
        }

        return output.ToArray();
    }
}
internal static class SyntheticOpenTypeCollection {
    public static byte[] Build(params byte[][] faces) {
        var headerLength = checked((12 + (faces.Length * 4)));
        var offsets = new int[faces.Length];
        var length = headerLength;

        for (var index = 0; (index < faces.Length); index++) {
            offsets[index] = length;
            length = checked((length + faces[index].Length));
            length = checked((length + 3) & ~3);
        }

        var output = new byte[length];

        "ttcf"u8.CopyTo(destination: output);
        BinaryPrimitives.WriteUInt32BigEndian(destination: output.AsSpan(start: 4), value: 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(destination: output.AsSpan(start: 8), value: checked((uint)faces.Length));

        for (var index = 0; (index < faces.Length); index++) {
            BinaryPrimitives.WriteUInt32BigEndian(destination: output.AsSpan(start: checked((12 + (index * 4)))), value: checked((uint)offsets[index]));
            faces[index].CopyTo(array: output, index: offsets[index]);
            RebaseTableOffsets(face: output.AsSpan(start: offsets[index], length: faces[index].Length), faceOffset: offsets[index]);
        }

        return output;
    }

    private static void RebaseTableOffsets(Span<byte> face, int faceOffset) {
        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(source: face.Slice(start: 4, length: 2));

        for (var index = 0; (index < tableCount); index++) {
            var offsetPosition = checked((20 + (index * 16)));
            var oldOffset = BinaryPrimitives.ReadUInt32BigEndian(source: face.Slice(start: offsetPosition, length: 4));

            BinaryPrimitives.WriteUInt32BigEndian(
                destination: face.Slice(start: offsetPosition, length: 4),
                value: checked((oldOffset + ((uint)faceOffset)))
            );
        }
    }
}
