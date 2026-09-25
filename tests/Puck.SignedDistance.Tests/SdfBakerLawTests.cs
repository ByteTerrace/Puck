using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Sources;
using Puck.Assets.Textures;
using Puck.SignedDistance.Baking;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// Laws over <see cref="SdfBaker"/>: a bake is a function of its program and tier, byte for byte; its mesh stays within
/// half a lattice cell of the field it was baked from; every quad owns one block-aligned tile whose corners sit on
/// texel centers; triangles face out of the surface; every texture is a tile-aware mip chain in its usage's format, and
/// a tile of one material stores that material's albedo and emission exactly; the impostor sees the silhouette; and a
/// program the fixed-point interpreter refuses, or one with no shape in its field, has no bake. The scenes cover a
/// sphere, a sharp box, a torus's hole, a carve, and a smooth-blended figure with thin limbs.
/// </summary>
public sealed class SdfBakerLawTests {
    // The stated bound: the largest field magnitude at any vertex, edge midpoint or triangle centroid, in lattice cells.
    private const double MaximumErrorCells = 0.5;

    private static readonly SdfMaterial[] Materials = [
        new(Albedo: new Vector3(x: 0.8f, y: 0.2f, z: 0.1f)),
        new(Albedo: new Vector3(x: 0.1f, y: 0.5f, z: 0.9f), Emissive: 3f),
    ];

    public static TheoryData<string> SceneNames => [.. Scenes().Select(selector: static scene => scene.Name)];

    private static SdfProgram Make(Action<SdfProgramBuilder> emit) {
        var builder = new SdfProgramBuilder();

        foreach (var material in Materials) {
            _ = builder.AddMaterial(material: material);
        }

        emit(obj: builder);

        return builder.Build(buildInstanceGrid: false);
    }
    private static (string Name, SdfProgram Program, float Reach)[] Scenes() => [
        ("sphere", Make(emit: static builder => builder.ResetPoint().Sphere(material: 0, radius: 0.8f)), 0.85f),
        ("box", Make(emit: static builder => builder.ResetPoint().Box(halfExtents: new Vector3(x: 0.6f, y: 0.4f, z: 0.5f), material: 0, round: 0f)), 0.9f),
        ("torus", Make(emit: static builder => builder.ResetPoint().Torus(majorRadius: 0.6f, material: 1, minorRadius: 0.15f)), 0.8f),
        ("carved", Make(emit: static builder => {
            _ = builder.ResetPoint().Box(halfExtents: new Vector3(value: 0.6f), material: 0, round: 0.02f);
            _ = builder.ResetPoint().Translate(offset: new Vector3(value: 0.3f)).Sphere(blend: SdfBlendOp.Subtraction, material: 1, radius: 0.5f);
        }), 1.1f),
        ("figure", Make(emit: static builder => {
            _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0.5f, z: 0f)).Sphere(material: 0, radius: 0.25f);
            _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: -0.3f, z: 0f)).Capsule(blend: SdfBlendOp.SmoothUnion, endpoint: new Vector3(x: 0f, y: 0.6f, z: 0f), material: 1, radius: 0.2f, smooth: 0.1f);
            _ = builder.ResetPoint().Translate(offset: new Vector3(x: -0.1f, y: -0.3f, z: 0f)).Capsule(blend: SdfBlendOp.SmoothUnion, endpoint: new Vector3(x: -0.1f, y: -0.5f, z: 0f), material: 1, radius: 0.08f, smooth: 0.05f);
            _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0.1f, y: -0.3f, z: 0f)).Capsule(blend: SdfBlendOp.SmoothUnion, endpoint: new Vector3(x: 0.1f, y: -0.5f, z: 0f), material: 1, radius: 0.08f, smooth: 0.05f);
        }), 1f),
    ];
    private static (SdfProgram Program, float Reach) Scene(string name) {
        var scene = Scenes().Single(predicate: scene => (scene.Name == name));

        return (scene.Program, scene.Reach);
    }
    private static SdfBake Bake(string name, SdfBakeQuality quality) {
        var (program, reach) = Scene(name: name);

        return SdfBaker.Bake(
            center: Vector3.Zero,
            materials: Materials,
            program: program,
            reach: reach,
            tier: SdfBakeTier.For(quality: quality)
        );
    }
    private static void AssertSameTexture(SdfBakedTexture expected, SdfBakedTexture actual) {
        Assert.Equal(expected: (expected.Usage, expected.Format, expected.ColorSpace, expected.Width, expected.Height, expected.TileTexels), actual: (actual.Usage, actual.Format, actual.ColorSpace, actual.Width, actual.Height, actual.TileTexels));
        Assert.Equal(expected: expected.Levels.Count, actual: actual.Levels.Count);

        for (var level = 0; (level < expected.Levels.Count); level++) {
            Assert.Equal(expected: expected.Levels[level], actual: actual.Levels[level]);
        }
    }
    private static void AssertChain(SdfBakedTexture texture, int tileTexels) {
        var plan = SdfBakedTexture.PlanFor(usage: texture.Usage);

        Assert.Equal(expected: (plan.Stored, plan.ColorSpace, tileTexels), actual: (texture.Format, texture.ColorSpace, texture.TileTexels));
        Assert.Equal(expected: TextureMipChain.LevelCount(tileTexels: tileTexels), actual: texture.Levels.Count);

        for (var level = 0; (level < texture.Levels.Count); level++) {
            var (width, height) = texture.LevelExtent(level: level);

            Assert.Equal(expected: ((texture.Width >> level), (texture.Height >> level)), actual: (width, height));
            Assert.Equal(expected: TextureFormats.LevelBytes(format: texture.Format, height: height, width: width), actual: texture.Levels[level].LongLength);
        }
    }

    [MemberData(memberName: nameof(SceneNames))]
    [Theory]
    public void BakingOneProgramTwiceYieldsTheSameBytes(string scene) {
        var first = Bake(name: scene, quality: SdfBakeQuality.Preview);
        var second = Bake(name: scene, quality: SdfBakeQuality.Preview);

        Assert.Equal(expected: first.Mesh.Vertices, actual: second.Mesh.Vertices);
        Assert.Equal(expected: first.Mesh.Indices, actual: second.Mesh.Indices);
        Assert.Equal(expected: first.Work, actual: second.Work);
        Assert.Equal(expected: first.Textures.Count, actual: second.Textures.Count);

        for (var index = 0; (index < first.Textures.Count); index++) {
            AssertSameTexture(actual: second.Textures[index], expected: first.Textures[index]);
        }

        AssertSameTexture(actual: second.Impostor.Albedo, expected: first.Impostor.Albedo);
        AssertSameTexture(actual: second.Impostor.Normal, expected: first.Impostor.Normal);
        AssertSameTexture(actual: second.Impostor.Depth, expected: first.Impostor.Depth);
        AssertSameTexture(actual: second.Impostor.Emission, expected: first.Impostor.Emission);
    }
    [MemberData(memberName: nameof(SceneNames))]
    [Theory]
    public void EveryTextureIsATileAwareChainAndAOneMaterialTileStoresItsMaterialExactly(string scene) {
        var bake = Bake(name: scene, quality: SdfBakeQuality.Standard);
        var mesh = bake.Mesh;
        const int Tile = SdfBakeTier.TileTexels;

        Assert.Equal(expected: [SdfBakeTextureUsage.Albedo, SdfBakeTextureUsage.Normal, SdfBakeTextureUsage.Occlusion, SdfBakeTextureUsage.Material, SdfBakeTextureUsage.Emission], actual: bake.Textures.Select(selector: static texture => texture.Usage));
        Assert.All(collection: bake.Textures, action: texture => AssertChain(texture: texture, tileTexels: Tile));
        Assert.Equal(expected: [SdfBakeTextureUsage.Albedo, SdfBakeTextureUsage.Normal, SdfBakeTextureUsage.Depth, SdfBakeTextureUsage.Emission], actual: ((SdfBakedTexture[])[bake.Impostor.Albedo, bake.Impostor.Normal, bake.Impostor.Depth, bake.Impostor.Emission]).Select(selector: static texture => texture.Usage));
        AssertChain(texture: bake.Impostor.Albedo, tileTexels: bake.Impostor.ViewTexels);
        AssertChain(texture: bake.Impostor.Normal, tileTexels: bake.Impostor.ViewTexels);
        AssertChain(texture: bake.Impostor.Depth, tileTexels: bake.Impostor.ViewTexels);
        AssertChain(texture: bake.Impostor.Emission, tileTexels: bake.Impostor.ViewTexels);

        var albedo = bake.Textures[0].Decode(level: 0);
        var materials = bake.Textures[3];
        var emission = bake.Textures[4].Decode(level: 0);
        var tiles = 0;

        // Material identity is stored as it was baked, and every mip keeps a material the program has.
        for (var level = 0; (level < materials.Levels.Count); level++) {
            Assert.All(collection: materials.Levels[level], action: id => Assert.InRange(actual: id, high: (Materials.Length - 1), low: 0));
        }

        for (var quad = 0; (quad < mesh.Quads); quad++) {
            var tileX = ((quad % mesh.TileColumns) * Tile);
            var tileY = ((quad / mesh.TileColumns) * Tile);
            var id = materials.Levels[0][((tileY * mesh.AtlasWidth) + tileX)];
            var uniform = true;

            for (var texel = 0; (texel < (Tile * Tile)); texel++) {
                uniform &= (materials.Levels[0][(((tileY + (texel / Tile)) * mesh.AtlasWidth) + (tileX + (texel % Tile)))] == id);
            }

            if (!uniform) {
                continue;
            }

            tiles++;

            var color = Materials[id].Albedo;
            var strength = ((double)Materials[id].Emissive);

            for (var texel = 0; (texel < (Tile * Tile)); texel++) {
                var at = (((tileY + (texel / Tile)) * mesh.AtlasWidth) + (tileX + (texel % Tile)));

                Assert.Equal(
                    expected: [ImageSourceConversion.LinearToSrgb8(value: color.X), ImageSourceConversion.LinearToSrgb8(value: color.Y), ImageSourceConversion.LinearToSrgb8(value: color.Z), 255],
                    actual: albedo.AsSpan(length: 4, start: (at * 4)).ToArray()
                );
                Assert.Equal(
                    expected: [BitConverter.HalfToUInt16Bits(value: ((Half)(color.X * strength))), BitConverter.HalfToUInt16Bits(value: ((Half)(color.Y * strength))), BitConverter.HalfToUInt16Bits(value: ((Half)(color.Z * strength)))],
                    actual: [BinaryPrimitives.ReadUInt16LittleEndian(source: emission.AsSpan(start: (at * 8))), BinaryPrimitives.ReadUInt16LittleEndian(source: emission.AsSpan(start: ((at * 8) + 2))), BinaryPrimitives.ReadUInt16LittleEndian(source: emission.AsSpan(start: ((at * 8) + 4)))]
                );
            }
        }

        Assert.True(condition: (tiles > 0), userMessage: $"{scene}: no tile holds one material");
    }
    [MemberData(memberName: nameof(SceneNames))]
    [Theory]
    public void BakedSilhouettesStayWithinHalfACellOfTheField(string scene) {
        foreach (var quality in ((SdfBakeQuality[])[SdfBakeQuality.Preview, SdfBakeQuality.Standard])) {
            var bake = Bake(name: scene, quality: quality);

            var (error, _) = SdfBaker.MeasureError(mesh: bake.Mesh, program: Scene(name: scene).Program);

            Assert.True(condition: (bake.Mesh.Triangles > 0), userMessage: $"{scene} at {quality} baked no triangles");
            Assert.True(
                condition: (error <= (MaximumErrorCells * bake.Mesh.CellSize)),
                userMessage: $"{scene} at {quality}: the mesh strays {(error / bake.Mesh.CellSize):F3} cells from the field"
            );
        }
    }
    [MemberData(memberName: nameof(SceneNames))]
    [Theory]
    public void EveryQuadOwnsOneBlockAlignedTileAndFacesOut(string scene) {
        var bake = Bake(name: scene, quality: SdfBakeQuality.Standard);
        var mesh = bake.Mesh;
        const int Tile = SdfBakeTier.TileTexels;

        Assert.Equal(actual: Tile, expected: 4);
        Assert.Equal(expected: 0, actual: (mesh.AtlasWidth % 4));
        Assert.Equal(expected: 0, actual: (mesh.AtlasHeight % 4));
        Assert.Equal(expected: (mesh.Quads * 6), actual: mesh.Indices.Length);
        Assert.All(collection: bake.Textures, action: texture => Assert.Equal(expected: (mesh.AtlasWidth, mesh.AtlasHeight), actual: (texture.Width, texture.Height)));

        var facing = 0;

        for (var quad = 0; (quad < mesh.Quads); quad++) {
            var tileX = ((quad % mesh.TileColumns) * Tile);
            var tileY = ((quad / mesh.TileColumns) * Tile);

            for (var corner = 0; (corner < 4); corner++) {
                var uv = mesh.Vertices[((quad * 4) + corner)].Uv;
                var texelX = ((uv.X * mesh.AtlasWidth) - 0.5f);
                var texelY = ((uv.Y * mesh.AtlasHeight) - 0.5f);

                Assert.InRange(actual: texelX, high: (((tileX + Tile) - 1) + 1e-3f), low: (tileX - 1e-3f));
                Assert.InRange(actual: texelY, high: (((tileY + Tile) - 1) + 1e-3f), low: (tileY - 1e-3f));
                Assert.Equal(expected: MathF.Round(x: texelX), actual: texelX, tolerance: 1e-3f);
            }

            // In double over the stored floats, as the baker judges a split.
            double ox = 0.0, oy = 0.0, oz = 0.0;

            for (var corner = 0; (corner < 4); corner++) {
                var normal = mesh.Vertices[((quad * 4) + corner)].Normal;

                ox += normal.X;
                oy += normal.Y;
                oz += normal.Z;
            }

            for (var triangle = 0; (triangle < 2); triangle++) {
                var at = ((quad * 6) + (triangle * 3));
                var a = mesh.Vertices[mesh.Indices[at]].Position;
                var b = mesh.Vertices[mesh.Indices[(at + 1)]].Position;
                var c = mesh.Vertices[mesh.Indices[(at + 2)]].Position;
                double ux = (((double)b.X) - a.X), uy = (((double)b.Y) - a.Y), uz = (((double)b.Z) - a.Z);
                double vx = (((double)c.X) - a.X), vy = (((double)c.Y) - a.Y), vz = (((double)c.Z) - a.Z);
                var dot = (((((uy * vz) - (uz * vy)) * ox) + (((uz * vx) - (ux * vz)) * oy)) + (((ux * vy) - (uy * vx)) * oz));

                facing += ((dot >= 0.0) ? 1 : 0);
            }
        }

        // Dual contouring folds a quad across a feature thinner than a cell, where no split faces out; every scene's
        // thinnest feature spans more than two cells at this tier.
        Assert.True(
            condition: (facing == mesh.Triangles),
            userMessage: $"{scene}: {(mesh.Triangles - facing)} of {mesh.Triangles} triangles wind inward"
        );
    }
    [Fact]
    public void TheImpostorSeesTheSilhouetteFromEveryView() {
        // At the standard tier each view's middle 4x4 block lies inside the silhouette.
        var bake = Bake(name: "sphere", quality: SdfBakeQuality.Standard);
        var impostor = bake.Impostor;
        var side = (impostor.Views * impostor.ViewTexels);
        var albedo = impostor.Albedo.Decode(level: 0);

        for (var j = 0; (j < impostor.Views); j++) {
            for (var i = 0; (i < impostor.Views); i++) {
                var middle = ((((j * impostor.ViewTexels) + (impostor.ViewTexels / 2)) * side) + ((i * impostor.ViewTexels) + (impostor.ViewTexels / 2)));
                var corner = (((j * impostor.ViewTexels) * side) + (i * impostor.ViewTexels));

                Assert.Equal(expected: 255, actual: albedo[((middle * 4) + 3)]);
                // The albedo is the material's exact sRGB code (the sphere is material 0): the view's middle block is one
                // color, which the block encoder stores exactly.
                Assert.Equal(
                    expected: (ImageSourceConversion.LinearToSrgb8(value: Materials[0].Albedo.X), ImageSourceConversion.LinearToSrgb8(value: Materials[0].Albedo.Y), ImageSourceConversion.LinearToSrgb8(value: Materials[0].Albedo.Z)),
                    actual: (albedo[(middle * 4)], albedo[((middle * 4) + 1)], albedo[((middle * 4) + 2)])
                );
                Assert.Equal(expected: 0, actual: albedo[((corner * 4) + 3)]);
                Assert.InRange(actual: SdfBakedImpostor.ViewDirection(i: i, j: j, views: impostor.Views).Length(), high: 1.0001f, low: 0.9999f);
            }
        }

        Assert.Equal(expected: bake.Work.Rays, actual: ((long)(side * side)));
    }
    [Fact]
    public void TheImpostorCarriesTheLightItsSurfaceEmits() {
        // The torus is all material 1, which emits its linear albedo three times over. Each view texel whose ray hit it
        // (its albedo's coverage) holds that light, and each texel whose ray missed holds none, within BC6H's stated
        // bound: 30% of the value (TextureCodecLawTests' figure over any block).
        const double Bound = 0.3;
        var bake = Bake(name: "torus", quality: SdfBakeQuality.Standard);
        var impostor = bake.Impostor;
        var side = (impostor.Views * impostor.ViewTexels);
        var albedo = impostor.Albedo.Decode(level: 0);
        var emission = impostor.Emission.Decode(level: 0);
        var strength = ((double)Materials[1].Emissive);
        double[] light = [((double)((Half)(Materials[1].Albedo.X * strength))), ((double)((Half)(Materials[1].Albedo.Y * strength))), ((double)((Half)(Materials[1].Albedo.Z * strength)))];

        var (hits, misses) = (0, 0);

        Assert.Equal(expected: (side, side), actual: (impostor.Emission.Width, impostor.Emission.Height));

        for (var texel = 0; (texel < (side * side)); texel++) {
            var hit = (albedo[((texel * 4) + 3)] >= 128);

            (hits, misses) = (hit ? ((hits + 1), misses) : (hits, (misses + 1)));

            for (var channel = 0; (channel < 3); channel++) {
                var value = ((double)BitConverter.UInt16BitsToHalf(value: BinaryPrimitives.ReadUInt16LittleEndian(source: emission.AsSpan(start: ((texel * 8) + (channel * 2))))));
                var expected = (hit ? light[channel] : 0.0);

                Assert.True(
                    condition: (Math.Abs(value: (value - expected)) <= (Bound * light[channel])),
                    userMessage: $"texel {texel} channel {channel}: {value} where the field emits {expected}"
                );
            }
        }

        Assert.True(condition: ((hits > 0) && (misses > 0)), userMessage: $"{hits} hits and {misses} misses");
    }
    [Fact]
    public void AnImpostorOfNothingEmissiveStoresTheConstantDarkBlock() {
        // The sphere is all material 0, which emits nothing: every level decodes to zero light, and every block is the
        // one block the encoder writes for a uniform dark block.
        var impostor = Bake(name: "sphere", quality: SdfBakeQuality.Standard).Impostor;
        var constant = impostor.Emission.Levels[0].AsSpan(length: Bc6hCodec.BlockBytes, start: 0).ToArray();

        for (var level = 0; (level < impostor.Emission.Levels.Count); level++) {
            var blocks = impostor.Emission.Levels[level];
            var decoded = impostor.Emission.Decode(level: level);

            for (var at = 0; (at < blocks.Length); at += Bc6hCodec.BlockBytes) {
                Assert.Equal(expected: constant, actual: blocks.AsSpan(length: Bc6hCodec.BlockBytes, start: at).ToArray());
            }

            for (var texel = 0; (texel < (decoded.Length / 8)); texel++) {
                Assert.Equal(expected: 0UL, actual: BinaryPrimitives.ReadUInt64LittleEndian(source: decoded.AsSpan(start: (texel * 8))) & 0x0000FFFFFFFFFFFFUL);
            }
        }
    }
    [Fact]
    public void AProgramTheInterpreterRefusesOrWithoutAFieldShapeHasNoBake() {
        var warped = Make(emit: static builder => builder.ResetPoint().TwistY(rate: 1f).Box(halfExtents: new Vector3(value: 0.3f), material: 0, round: 0f));
        var detailOnly = Make(emit: static builder => builder.ResetPoint().Sphere(detail: true, material: 0, radius: 0.5f));

        foreach (var program in ((SdfProgram[])[warped, detailOnly])) {
            _ = Assert.ThrowsAny<ArgumentException>(testCode: () => SdfBaker.Bake(
                center: Vector3.Zero,
                materials: Materials,
                program: program,
                reach: 1f,
                tier: SdfBakeTier.For(quality: SdfBakeQuality.Preview)
            ));
        }
    }
}
