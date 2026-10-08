using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Actual proof leases coalesce successful support, defer unreadable publications and preserve collision fallback.</summary>
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
        var rows = Enumerable.Range(count: Count, start: 0).Select(selector: index => new Vector4(BitConverter.UInt32BitsToSingle(value: ((uint)index)), 0, 0, 0)).ToArray();
        var result = SdfIndirectDeviceProbe.Run(services, extension, "sdf-indirect-proof-ownership.comp", 4,
            Enumerable.Repeat(count: Count, element: program).ToArray(), rows);

        // All 64 lanes ask for the same component. Exactly one clear-field query publishes the positive proof;
        // the other lanes defer, then reuse its complete publication in a barrier-separated phase with a newer stamp.
        Expect(0, new(w: 9, x: 1, y: 1, z: 63), new(w: 1, x: 1, y: 255, z: 2340), new(w: 9, x: 0, y: 64, z: 0), new(w: 1, x: 0, y: 16320, z: 2340));
        // Pending/current stamps carry poisoned payloads. Neither key nor anchor is usable before an older stamp.
        Expect(1, new(w: -1, x: 0, y: 0, z: 64), new(w: 1, x: 0, y: 0, z: 2112), new(w: 10, x: 1, y: 1, z: 63), new(w: 1, x: 1, y: 255, z: 2340));
        Expect(2, new(w: 9, x: 0, y: 0, z: 64), new(w: 1, x: 0, y: 0, z: 2112), new(w: 9, x: 64, y: 64, z: 0), new(w: 1, x: 64, y: 16320, z: 2112));
        // Literal keys2112 and2340 collide in bucket1. Earlier foreign keys, unsupported anchor balls and zero
        // clearance must keep admitted uncached work, otherwise eight occupied buckets could starve a cell.
        Expect(3, new(w: 8, x: 64, y: 64, z: 0), new(w: 1, x: 64, y: 16320, z: 2112), new(w: 8, x: 64, y: 64, z: 0), new(w: 1, x: 64, y: 16320, z: 2112));
        foreach (var index in new[] { 4, 5 }) {
            Expect(index, new(w: 8, x: 64, y: 64, z: 0), new(w: 1, x: 64, y: 16320, z: 2340), new(w: 8, x: 64, y: 64, z: 0), new(w: 1, x: 64, y: 16320, z: 2340));
        }
        Expect(6, new(w: 8, x: 0, y: 64, z: 0), new(w: 1, x: 0, y: 10560, z: 2340), new(w: 8, x: 0, y: 64, z: 0), new(w: 1, x: 0, y: 10560, z: 2340));

        // A failed owner releases rather than publishing a negative proof. Another lane may legitimately acquire
        // that empty slot in this dispatch; count actual owners without assuming wave arrival order.
        var failed = Row(index: 7, row: 0);

        Assert.InRange(actual: failed.X, high: 64, low: 1);
        Assert.Equal(new Vector4(w: 0, x: failed.X, y: 0, z: (64 - failed.X)), failed);
        Assert.Equal(new Vector4(w: 1, x: failed.X, y: 0, z: 0), Row(index: 7, row: 1));
        ExpectRecovered(index: 7);
        Expect(8, new(w: 0, x: 0, y: 0, z: 64), new(w: 1, x: 0, y: 0, z: 0), new(w: 10, x: 1, y: 1, z: 63), new(w: 1, x: 1, y: 255, z: 2340));

        // Clear support followed by nonfinite anchor clearance answers only the current request and releases.
        var unpublishable = Row(index: 9, row: 0);

        Assert.InRange(actual: unpublishable.Y, high: 64, low: 1);
        Assert.Equal(new Vector4(w: 0, x: (2 * unpublishable.Y), y: unpublishable.Y, z: (64 - unpublishable.Y)), unpublishable);
        Assert.Equal(new Vector4(w: 1, x: unpublishable.Y, y: (255 * unpublishable.Y), z: 0), Row(index: 9, row: 1));
        ExpectRecovered(index: 9);
        // An already admitted launch owns its permit; proof leasing must not debit a second one.
        Expect(10, new(w: 9, x: 1, y: 1, z: 63), new(w: 1, x: 0, y: 255, z: 2340), new(w: 9, x: 0, y: 64, z: 0), new(w: 1, x: 0, y: 16320, z: 2340));
        foreach (var index in new[] { 11, 12 }) {
            Assert.Equal(Vector4.Zero, Row(index: index, row: 0));
            Assert.Equal(new Vector4(w: 1, x: 0, y: 0, z: 0), Row(index: index, row: 1));
            ExpectRecovered(index: index);
        }
        foreach (var index in new[] { 13, 14 }) {
            var empty = Row(index: index, row: 0);

            Assert.InRange(actual: empty.Z, high: 63, low: 0);
            Assert.Equal(new Vector4(w: 0, x: 0, y: 0, z: empty.Z), empty);
            Assert.Equal(new Vector4(w: 1, x: (64 - empty.Z), y: 0, z: 0), Row(index: index, row: 1));
            ExpectRecovered(index: index);
        }
        Expect(15, new(w: 0, x: 0, y: 0, z: 64), new(w: 1, x: 0, y: 0, z: 0), new(w: 10, x: 1, y: 1, z: 63), new(w: 1, x: 1, y: 255, z: 2340));

        // Trace keeps its existing designated source-ray owner. Other rays still calculate their own mask.
        var traced = SdfIndirectDeviceProbe.Run(services, extension, "sdf-indirect-proof-trace-owner.comp", 4,
            new[] { program, program }, rows[..2]);

        Assert.Equal(new Vector4(w: 9, x: 64, y: 64, z: 0), traced[0]);
        Assert.Equal(new Vector4(w: 1, x: 0, y: 16320, z: 2340), traced[2]);
        Assert.Equal(new Vector4(w: 9, x: 0, y: 64, z: 0), traced[4]);
        Assert.Equal(new Vector4(w: 0, x: 64, y: 64, z: 0), traced[1]);
        Assert.Equal(new Vector4(w: 1, x: 0, y: 16320, z: 0), traced[3]);
        Assert.Equal(new Vector4(w: 0, x: 64, y: 64, z: 0), traced[5]);

        Vector4 Row(int index, int row) => result[((row * Count) + index)];
        void Expect(int index, Vector4 first, Vector4 firstWork, Vector4 second, Vector4 secondWork) {
            Assert.Equal(first, Row(index: index, row: 0));
            Assert.Equal(firstWork, Row(index: index, row: 1));
            Assert.Equal(second, Row(index: index, row: 2));
            Assert.Equal(secondWork, Row(index: index, row: 3));
        }
        void ExpectRecovered(int index) {
            Assert.Equal(new Vector4(w: 10, x: 1, y: 1, z: 63), Row(index: index, row: 2));
            Assert.Equal(new Vector4(w: 1, x: 1, y: 255, z: 2340), Row(index: index, row: 3));
        }
    }
}
