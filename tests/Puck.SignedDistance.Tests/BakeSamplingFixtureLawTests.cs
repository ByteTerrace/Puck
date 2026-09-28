using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Gpu;
using Puck.Assets.Textures;
using Puck.SignedDistance.Baking;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The GPU-free half of the bake sampling check. <c>Fixtures/bake-sampling.json</c> holds three textures of one real
/// bake, a two-material sphere whose second material glows, at the preview tier: its surface albedo (BC7, sRGB), normal
/// (BC5) and emission (BC6H), every mip level's blocks, and one probe texel per level with the value the CPU decoder
/// reads there. The probe is the level's texel with the largest channel sum, the first in scan order on a tie.
/// <para>These laws hold the fixture to a fresh bake, byte for byte, and each probe to the decoder, so the fixture is a
/// real bake of the current baker and its expectations are the oracle's. When the baker moves, the first law writes the
/// regenerated fixture to the temporary directory and names it.</para>
/// <para>The device half (<c>BakeSamplingDeviceLawTests</c> in <c>tests/Puck.World.Tests</c>) uploads each texture as an image of its format (<c>BC7_UNORM</c> sampled without sRGB decode,
/// <c>BC5_UNORM</c>, <c>BC6H_UFLOAT</c>) with every level, samples each probe's texel center at its level through a
/// point sampler (<c>SampleLevel</c>), and compares on both backends: BC7 within half a code of the expected code over
/// 255, BC5 within one code (a device interpolates BC4 in float), and BC6H exactly the expected half.</para>
/// </summary>
public sealed class BakeSamplingFixtureLawTests {
    private static readonly SdfMaterial[] Materials = [
        new(Albedo: new Vector3(x: 0.8f, y: 0.2f, z: 0.1f)),
        new(Albedo: new Vector3(x: 0.1f, y: 0.5f, z: 0.9f), Emissive: 3f),
    ];

    private static string FixturePath =>
        Path.Combine(path1: AppContext.BaseDirectory, path2: "Fixtures", path3: "bake-sampling.json");

    private static SdfBake Bake() {
        var builder = new SdfProgramBuilder();

        foreach (var material in Materials) {
            _ = builder.AddMaterial(material: material);
        }

        _ = builder.ResetPoint().Sphere(material: 0, radius: 0.6f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0.45f, z: 0f)).Sphere(material: 1, radius: 0.3f);

        return SdfBaker.Bake(
            center: Vector3.Zero,
            materials: Materials,
            program: builder.Build(buildInstanceGrid: false),
            reach: 0.8f,
            tier: SdfBakeTier.For(quality: SdfBakeQuality.Preview)
        );
    }
    private static SdfBakedTexture[] Sampled(SdfBake bake) => [bake.Textures[0], bake.Textures[1], bake.Textures[4]];
    // The decoded texel at (x, y) of a decoded level, as integers: bytes for BC7 and BC5, half bits for BC6H.
    private static int[] Texel(SdfBakedTexture texture, byte[] decoded, int width, int x, int y) {
        var at = ((y * width) + x);

        return texture.Format switch {
            GpuPixelFormat.Bc7Unorm => [decoded[(at * 4)], decoded[((at * 4) + 1)], decoded[((at * 4) + 2)], decoded[((at * 4) + 3)]],
            GpuPixelFormat.Bc5Unorm => [decoded[(at * 2)], decoded[((at * 2) + 1)]],
            _ => [
                BinaryPrimitives.ReadUInt16LittleEndian(source: decoded.AsSpan(start: (at * 8))),
                BinaryPrimitives.ReadUInt16LittleEndian(source: decoded.AsSpan(start: ((at * 8) + 2))),
                BinaryPrimitives.ReadUInt16LittleEndian(source: decoded.AsSpan(start: ((at * 8) + 4))),
            ],
        };
    }
    private static JsonObject Record(SdfBake bake) {
        var textures = new JsonArray();

        foreach (var texture in Sampled(bake: bake)) {
            var probes = new JsonArray();

            for (var level = 0; (level < texture.Levels.Count); level++) {
                var (width, height) = texture.LevelExtent(level: level);
                var decoded = texture.Decode(level: level);

                var (bestX, bestY, bestSum) = (0, 0, -1L);

                for (var y = 0; (y < height); y++) {
                    for (var x = 0; (x < width); x++) {
                        var sum = Texel(decoded: decoded, texture: texture, width: width, x: x, y: y).Sum(selector: static value => ((long)value));

                        if (sum > bestSum) {
                            (bestX, bestY, bestSum) = (x, y, sum);
                        }
                    }
                }

                probes.Add(item: new JsonObject {
                    ["level"] = level,
                    ["x"] = bestX,
                    ["y"] = bestY,
                    ["expected"] = new JsonArray(items: [.. Texel(decoded: decoded, texture: texture, width: width, x: bestX, y: bestY).Select(selector: static value => ((JsonNode?)value))]),
                });
            }

            textures.Add(item: new JsonObject {
                ["usage"] = texture.Usage.ToString(),
                ["format"] = texture.Format.ToString(),
                ["colorSpace"] = texture.ColorSpace.ToString(),
                ["width"] = texture.Width,
                ["height"] = texture.Height,
                ["tileTexels"] = texture.TileTexels,
                ["levels"] = new JsonArray(items: [.. texture.Levels.Select(selector: static level => ((JsonNode?)Convert.ToBase64String(inArray: level)))]),
                ["probes"] = probes,
            });
        }

        return new JsonObject {
            ["bakerVersion"] = SdfBaker.Version,
            ["textures"] = textures,
        };
    }

    [Fact]
    public void TheFixtureIsAFreshBakeOfTheCurrentBaker() {
        var fresh = Record(bake: Bake()).ToJsonString(options: new JsonSerializerOptions { WriteIndented = true });
        var recorded = File.ReadAllText(path: FixturePath).ReplaceLineEndings(replacementText: "\n").TrimEnd();

        if (recorded != fresh.ReplaceLineEndings(replacementText: "\n").TrimEnd()) {
            var regenerated = Path.Combine(path1: Path.GetTempPath(), path2: "bake-sampling.json");

            File.WriteAllText(contents: (fresh + "\n"), path: regenerated);
            Assert.Fail(message: $"tests/Puck.SignedDistance.Tests/Fixtures/bake-sampling.json is not a fresh bake at baker version {SdfBaker.Version}; the regenerated fixture is at {regenerated}.");
        }
    }
    [Fact]
    public void EveryProbeIsWhatTheDecoderReadsAtItsLevel() {
        var fixture = JsonNode.Parse(json: File.ReadAllText(path: FixturePath))!;
        var textures = fixture["textures"]!.AsArray();

        Assert.Equal(expected: ["Bc7Unorm", "Bc5Unorm", "Bc6hUfloat"], actual: textures.Select(selector: static texture => texture!["format"]!.GetValue<string>()));

        foreach (var node in textures) {
            var texture = new SdfBakedTexture(
                ColorSpace: Enum.Parse<TextureColorSpace>(value: node!["colorSpace"]!.GetValue<string>()),
                Format: Enum.Parse<GpuPixelFormat>(value: node["format"]!.GetValue<string>()),
                Height: node["height"]!.GetValue<int>(),
                Levels: [.. node["levels"]!.AsArray().Select(selector: static level => Convert.FromBase64String(s: level!.GetValue<string>()))],
                TileTexels: node["tileTexels"]!.GetValue<int>(),
                Usage: Enum.Parse<SdfBakeTextureUsage>(value: node["usage"]!.GetValue<string>()),
                Width: node["width"]!.GetValue<int>()
            );
            var probes = node["probes"]!.AsArray();

            Assert.Equal(expected: texture.Levels.Count, actual: probes.Count);
            Assert.Equal(expected: TextureMipChain.LevelCount(tileTexels: texture.TileTexels), actual: texture.Levels.Count);

            foreach (var probe in probes) {
                var level = probe!["level"]!.GetValue<int>();

                var (width, _) = texture.LevelExtent(level: level);

                Assert.Equal(
                    expected: probe["expected"]!.AsArray().Select(selector: static value => value!.GetValue<int>()),
                    actual: Texel(decoded: texture.Decode(level: level), texture: texture, width: width, x: probe["x"]!.GetValue<int>(), y: probe["y"]!.GetValue<int>())
                );
            }
        }
    }
}
