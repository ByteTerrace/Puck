using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfLightAndSkyLayoutLawTests {
    [Fact]
    public void SkyGainsReachTheirGeneratedFieldsWithoutAnAuxiliaryReflectionTable() {
        var sky = new SdfSky();

        sky.Block.Ambient = .375f;
        sky.Block.Reflection = .625f;
        sky.Pack(SdfLights.Default(), 40f, new SdfSkyDetails(), out var block, new SdfSkyLayer[SdfSky.MaxLayers]);
        var bytes = MemoryMarshal.AsBytes(span: MemoryMarshal.CreateReadOnlySpan(length: 1, reference: ref block));
        var structure = SdfKernelInterfaces.LightAndSkyTables.Single(predicate: m => (m.Name == SdfKernelInterfaces.Sky)).Structure!;

        Assert.Equal(256u, structure.SizeBytes);
        Assert.Equal(.375f, BinaryPrimitives.ReadSingleLittleEndian(source: bytes[((int)structure.Members.Single(predicate: m => (m.Name == nameof(SdfSkyBlock.Ambient))).Offset)..]));
        Assert.Equal(.625f, BinaryPrimitives.ReadSingleLittleEndian(source: bytes[((int)structure.Members.Single(predicate: m => (m.Name == nameof(SdfSkyBlock.Reflection))).Offset)..]));
        Assert.DoesNotContain(collection: SdfKernelInterfaces.LightAndSkyTables, filter: m => m.Name.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "softbox"));
    }
    [Fact]
    public void DefaultLightsContainOnlyTheSunAndAmbientComesFromTheSky() {
        var lights = SdfLights.Default();

        Assert.Equal(1, lights.Count);
        Assert.Equal(SdfLightKind.Directional, lights[0].Kind);
        Assert.Equal(Vector3.One, lights[0].Color);
        Assert.DoesNotContain("Hemisphere", Enum.GetNames<SdfLightKind>());
    }
}
