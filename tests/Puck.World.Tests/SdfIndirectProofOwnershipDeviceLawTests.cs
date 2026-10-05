using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Actual proof leases coalesce successful support, defer unreadable publications and preserve collision fallback.</summary>
[Collection(DebugLayerCollection.Name)]
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfIndirectProofOwnershipDeviceLawTests {
    [Fact]
    public void VulkanCoalescesProofWorkWithoutSharingFailuresOrLosingCollisionFallbacks() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfIndirectProofOwnershipDeviceLawTests));

        Verify(device.Services, ".spv");
    }
    [Fact]
    public void DirectXCoalescesProofWorkWithoutSharingFailuresOrLosingCollisionFallbacks() {
        using var output = new StringWriter();

        using (var device = DirectXTestDevices.Debug(output: output)) { Verify(device.Services, ".dxil"); }
        Assert.DoesNotContain("[d3d12-debug]", output.ToString(), StringComparison.Ordinal);
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder.Sphere(radius: 1, material: material).Build();
        const int Count = 16;
        var rows = Enumerable.Range(0, Count).Select(index => new Vector4(BitConverter.UInt32BitsToSingle((uint)index), 0, 0, 0)).ToArray();
        var result = SdfIndirectDeviceProbe.Run(services, extension, "sdf-indirect-proof-ownership.comp", 4,
            Enumerable.Repeat(program, Count).ToArray(), rows);

        // All 64 lanes ask for the same component. Exactly one clear-field query publishes the positive proof;
        // the other lanes defer, then reuse its complete publication on the next submission.
        Expect(0, new(1, 1, 63, 9), new(1, 255, 2340, 1), new(0, 64, 0, 9), new(0, 16320, 2340, 1));
        // Pending/current stamps carry poisoned payloads. Neither key nor anchor is usable before an older stamp.
        Expect(1, new(0, 0, 64, -1), new(0, 0, 2112, 1), new(1, 1, 63, 10), new(1, 255, 2340, 1));
        Expect(2, new(0, 0, 64, 9), new(0, 0, 2112, 1), new(64, 64, 0, 9), new(64, 16320, 2112, 1));
        // Literal keys2112 and2340 collide in bucket1. Earlier foreign keys, unsupported anchor balls and zero
        // clearance must keep admitted uncached work, otherwise eight occupied buckets could starve a cell.
        Expect(3, new(64, 64, 0, 8), new(64, 16320, 2112, 1), new(64, 64, 0, 8), new(64, 16320, 2112, 1));
        foreach (var index in new[] { 4, 5 }) {
            Expect(index, new(64, 64, 0, 8), new(64, 16320, 2340, 1), new(64, 64, 0, 8), new(64, 16320, 2340, 1));
        }
        Expect(6, new(0, 64, 0, 8), new(0, 10560, 2340, 1), new(0, 64, 0, 8), new(0, 10560, 2340, 1));

        // A failed owner releases rather than publishing a negative proof. Another lane may legitimately acquire
        // that empty slot in this dispatch; count actual owners without assuming wave arrival order.
        var failed = Row(7, 0);
        Assert.InRange(failed.X, 1, 64);
        Assert.Equal(new Vector4(failed.X, 0, 64 - failed.X, 0), failed);
        Assert.Equal(new Vector4(failed.X, 0, 0, 1), Row(7, 1));
        ExpectRecovered(7);
        Expect(8, new(0, 0, 64, 0), new(0, 0, 0, 1), new(1, 1, 63, 10), new(1, 255, 2340, 1));

        // Clear support followed by nonfinite anchor clearance answers only the current request and releases.
        var unpublishable = Row(9, 0);
        Assert.InRange(unpublishable.Y, 1, 64);
        Assert.Equal(new Vector4(2 * unpublishable.Y, unpublishable.Y, 64 - unpublishable.Y, 0), unpublishable);
        Assert.Equal(new Vector4(unpublishable.Y, 255 * unpublishable.Y, 0, 1), Row(9, 1));
        ExpectRecovered(9);
        // An already admitted launch owns its permit; proof leasing must not debit a second one.
        Expect(10, new(1, 1, 63, 9), new(0, 255, 2340, 1), new(0, 64, 0, 9), new(0, 16320, 2340, 1));
        foreach (var index in new[] { 11, 12 }) {
            Assert.Equal(Vector4.Zero, Row(index, 0));
            Assert.Equal(new Vector4(0, 0, 0, 1), Row(index, 1));
            ExpectRecovered(index);
        }
        foreach (var index in new[] { 13, 14 }) {
            var empty = Row(index, 0);
            Assert.InRange(empty.Z, 0, 63);
            Assert.Equal(new Vector4(0, 0, empty.Z, 0), empty);
            Assert.Equal(new Vector4(64 - empty.Z, 0, 0, 1), Row(index, 1));
            ExpectRecovered(index);
        }
        Expect(15, new(0, 0, 64, 0), new(0, 0, 0, 1), new(1, 1, 63, 10), new(1, 255, 2340, 1));

        // Trace keeps its existing designated source-ray owner. Other rays still calculate their own mask.
        var traced = SdfIndirectDeviceProbe.Run(services, extension, "sdf-indirect-proof-trace-owner.comp", 4,
            new[] { program, program }, rows[..2]);
        Assert.Equal(new Vector4(64, 64, 0, 9), traced[0]);
        Assert.Equal(new Vector4(0, 16320, 2340, 1), traced[2]);
        Assert.Equal(new Vector4(0, 64, 0, 9), traced[4]);
        Assert.Equal(new Vector4(64, 64, 0, 0), traced[1]);
        Assert.Equal(new Vector4(0, 16320, 0, 1), traced[3]);
        Assert.Equal(new Vector4(64, 64, 0, 0), traced[5]);

        Vector4 Row(int index, int row) => result[(row * Count) + index];
        void Expect(int index, Vector4 first, Vector4 firstWork, Vector4 second, Vector4 secondWork) {
            Assert.Equal(first, Row(index, 0));
            Assert.Equal(firstWork, Row(index, 1));
            Assert.Equal(second, Row(index, 2));
            Assert.Equal(secondWork, Row(index, 3));
        }
        void ExpectRecovered(int index) {
            Assert.Equal(new Vector4(1, 1, 63, 10), Row(index, 2));
            Assert.Equal(new Vector4(1, 255, 2340, 1), Row(index, 3));
        }
    }
}
