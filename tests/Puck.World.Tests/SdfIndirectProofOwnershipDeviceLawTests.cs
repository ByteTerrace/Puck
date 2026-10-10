using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Shared receiver proofs are canonical: one record per anchor bin, proved from the bin's centre within a
/// fixed allowance, so whichever receiver computes or reads it, every receiver's answer is the same. Unreadable
/// publications defer, failed records transfer nothing, and transport proofs stay private.</summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfIndirectProofOwnershipDeviceLawTests {
    [Fact]
    public void VulkanSharesCanonicalProofsWhoeverComputesThem() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfIndirectProofOwnershipDeviceLawTests));

        Verify(device.Services, ".spv");
    }
    [Fact]
    public void DirectXSharesCanonicalProofsWhoeverComputesThem() {
        using var output = new StringWriter();

        using (var device = DirectXTestDevices.Debug(output: output)) { Verify(device.Services, ".dxil"); }
        Assert.DoesNotContain("[d3d12-debug]", output.ToString(), StringComparison.Ordinal);
    }

    // Each case's rows: (queries, resolved lanes, deferred lanes, publication stamp) and (admissions, summed masks,
    // record key, bucket entry), for the first phase and then the second, a barrier later with a newer frame stamp.
    private static void Verify(GpuDeviceServices services, string extension) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder.Sphere(radius: 1, material: material).Build();
        const int Count = 16;
        var rows = Enumerable.Range(count: Count, start: 0).Select(selector: index => new Vector4(BitConverter.UInt32BitsToSingle(value: ((uint)index)), 0, 0, 0)).ToArray();
        var result = SdfIndirectDeviceProbe.Run(services, extension, "sdf-indirect-proof-ownership.comp", 4,
            Enumerable.Repeat(count: Count, element: program).ToArray(), rows);
        Vector4 fresh = new(w: 9, x: 2, y: 1, z: 63), freshWork = new(w: 1, x: 1, y: 255, z: 2340);
        Vector4 reused = new(w: 9, x: 0, y: 64, z: 0), reusedWork = new(w: 1, x: 0, y: 16320, z: 2340);
        Vector4 own = new(w: 9, x: 64, y: 64, z: 0), ownWork = new(w: 1, x: 64, y: 16320, z: 2340);

        // All 64 lanes ask for one bin. Its claimant proves the bin's centre (its clearance, then one clear component)
        // and publishes; the other lanes defer, then inherit the published mask with no field work.
        Expect(first: fresh, firstWork: freshWork, index: 0, second: reused, secondWork: reusedWork);
        // A pending claim or a publication of the current submission carries a poisoned payload: neither key nor
        // anchor is read before an earlier stamp. The released bin then fills as a fresh one.
        Expect(1, new(w: -1, x: 0, y: 0, z: 64), new(w: 1, x: 0, y: 0, z: 2112), new(w: 10, x: 2, y: 1, z: 63), freshWork);
        // Keys 2112 and 2340 collide in one bucket. Another bin's earlier record is no obstacle: every receiver
        // computes its own bin's canonical record again, unpublished, and reaches the same answer.
        Expect(2, new(w: 9, x: 0, y: 0, z: 64), new(w: 1, x: 0, y: 0, z: 2112), new(w: 9, x: 128, y: 64, z: 0), new(w: 1, x: 64, y: 16320, z: 2112));
        Expect(3, new(w: 8, x: 128, y: 64, z: 0), new(w: 1, x: 64, y: 16320, z: 2112), new(w: 8, x: 128, y: 64, z: 0), new(w: 1, x: 64, y: 16320, z: 2112));
        // The bin's own record whose anchor ball no receiver reaches, or whose anchor has no clearance, transfers
        // nothing: each receiver proves its own point.
        foreach (var index in new[] { 4, 5 }) {
            Expect(index, own with { W = 8 }, ownWork, own with { W = 8 }, ownWork);
        }
        // A reachable earlier record answers every receiver with its own mask.
        Expect(6, new(w: 8, x: 0, y: 64, z: 0), new(w: 1, x: 0, y: 10560, z: 2340), new(w: 8, x: 0, y: 64, z: 0), new(w: 1, x: 0, y: 10560, z: 2340));
        // A failed canonical proof is published as a record that transfers nothing, never as a negative answer:
        // its readers later prove their own points.
        Expect(7, new(w: 9, x: 2, y: 0, z: 63), new(w: 1, x: 1, y: 0, z: 2340), own, ownWork);
        // Denied admission defers every lane and publishes nothing; the bin then fills as a fresh one.
        foreach (var index in new[] { 8, 15 }) {
            Expect(index, new(w: 0, x: 0, y: 0, z: 64), new(w: 1, x: 0, y: 0, z: 0), new(w: 10, x: 2, y: 1, z: 63), freshWork);
        }
        // A nonfinite component segment fails the canonical record, which then transfers nothing; the claimant's own
        // proof still answers it.
        Expect(9, new(w: 9, x: 3, y: 1, z: 63), freshWork, own, ownWork);
        // An already admitted launch owns its permit; the canonical proof does not debit a second one.
        Expect(10, fresh, freshWork with { X = 0 }, reused, reusedWork);
        // A stale cell or an unusable directory answers nothing and claims nothing.
        foreach (var index in new[] { 11, 12 }) {
            Expect(index, Vector4.Zero, new(w: 1, x: 0, y: 0, z: 0), new(w: 10, x: 2, y: 1, z: 63), freshWork);
        }
        // Inactive corners give the canonical record no clear component, so its readers prove their own points.
        Expect(13, new(w: 9, x: 0, y: 0, z: 63), new(w: 1, x: 1, y: 0, z: 2340), own, ownWork);
        // The canonical allowance is fixed, whatever the claimant's own budget: the record is a function of the bin.
        Expect(first: fresh, firstWork: freshWork, index: 14, second: reused, secondWork: reusedWork);

        // Transport proofs are the caller's own: every ray proves its own point and nothing is published.
        var traced = SdfIndirectDeviceProbe.Run(services, extension, "sdf-indirect-proof-trace-owner.comp", 4,
            new[] { program, program }, rows[..2]);

        for (var index = 0; (index < 2); index++) {
            Assert.Equal(new Vector4(w: 0, x: 64, y: 64, z: 0), traced[index]);
            Assert.Equal(new Vector4(w: 1, x: 0, y: 16320, z: 0), traced[(2 + index)]);
            Assert.Equal(new Vector4(w: 0, x: 64, y: 64, z: 0), traced[(4 + index)]);
            Assert.Equal(new Vector4(w: 1, x: 0, y: 16320, z: 0), traced[(6 + index)]);
        }

        Vector4 Row(int index, int row) => result[((row * Count) + index)];
        void Expect(int index, Vector4 first, Vector4 firstWork, Vector4 second, Vector4 secondWork) {
            Assert.Equal(first, Row(index: index, row: 0));
            Assert.Equal(firstWork, Row(index: index, row: 1));
            Assert.Equal(second, Row(index: index, row: 2));
            Assert.Equal(secondWork, Row(index: index, row: 3));
        }
    }
}
