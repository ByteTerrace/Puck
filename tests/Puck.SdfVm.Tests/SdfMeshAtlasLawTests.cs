using Puck.Abstractions.Gpu;
using Puck.SignedDistance.Baking;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: the mesh atlases hold every packed texture set's stored bytes unchanged. Each set takes a rectangle whose
/// origin and extent are multiples of <see cref="SdfMeshAtlas.Alignment"/> texels, the same in all five atlases, so at
/// every level of the chain its units (a block of a block-compressed format, a texel of the material's R8) sit in the
/// atlas exactly as they sit in its own texture, at the rectangle's origin shifted by the level; every other unit is zero;
/// no two rectangles overlap; and a set's placement moves its texture coordinates onto its rectangle. A set listed twice
/// is packed once, and sets of different tiles or level counts, or more than the atlas extent, are refused by name.
/// </summary>
public sealed class SdfMeshAtlasLawTests {
    // A texture set of the given extent in four-texel tiles over three levels, each usage's every unit filled with a
    // byte pattern that names the set, the usage, the level and the unit, never zero.
    private static SdfMeshTextures Set(int width, int height, byte seed) =>
        new(textures: SdfMeshTextures.Usages.Select((usage, index) => {
            var format = SdfMeshTextures.FormatOf(usage: usage);

            return new SdfBakedTexture(
                ColorSpace: SdfBakedTexture.PlanFor(usage: usage).ColorSpace,
                Format: format,
                Height: height,
                Levels: [.. Enumerable.Range(start: 0, count: 3).Select(selector: level => {
                    var (levelWidth, levelHeight) = GpuPixelFormats.LevelExtent(height: (uint)height, level: (uint)level, width: (uint)width);
                    var bytes = new byte[GpuPixelFormats.LevelByteLength(format: format, height: levelHeight, width: levelWidth)];

                    for (var at = 0; (at < bytes.Length); at++) {
                        bytes[at] = (byte)(1 + (((((seed * 31) + (index * 7)) + (level * 3)) + at) % 250));
                    }

                    return bytes;
                })],
                TileTexels: 4,
                Usage: usage,
                Width: width
            );
        }).ToArray());

    [Fact]
    public void EverySetsUnitsSitInItsAlignedRectangleUnchangedAndEveryOtherUnitIsZero() {
        SdfMeshTextures[] sets = [Set(width: 28, height: 12, seed: 1), Set(width: 64, height: 40, seed: 2), Set(width: 4, height: 4, seed: 3), Set(width: 20, height: 36, seed: 4)];
        var atlas = SdfMeshAtlas.Pack(textures: [.. sets, sets[0]]);

        Assert.Equal(expected: sets.Length, actual: atlas.MeshCount);
        Assert.Equal(expected: 3, actual: atlas.Levels);
        Assert.Equal(expected: 0, actual: (atlas.Width % SdfMeshAtlas.Alignment));
        Assert.Equal(expected: 0, actual: (atlas.Height % SdfMeshAtlas.Alignment));

        var rectangles = new List<(int X, int Y, int Width, int Height)>();

        foreach (var set in sets) {
            var placement = atlas.Placement(textures: set);
            var x = (int)MathF.Round(x: (placement.Z * atlas.Width));
            var y = (int)MathF.Round(x: (placement.W * atlas.Height));

            Assert.Equal(expected: (0, 0), actual: ((x % SdfMeshAtlas.Alignment), (y % SdfMeshAtlas.Alignment)));
            Assert.Equal(expected: ((float)set.Width / atlas.Width), actual: placement.X);
            Assert.Equal(expected: ((float)set.Height / atlas.Height), actual: placement.Y);
            rectangles.Add(item: (x, y, (((set.Width + 15) / 16) * 16), (((set.Height + 15) / 16) * 16)));
        }

        for (var left = 0; (left < rectangles.Count); left++) {
            for (var right = (left + 1); (right < rectangles.Count); right++) {
                var (a, b) = (rectangles[left], rectangles[right]);

                Assert.False(condition: ((a.X < (b.X + b.Width)) && (b.X < (a.X + a.Width)) && (a.Y < (b.Y + b.Height)) && (b.Y < (a.Y + a.Height))), userMessage: $"rectangles {left} and {right} overlap");
            }
        }

        for (var usage = 0; (usage < SdfMeshTextures.Usages.Count); usage++) {
            var format = SdfMeshTextures.FormatOf(usage: SdfMeshTextures.Usages[usage]);
            var unitTexels = (GpuPixelFormats.IsBlockCompressed(format: format) ? 4 : 1);
            var unit = (int)GpuPixelFormats.UnitBytes(format: format);
            var chain = atlas.Chains[usage];
            var levelStart = 0;

            Assert.Equal(expected: (long)GpuPixelFormats.ChainByteLength(format: format, height: (uint)atlas.Height, levels: 3U, width: (uint)atlas.Width), actual: chain.LongLength);

            for (var level = 0; (level < 3); level++) {
                var (levelWidth, levelHeight) = GpuPixelFormats.LevelExtent(height: (uint)atlas.Height, level: (uint)level, width: (uint)atlas.Width);
                var columns = (int)((levelWidth + (uint)(unitTexels - 1)) / (uint)unitTexels);
                var rows = (int)((levelHeight + (uint)(unitTexels - 1)) / (uint)unitTexels);
                var covered = new bool[(columns * rows)];

                for (var index = 0; (index < sets.Length); index++) {
                    var texture = sets[index].Textures[usage];
                    var (sourceWidth, sourceHeight) = GpuPixelFormats.LevelExtent(height: (uint)texture.Height, level: (uint)level, width: (uint)texture.Width);
                    var sourceColumns = (int)((sourceWidth + (uint)(unitTexels - 1)) / (uint)unitTexels);
                    var sourceRows = (int)((sourceHeight + (uint)(unitTexels - 1)) / (uint)unitTexels);
                    var originColumn = ((rectangles[index].X >> level) / unitTexels);
                    var originRow = ((rectangles[index].Y >> level) / unitTexels);

                    for (var row = 0; (row < sourceRows); row++) {
                        for (var column = 0; (column < sourceColumns); column++) {
                            var target = (((originRow + row) * columns) + (originColumn + column));

                            covered[target] = true;
                            Assert.True(
                                condition: chain.AsSpan(length: unit, start: (levelStart + (target * unit))).SequenceEqual(other: texture.Levels[level].AsSpan(length: unit, start: (((row * sourceColumns) + column) * unit))),
                                userMessage: $"usage {SdfMeshTextures.Usages[usage]} level {level}: set {index}'s unit ({column}, {row}) moved"
                            );
                        }
                    }
                }
                for (var target = 0; (target < covered.Length); target++) {
                    if (!covered[target]) {
                        Assert.True(condition: chain.AsSpan(length: unit, start: (levelStart + (target * unit))).IndexOfAnyExcept(value: (byte)0) < 0, userMessage: $"usage {SdfMeshTextures.Usages[usage]} level {level}: unit {target} lies outside every rectangle and is not zero");
                    }
                }

                levelStart += (columns * rows * unit);
            }
        }
    }
    [Fact]
    public void SetsOfOtherTilesOrLevelsAnEmptyListAndAnOversizedPackAreRefusedByName() {
        var set = Set(width: 8, height: 8, seed: 1);
        var shallow = new SdfMeshTextures(textures: [.. set.Textures.Select(selector: static texture => (texture with { Levels = [texture.Levels[0], texture.Levels[1]] }))]);

        Assert.Equal(expected: "textures", actual: Assert.Throws<ArgumentException>(testCode: static () => SdfMeshAtlas.Pack(textures: [])).ParamName);
        Assert.Contains(expectedSubstring: "tile and level count", actualString: Assert.Throws<ArgumentException>(testCode: () => SdfMeshAtlas.Pack(textures: [set, shallow])).Message);
        Assert.Contains(expectedSubstring: $"at most {SdfMeshAtlas.MaxExtent} texels", actualString: Assert.Throws<ArgumentException>(testCode: static () => SdfMeshAtlas.Pack(textures: [Set(width: (SdfMeshAtlas.MaxExtent + 4), height: 4, seed: 1)])).Message);
    }
    [Fact]
    public void ASetMissingAUsageOrOfTheWrongFormatIsRefusedByName() {
        var set = Set(width: 8, height: 8, seed: 1);

        Assert.Contains(expectedSubstring: "lack its Emission texture", actualString: Assert.Throws<ArgumentException>(testCode: () => new SdfMeshTextures(textures: [.. set.Textures.Take(count: 4)])).Message);
        Assert.Contains(expectedSubstring: "is stored as", actualString: Assert.Throws<ArgumentException>(testCode: () => new SdfMeshTextures(textures: [(set.Textures[0] with { Format = GpuPixelFormat.R8Unorm }), .. set.Textures.Skip(count: 1)])).Message);
        Assert.Contains(expectedSubstring: "repeated", actualString: Assert.Throws<ArgumentException>(testCode: () => new SdfMeshTextures(textures: [.. set.Textures, set.Textures[0]])).Message);
    }
}
