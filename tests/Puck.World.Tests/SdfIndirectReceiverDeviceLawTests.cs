using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Production receiver certificates preserve exact scopes and completed outcomes without quantizing launch data.</summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfIndirectReceiverDeviceLawTests {
    [Fact]
    public void VulkanRetainsCompletedCertificatesButNeverDeferredOrForeignScopes() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfIndirectReceiverDeviceLawTests));

        Verify(device.Services, ".spv");
    }
    [Fact]
    public void DirectXRetainsCompletedCertificatesButNeverDeferredOrForeignScopes() {
        using var output = new StringWriter();

        using (var device = DirectXTestDevices.Debug(output: output)) { Verify(device.Services, ".dxil"); }
        Assert.DoesNotContain("[d3d12-debug]", output.ToString(), StringComparison.Ordinal);
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        // Allocation low word, high word, revision, proof mask, completed, valid.
        (uint Low, uint High, uint Revision, uint Mask, bool Completed, bool Valid)[] cases = [
            (9, 7, 13, 165, true, true),
            (9, 7, 13, 0, true, true),
            (9, 7, 13, 0, false, false),
            (8, 7, 13, 165, true, false),
            (9, 8, 13, 165, true, false),
            (9, 7, 14, 165, true, false),
        ];

        static float Bits(uint value) => BitConverter.UInt32BitsToSingle(value: value);
        var rows = new Vector4[(cases.Length * 2)];

        for (var index = 0; (index < cases.Length); index++) {
            var item = cases[index];

            rows[(index * 2)] = new Vector4(Bits(value: item.Low), Bits(value: item.High), Bits(value: item.Revision), 0);
            rows[((index * 2) + 1)] = new Vector4(Bits(value: item.Mask), Bits(value: (item.Completed ? 1u : 0u)), 0, 0);
        }
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder.Sphere(radius: 1, material: material).Build();
        var results = SdfIndirectDeviceProbe.Run(services, extension, "sdf-indirect-receiver-proof.comp", 5,
            Enumerable.Repeat(program, cases.Length).ToArray(), rows);

        for (var index = 0; (index < cases.Length); index++) {
            var item = cases[index];

            Assert.Equal(new Vector4(w: (item.Completed ? 8 : 0), x: (item.Valid ? 1 : 0),
                y: (item.Completed ? 2 : 0), z: (item.Completed ? item.Mask : 0)), results[index]);
            Assert.Equal((item.Valid ? new Vector4(w: 0.03125f, x: 1.25f, y: -0.125f, z: 8192f) : Vector4.Zero), results[(cases.Length + index)]);
            Assert.Equal(new Vector4(w: 0, x: 12345, y: 0, z: 0), results[((2 * cases.Length) + index)]);
            // A moved surface point keeps the certificate within its launch ball plus the acceptance threshold, never
            // beyond it, and an unresolved certificate (no clearance) only within the threshold.
            Assert.Equal(new Vector4(w: 0, x: 1, y: 0, z: 1), results[((3 * cases.Length) + index)]);
            // A withdrawn certificate is invalid even in its own scope.
            Assert.Equal(Vector4.Zero, results[((4 * cases.Length) + index)]);
        }
    }
}
