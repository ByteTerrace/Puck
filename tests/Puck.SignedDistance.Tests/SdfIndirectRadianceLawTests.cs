using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfIndirectRadianceLawTests {
    [Fact]
    public void SignedZeroIsBlackAndCannotLeakIntoAnotherChannel() {
        var negativeZero = BitConverter.Int32BitsToSingle(value: int.MinValue);

        Assert.Equal(0u, SdfIndirectRadiance.Pack(value: new Vector3(x: negativeZero, y: 0, z: 0)));
        Assert.Equal(0u, SdfIndirectRadiance.Pack(value: new Vector3(x: 0, y: negativeZero, z: 0)));
        Assert.Equal(0u, SdfIndirectRadiance.Pack(value: new Vector3(x: 0, y: 0, z: negativeZero)));
        Assert.Equal(0x781E0000u, SdfIndirectRadiance.Pack(value: new Vector3(x: negativeZero, y: 1, z: 1)));
        Assert.Equal(0x780003C0u, SdfIndirectRadiance.Pack(value: new Vector3(x: 1, y: negativeZero, z: 1)));
        Assert.Equal(0x001E03C0u, SdfIndirectRadiance.Pack(value: new Vector3(x: 1, y: 1, z: negativeZero)));
    }
    [Fact]
    public void StorageRetainsIndependentHdrChannelsAndRoundsAtTheDeclaredMantissa() {
        Assert.Equal(0u, SdfIndirectRadiance.Pack(value: Vector3.Zero));
        Assert.Equal(0x781E03C0u, SdfIndirectRadiance.Pack(value: Vector3.One));
        Assert.Equal(new Vector3(x: 1.0f, y: 2.0f, z: 4.0f), SdfIndirectRadiance.Unpack(value: 0x882003C0u));
        Assert.Equal(new Vector3(x: 65024.0f, y: 65024.0f, z: 64512.0f), SdfIndirectRadiance.Unpack(value: SdfIndirectRadiance.Pack(value: new Vector3(value: float.PositiveInfinity))));
        Assert.Equal(Vector3.Zero, SdfIndirectRadiance.Unpack(value: SdfIndirectRadiance.Pack(value: new Vector3(x: -1.0f, y: float.NaN, z: float.NegativeInfinity))));
        Assert.Equal(1.0f, SdfIndirectRadiance.Unpack(value: SdfIndirectRadiance.Pack(value: new Vector3(x: 1.0078125f, y: 0, z: 0))).X);
        Assert.Equal(1.03125f, SdfIndirectRadiance.Unpack(value: SdfIndirectRadiance.Pack(value: new Vector3(x: 1.0234375f, y: 0, z: 0))).X);
    }
}
