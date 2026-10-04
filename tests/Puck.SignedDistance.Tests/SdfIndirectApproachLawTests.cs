using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfIndirectApproachLawTests {
    [Fact]
    public void ThePackedBallStaysInsideTheSampleCertificateAndJoinedToTheHit() {
        var surface = new Vector3(3.1f, -0.4f, 2.7f);
        var direction = Vector3.Normalize(new Vector3(1, 2, 3));
        foreach (var distance in new[] { 0.0f, 0.00123f, 0.07237f, 0.19991f }) {
            var sample = surface - direction * distance;
            var clearance = distance + 0.00371f;
            var packed = SdfIndirectApproach.Pack(surface, direction, sample, clearance, 0.0f, 0.5f);
            Assert.NotEqual(0u, packed);
            var reconstructed = SdfIndirectApproach.Point(surface, direction, packed);
            var radius = SdfIndirectApproach.Clearance(packed);
            Assert.True(Vector3.Distance(sample, reconstructed) + radius <= clearance);
            Assert.True(Vector3.Distance(surface, reconstructed) <= radius);
            Assert.True(Vector3.Distance(surface, reconstructed) <= 0.25f);
        }
    }

    [Fact]
    public void ARemoteOrDisconnectedSampleCannotBecomeAReceiverLaunch() {
        Assert.Equal(0u, SdfIndirectApproach.Pack(Vector3.Zero, Vector3.UnitZ, new(0, 0, -0.3f), 1, 0, 0.5f));
        Assert.Equal(0u, SdfIndirectApproach.Pack(Vector3.Zero, Vector3.UnitZ, new(0, 0, -0.1f), 0.01f, 0.01f, 0.5f));
        Assert.Equal(0u, SdfIndirectApproach.Pack(Vector3.Zero, Vector3.UnitZ, Vector3.Zero, float.NaN, 0, 0.5f));
        Assert.Equal(0u, SdfIndirectApproach.Pack(Vector3.Zero, Vector3.UnitZ, Vector3.Zero, 0, 0, 0.5f));
        var joined = SdfIndirectApproach.Pack(Vector3.Zero, Vector3.UnitZ, new(0, 0, -0.1f), 0.08f, 0.03f, 0.5f);
        Assert.NotEqual(0u, joined);
        Assert.True(Vector3.Distance(Vector3.Zero, SdfIndirectApproach.Point(Vector3.Zero, Vector3.UnitZ, joined))
            <= SdfIndirectApproach.Clearance(joined) + 0.03f);
    }
}
