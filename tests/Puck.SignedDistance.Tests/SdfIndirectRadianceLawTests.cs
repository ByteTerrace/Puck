using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfIndirectRadianceLawTests {
    [Fact]
    public void StorageRetainsIndependentHdrChannelsAndRoundsAtTheDeclaredMantissa() {
        Assert.Equal(0u, SdfIndirectRadiance.Pack(Vector3.Zero));
        Assert.Equal(0x781E03C0u, SdfIndirectRadiance.Pack(Vector3.One));
        Assert.Equal(new Vector3(1.0f, 2.0f, 4.0f), SdfIndirectRadiance.Unpack(0x882003C0u));
        Assert.Equal(new Vector3(65024.0f, 65024.0f, 64512.0f), SdfIndirectRadiance.Unpack(SdfIndirectRadiance.Pack(new Vector3(float.PositiveInfinity))));
        Assert.Equal(Vector3.Zero, SdfIndirectRadiance.Unpack(SdfIndirectRadiance.Pack(new Vector3(-1.0f, float.NaN, float.NegativeInfinity))));
        Assert.Equal(1.0f, SdfIndirectRadiance.Unpack(SdfIndirectRadiance.Pack(new Vector3(1.0078125f, 0, 0))).X);
        Assert.Equal(1.03125f, SdfIndirectRadiance.Unpack(SdfIndirectRadiance.Pack(new Vector3(1.0234375f, 0, 0))).X);
    }
}
