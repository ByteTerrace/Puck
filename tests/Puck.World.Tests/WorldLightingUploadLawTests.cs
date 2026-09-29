using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Authored integer sky seeds retain every bit through resolution and the native record upload.</summary>
public sealed class WorldLightingUploadLawTests {
    [Theory]
    [InlineData(7u)]
    [InlineData(8388609u)]
    [InlineData(16777217u)]
    [InlineData(2147483649u)]
    [InlineData(uint.MaxValue)]
    public void Authored_star_and_cloud_seeds_reach_the_native_record_without_a_float_carrier(uint seed) {
        var json = $$"""
            { "sky": { "layers": [
              { "$type": "stars", "brightness": 1, "seed": {{seed}} },
              { "$type": "clouds", "coverage": 0.5, "seed": {{seed}} }
            ] } }
            """;
        var definition = Fixtures.BuildDocument() with {
            RenderRaw = JsonSerializer.Deserialize(json, WorldJsonContext.Default.WorldRenderDefaults),
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);
        var resolved = new WorldEnvironmentResolve().Resolve(definition, 0, ClientFixtures.StateMirror(definition));
        var upload = new SdfLightingUpload();
        upload.Pack(resolved);
        var starOffset = Marshal.OffsetOf<SdfSkyFrameData>(nameof(SdfSkyFrameData.StarSeed)).ToInt32();
        var cloudOffset = Marshal.OffsetOf<SdfSkyFrameData>(nameof(SdfSkyFrameData.CloudSeed)).ToInt32();
        Assert.Equal(seed, BinaryPrimitives.ReadUInt32LittleEndian(upload.SkyFrameBytes[starOffset..]));
        Assert.Equal(seed, BinaryPrimitives.ReadUInt32LittleEndian(upload.SkyFrameBytes[cloudOffset..]));
    }
}
