using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The production terminal-source helper reads physical radiance only for an enabled certified exit.
/// A constant full map has an exact directional oracle; a different reflection plane exposes a wrong-plane read.</summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfIndirectSkyDeviceLawTests {
    [Fact]
    public void VulkanReadsThePhysicalSkyOnlyAtCertifiedEnabledExits() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfIndirectSkyDeviceLawTests));

        Verify(device.Services, ".spv");
    }
    [Fact]
    public void DirectXReadsThePhysicalSkyOnlyAtCertifiedEnabledExits() {
        using var output = new StringWriter();

        using (var device = DirectXTestDevices.Debug(output: output)) { Verify(device.Services, ".dxil"); }
        Assert.DoesNotContain("[d3d12-debug]", output.ToString(), StringComparison.Ordinal);
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        (IrradianceHitKind Kind, SdfIndirectSources Sources, Vector3 Direction, bool Lit, float Gain)[] cases = [
            (IrradianceHitKind.Exit, SdfIndirectSources.Sky, Vector3.UnitY, true, 1f),
            (IrradianceHitKind.Exit, SdfIndirectSources.All, -Vector3.UnitX, true, .25f),
            (IrradianceHitKind.Exit, SdfIndirectSources.Sky, -Vector3.UnitY, true, .5f),
            (IrradianceHitKind.Exit, SdfIndirectSources.Direct, Vector3.UnitY, false, 1f),
            (IrradianceHitKind.Hit, SdfIndirectSources.All, Vector3.UnitY, false, 1f),
            (IrradianceHitKind.Continuation, SdfIndirectSources.All, Vector3.UnitY, false, 1f),
            (IrradianceHitKind.Unresolved, SdfIndirectSources.All, Vector3.UnitY, false, 1f),
        ];
        var physical = new Vector3(x: .25f, y: .5f, z: 1f);
        var reflection = new Vector3(x: 8f, y: 4f, z: 2f);
        var map = new byte[SdfSkyEnvironment.MapBytes];

        for (var texel = 0; (texel < (2 * SdfSkyEnvironment.Texels)); texel++) {
            var color = ((texel < SdfSkyEnvironment.Texels) ? physical : reflection);

            for (var channel = 0; (channel < 3); channel++) {
                BinaryPrimitives.WriteUInt16LittleEndian(destination: map.AsSpan(start: ((texel * SdfSkyEnvironment.TexelBytes) + (channel * sizeof(ushort)))),
                    value: BitConverter.HalfToUInt16Bits(value: ((Half)color[channel])));
            }
        }
        var rows = new Vector4[(cases.Length * 2)];

        for (var index = 0; (index < cases.Length); index++) {
            rows[(2 * index)] = new Vector4(value: cases[index].Direction, w: BitConverter.UInt32BitsToSingle(value: ((uint)cases[index].Kind)));
            rows[((2 * index) + 1)] = new Vector4(BitConverter.UInt32BitsToSingle(value: ((uint)cases[index].Sources)), cases[index].Gain, 0, 0);
        }
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder.Sphere(radius: 1, material: material).Build();
        var results = SdfIndirectDeviceProbe.Run(services, extension, "sdf-indirect-sky-proof.comp", 2,
            Enumerable.Repeat(program, cases.Length).ToArray(), rows,
            passValues: new byte[SdfWorldInterfaces.IndirectParameters.SizeBytes], environment: map);

        for (var index = 0; (index < cases.Length); index++) {
            Assert.Equal((cases[index].Lit ? new Vector4(value: (physical * cases[index].Gain), w: 4f) : Vector4.Zero), results[index]);
            Assert.Equal(Vector4.Zero, results[(cases.Length + index)]);
        }
    }
}
