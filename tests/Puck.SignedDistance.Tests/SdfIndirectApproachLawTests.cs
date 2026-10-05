using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfIndirectApproachLawTests {
    [Fact]
    public void ThePackedBallStaysInsideTheSampleCertificateAndJoinedToTheHit() {
        var surface = new Vector3(x: 3.1f, y: -0.4f, z: 2.7f);
        var direction = Vector3.Normalize(value: new Vector3(x: 1, y: 2, z: 3));

        foreach (var distance in new[] { 0.0f, 0.00123f, 0.07237f, 0.19991f }) {
            var sample = (surface - (direction * distance));
            var clearance = (distance + 0.00371f);
            var packed = SdfIndirectApproach.Pack(clearance: clearance, direction: direction, sample: sample, spacing: 0.5f, surface: surface, surfaceClearance: 0.0f);

            Assert.NotEqual(actual: packed, expected: 0u);
            var reconstructed = SdfIndirectApproach.Point(direction: direction, packed: packed, surface: surface);
            var radius = SdfIndirectApproach.Clearance(packed: packed);

            Assert.True(condition: ((Vector3.Distance(value1: sample, value2: reconstructed) + radius) <= clearance));
            Assert.True(condition: (Vector3.Distance(value1: surface, value2: reconstructed) <= radius));
            Assert.True(condition: (Vector3.Distance(value1: surface, value2: reconstructed) <= 0.25f));
        }
    }
    [Fact]
    public void ARemoteOrDisconnectedSampleCannotBecomeAReceiverLaunch() {
        Assert.Equal(0u, SdfIndirectApproach.Pack(Vector3.Zero, Vector3.UnitZ, new(x: 0, y: 0, z: -0.3f), 1, 0, 0.5f));
        Assert.Equal(0u, SdfIndirectApproach.Pack(Vector3.Zero, Vector3.UnitZ, new(x: 0, y: 0, z: -0.1f), 0.01f, 0.01f, 0.5f));
        Assert.Equal(0u, SdfIndirectApproach.Pack(Vector3.Zero, Vector3.UnitZ, Vector3.Zero, float.NaN, 0, 0.5f));
        Assert.Equal(0u, SdfIndirectApproach.Pack(Vector3.Zero, Vector3.UnitZ, Vector3.Zero, 0, 0, 0.5f));
        var joined = SdfIndirectApproach.Pack(Vector3.Zero, Vector3.UnitZ, new(x: 0, y: 0, z: -0.1f), 0.08f, 0.03f, 0.5f);

        Assert.NotEqual(actual: joined, expected: 0u);
        Assert.True(condition: (Vector3.Distance(value1: Vector3.Zero, value2: SdfIndirectApproach.Point(Vector3.Zero, Vector3.UnitZ, joined))
            <= (SdfIndirectApproach.Clearance(packed: joined) + 0.03f)));
    }
}
