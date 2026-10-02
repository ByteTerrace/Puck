using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Assets.Textures;
using Puck.SignedDistance;
using Puck.SignedDistance.Baking;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: an impostor reaches the mesh pass as the bake made it. A baked impostor becomes five textures, one of each of
/// albedo, normal, depth, material and emission, each stored in the format its usage declares (BC7 in sRGB, BC5, BC4, R8,
/// BC6H) as
/// tile-aware chains of the view grid's extent, and a sphere the draw places and a view grid it searches. A set of
/// another kind, a grid the textures do not fill, or a sphere that cannot be drawn is refused by name. The impostor atlases
/// pack impostors as the mesh atlases pack meshes' textures, aligned to whole blocks at the last level of the chain, and
/// keep their bytes; a mesh's textures and an impostor never share an atlas. A card draw's record in the mesh region carries
/// the impostor flag, the sphere, the grid and the atlas rectangle, and every other draw's carries none of them.
/// </summary>
public sealed class SdfMeshImpostorLawTests {
    private static readonly SdfMaterial[] Materials = [new(Albedo: Vector3.One), new(Albedo: new Vector3(x: 0.8f, y: 0.2f, z: 0.1f))];

    private static SdfBake Bake(float radius) {
        var builder = new SdfProgramBuilder();

        foreach (var material in Materials) {
            _ = builder.AddMaterial(material: material);
        }
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 1f, y: 2f, z: 3f)).Sphere(material: 1, radius: radius);

        return SdfBaker.Bake(
            center: new Vector3(x: 1f, y: 2f, z: 3f),
            materials: Materials,
            program: builder.Build(buildInstanceGrid: false),
            reach: (radius + 0.05f),
            tier: SdfBakeTier.For(quality: SdfBakeQuality.Preview)
        );
    }

    [Fact]
    public void AnImpostorHoldsTheBakesFiveTexturesInTheirDeclaredFormats() {
        var bake = Bake(radius: 0.5f);
        var impostor = new SdfMeshImpostor(impostor: bake.Impostor);

        Assert.Equal(expected: [SdfBakeTextureUsage.Albedo, SdfBakeTextureUsage.Normal, SdfBakeTextureUsage.Depth, SdfBakeTextureUsage.Material, SdfBakeTextureUsage.Emission], actual: impostor.UsageOrder);
        Assert.Equal(expected: [GpuPixelFormat.Bc7Unorm, GpuPixelFormat.Bc5Unorm, GpuPixelFormat.Bc4Unorm, GpuPixelFormat.R8Unorm, GpuPixelFormat.Bc6hUfloat], actual: impostor.Textures.Select(selector: static texture => texture.Format));
        Assert.Equal(expected: [TextureColorSpace.Srgb, TextureColorSpace.Linear, TextureColorSpace.Linear, TextureColorSpace.Linear, TextureColorSpace.Linear], actual: impostor.Textures.Select(selector: static texture => texture.ColorSpace));
        Assert.Equal(expected: (bake.Impostor.Center, bake.Impostor.Radius, bake.Impostor.Views, bake.Impostor.ViewTexels), actual: (impostor.Center, impostor.Radius, impostor.Views, impostor.ViewTexels));
        Assert.Equal(expected: (impostor.Views * impostor.ViewTexels), actual: impostor.Width);
        Assert.Equal(expected: impostor.ViewTexels, actual: impostor.TileTexels);
        Assert.Equal(expected: TextureMipChain.LevelCount(tileTexels: impostor.ViewTexels), actual: impostor.Levels);
    }
    [Fact]
    public void AnImpostorThatCannotBeDrawnIsRefusedByName() {
        var baked = Bake(radius: 0.5f).Impostor;

        Assert.Equal(expected: "impostor", actual: Assert.Throws<ArgumentNullException>(testCode: static () => new SdfMeshImpostor(impostor: null!)).ParamName);
        Assert.Contains(expectedSubstring: "positive finite radius", actualString: Assert.Throws<ArgumentException>(testCode: () => new SdfMeshImpostor(impostor: (baked with { Radius = 0f }))).Message);
        Assert.Contains(expectedSubstring: "finite center", actualString: Assert.Throws<ArgumentException>(testCode: () => new SdfMeshImpostor(impostor: (baked with { Center = new Vector3(x: float.NaN, y: 0f, z: 0f) }))).Message);
        Assert.Contains(expectedSubstring: "square grid", actualString: Assert.Throws<ArgumentException>(testCode: () => new SdfMeshImpostor(impostor: (baked with { Views = (baked.Views + 1) }))).Message);
        Assert.Contains(expectedSubstring: "repeated", actualString: Assert.Throws<ArgumentException>(testCode: () => new SdfMeshImpostor(impostor: (baked with { Depth = baked.Albedo }))).Message);
    }
    [Fact]
    public void TheImpostorAtlasesKeepEachImpostorsBytesInAnAlignedRectangle() {
        var small = new SdfMeshImpostor(impostor: Bake(radius: 0.3f).Impostor);
        var large = new SdfMeshImpostor(impostor: Bake(radius: 0.5f).Impostor);
        var atlas = SdfMeshAtlas.Pack(textures: [small, large, small]);
        var alignment = SdfMeshAtlas.AlignmentOf(levels: atlas.Levels);

        Assert.Contains(collection: small.Textures[0].Decode(level: 0).Where(predicate: static (_, index) => ((index % 4) == 3)), filter: static alpha => (alpha > 0));
        Assert.Contains(expected: ((byte)1), collection: small.Textures.Single(predicate: static texture => (texture.Usage == SdfBakeTextureUsage.Material)).Levels[0]);
        Assert.Equal(expected: 2, actual: atlas.MeshCount);
        Assert.Equal(expected: (small.Levels, 4), actual: (atlas.Levels, small.Levels));
        Assert.Equal(actual: alignment, expected: 32);
        Assert.Equal(expected: 0, actual: (atlas.Width % alignment));
        Assert.Equal(expected: 0, actual: (atlas.Height % alignment));

        foreach (var impostor in new[] { small, large }) {
            var placement = atlas.Placement(textures: impostor);

            var (x, y) = (((int)MathF.Round(x: (placement.Z * atlas.Width))), ((int)MathF.Round(x: (placement.W * atlas.Height))));

            Assert.Equal(actual: ((x % alignment), (y % alignment)), expected: (0, 0));
            Assert.Equal(expected: (((float)impostor.Width) / atlas.Width), actual: placement.X);

            for (var usage = 0; (usage < impostor.Textures.Count); usage++) {
                var format = SdfTextureSet.FormatOf(usage: impostor.UsageOrder[usage]);
                var unit = ((int)GpuPixelFormats.UnitBytes(format: format));
                var unitTexels = (GpuPixelFormats.IsBlockCompressed(format: format) ? 4 : 1);
                var atlasOffset = 0;

                for (var level = 0; (level < atlas.Levels); level++) {
                    var source = impostor.Textures[usage].Levels[level];
                    var columns = ((((impostor.Width >> level) + unitTexels) - 1) / unitTexels);
                    var rows = ((((impostor.Height >> level) + unitTexels) - 1) / unitTexels);
                    var atlasColumns = ((((atlas.Width >> level) + unitTexels) - 1) / unitTexels);

                    for (var row = 0; (row < rows); row++) {
                        var target = (atlasOffset + ((((((y >> level) / unitTexels) + row) * atlasColumns) + ((x >> level) / unitTexels)) * unit));

                        Assert.True(condition: atlas.Chains[usage].AsSpan(length: (columns * unit), start: target).SequenceEqual(other: source.AsSpan(length: (columns * unit), start: ((row * columns) * unit))), userMessage: $"usage {usage} level {level} row {row} moved");
                    }
                    atlasOffset += ((int)GpuPixelFormats.LevelByteLength(format: format, height: ((uint)(atlas.Height >> level)), width: ((uint)(atlas.Width >> level))));
                }
            }
        }
    }
    [Fact]
    public void AMeshesTexturesAndAnImpostorNeverSharePackedAtlases() {
        var bake = Bake(radius: 0.5f);
        var meshTextures = new SdfMeshTextures(textures: bake.Textures);
        var impostor = new SdfMeshImpostor(impostor: bake.Impostor);

        Assert.Contains(expectedSubstring: "all of one kind", actualString: Assert.Throws<ArgumentException>(testCode: () => SdfMeshAtlas.Pack(textures: [meshTextures, impostor])).Message);
        Assert.False(condition: SdfMeshAtlas.Pack(textures: [meshTextures]).Holds(textures: impostor));
    }
    [Fact]
    public void ACardsRecordCarriesItsImpostorAndNoOtherDrawsDoes() {
        var bake = Bake(radius: 0.5f);
        var impostor = new SdfMeshImpostor(impostor: bake.Impostor);
        var meshTextures = new SdfMeshTextures(textures: bake.Textures);
        var plain = new SdfMesh(indices: new uint[] { 0, 1, 2 }, positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY });
        var matrix = (Matrix4x4.CreateScale(scale: 2f) * Matrix4x4.CreateTranslation(position: new Vector3(x: 5f, y: 0f, z: 0f)));
        SdfMeshDraw[] draws = [
            new(Identity: "plain", Material: 3, Mesh: plain, ObjectToWorld: Matrix4x4.Identity),
            new(Identity: "card", Material: 4, Mesh: SdfMeshCard.Mesh, ObjectToWorld: matrix) { Impostor = impostor, Lod = SdfMeshLod.ForImpostor(far: true, impostor: impostor) },
        ];
        var meshes = new Dictionary<SdfMesh, SdfMeshRegionMesh>(comparer: ReferenceEqualityComparer.Instance);
        var layout = SdfMeshRegion.Plan(draws: draws, meshes: meshes);
        var atlas = SdfMeshAtlas.Pack(textures: [impostor]);
        var words = new uint[layout.Words];

        SdfMeshRegion.Write(destination: words, draws: draws, impostors: atlas, layout: layout, meshes: meshes);

        Assert.Equal(actual: SdfMeshRegion.DrawWords, expected: 41);

        var first = words.AsSpan(length: SdfMeshRegion.DrawWords, start: 0);
        var second = words.AsSpan(length: SdfMeshRegion.DrawWords, start: SdfMeshRegion.DrawWords);
        var placement = atlas.Placement(textures: impostor);

        Assert.Equal(expected: 0u, actual: first[20] & SdfMeshRegion.ImpostorFlag);
        Assert.True(condition: first[31..41].ToArray().All(predicate: static word => (word == 0u)));
        Assert.Equal(expected: SdfMeshRegion.ImpostorFlag, actual: second[20] & SdfMeshRegion.ImpostorFlag);
        Assert.Equal(expected: [impostor.Center.X, impostor.Center.Y, impostor.Center.Z, impostor.Radius], actual: second[31..35].ToArray().Select(selector: static word => BitConverter.UInt32BitsToSingle(value: word)));
        Assert.Equal(expected: [((uint)impostor.Views), ((uint)impostor.ViewTexels)], actual: second[35..37].ToArray());
        Assert.Equal(expected: [placement.X, placement.Y, placement.Z, placement.W], actual: second[37..41].ToArray().Select(selector: static word => BitConverter.UInt32BitsToSingle(value: word)));
        Assert.Equal(expected: 0u, actual: second[20] & SdfMeshRegion.TexturesFlag);

        // An atlas that does not hold the impostor leaves the card unflagged, so the mesh pass draws nothing for it.
        var unpacked = new uint[layout.Words];

        SdfMeshRegion.Write(destination: unpacked, draws: draws, impostors: SdfMeshAtlas.Pack(textures: [meshTextures]), layout: layout, meshes: meshes);
        Assert.Equal(expected: 0u, actual: unpacked[(SdfMeshRegion.DrawWords + 20)] & SdfMeshRegion.ImpostorFlag);
        Assert.True(condition: unpacked.AsSpan(length: 10, start: (SdfMeshRegion.DrawWords + 31)).ToArray().All(predicate: static word => (word == 0u)));
    }
}
