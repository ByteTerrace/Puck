using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Uses the production resource-only Near helper and its replacement fold. The emission oracle is
/// independently albedo times emission times bleed; resolved color, fog and specular are not source inputs.</summary>
[Collection(DebugLayerCollection.Name)]
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfIndirectNearDeviceLawTests {
    [Fact]
    public void VulkanVisitsEachHighCachePixelParityOnceAndExcludesOtherTiersAndMethods() =>
        Vulkan(verify: VerifyAdmission);
    [Fact]
    public void DirectXVisitsEachHighCachePixelParityOnceAndExcludesOtherTiersAndMethods() =>
        DirectX(verify: VerifyAdmission);
    [Fact]
    public void VulkanReplacesIncomingWithOnlyTheSmallObjectsColoredDiffuseEmission() =>
        Vulkan(verify: VerifyEmission);
    [Fact]
    public void DirectXReplacesIncomingWithOnlyTheSmallObjectsColoredDiffuseEmission() =>
        DirectX(verify: VerifyEmission);
    [Fact]
    public void VulkanSharesTwelveQueriesAcrossTransportLaunchAndProof() => Vulkan(verify: VerifyAllowance);
    [Fact]
    public void DirectXSharesTwelveQueriesAcrossTransportLaunchAndProof() => DirectX(verify: VerifyAllowance);
    [Fact]
    public void VulkanKeepsWholeFallbackForInsideUnsupportedAndUnpublishedResponses() => Vulkan(verify: VerifyFallback);
    [Fact]
    public void DirectXKeepsWholeFallbackForInsideUnsupportedAndUnpublishedResponses() => DirectX(verify: VerifyFallback);
    [Fact]
    public void VulkanCountsDirectionAndRejectedContinuationLoadsAndHashes() => Vulkan(verify: VerifyResourceCounts);
    [Fact]
    public void DirectXCountsDirectionAndRejectedContinuationLoadsAndHashes() => DirectX(verify: VerifyResourceCounts);

    private static readonly Vector3 Emission = new(x: 0.5f, y: 0.5f, z: 0.375f);
    private static readonly Vector3 Center = new(x: 0, y: 0, z: 0.25f);

    private static void Vulkan(Action<GpuDeviceServices, string> verify) {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfIndirectNearDeviceLawTests));

        verify(device.Services, ".spv");
    }
    private static void DirectX(Action<GpuDeviceServices, string> verify) {
        using var output = new StringWriter();

        using (var device = DirectXTestDevices.Debug(output: output)) { verify(device.Services, ".dxil"); }
        Assert.DoesNotContain("[d3d12-debug]", output.ToString(), StringComparison.Ordinal);
    }
    private static Vector4[] Run(GpuDeviceServices services, string extension, IReadOnlyList<SdfProgram> programs,
        Vector4[] rays, SdfIndirectSources sources = SdfIndirectSources.Emission, uint previous = 0, float emissionGain = 1f) {
        var parameters = SdfWorldInterfaces.WorldParameters;
        var values = new byte[parameters.SizeBytes];

        void Write(string name, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(
            destination: values.AsSpan(start: ((int)parameters.BlockOffsetOf(member: name))), value: value);
        Write(name: SdfWorldPackage.IndirectTier, value: ((uint)SdfIndirectTier.High));
        Write(name: SdfWorldPackage.IndirectBodies, value: ((uint)SdfIndirectParticipation.Cast));
        Write(name: SdfWorldPackage.IndirectSources, value: ((uint)sources));
        var gainOffset = ((int)parameters.BlockOffsetOf(member: SdfWorldPackage.IndirectSourceGains));

        for (var channel = 0; (channel < 4); channel++) { BinaryPrimitives.WriteSingleLittleEndian(destination: values.AsSpan(start: (gainOffset + (channel * sizeof(float)))), value: ((channel == 1) ? emissionGain : 1f)); }
        BinaryPrimitives.WriteSingleLittleEndian(destination: values.AsSpan(start: ((int)parameters.BlockOffsetOf(member: SdfWorldPackage.IndirectFeedbackGain))), value: 1f);
        Write(name: SdfWorldPackage.IndirectNearEnabled, value: 1);
        Write(name: SdfWorldPackage.IndirectPreviousPublication, value: previous);
        return SdfIndirectDeviceProbe.Run(services, extension, "sdf-indirect-near-proof.comp", 11,
            programs, rays, passValues: values, worldParameters: true);
    }
    private static void VerifyAdmission(GpuDeviceServices services, string extension) {
        var result = Run(services, extension, [Sphere(Center)], [Vector4.Zero, new(value: Vector3.UnitZ, w: 0)]);

        Assert.Equal(new Vector4(w: 8, x: 1, y: 2, z: 4), result[7]);
        Assert.Equal(Vector4.Zero, result[8]);
    }
    private static void VerifyResourceCounts(GpuDeviceServices services, string extension) {
        var result = Run(services, extension, [Sphere(Center)], [Vector4.Zero, new(value: Vector3.UnitZ, w: 0)]);

        Assert.Equal(new Vector4(w: 0, x: 1, y: 1, z: 0), result[9]);
        // The fixture's first trace-state word is zero: no ray is eligible, but the actual state load still counts.
        Assert.Equal(new Vector4(w: 0, x: 1, y: 0, z: -1), result[10]);
        Assert.Equal(2f, (result[9].X + result[10].X));
        Assert.Equal(1f, (result[9].Y + result[10].Y));
    }
    private static void VerifyEmission(GpuDeviceServices services, string extension) {
        var result = Run(services, extension, [Sphere(Center)], [Vector4.Zero, new(value: Vector3.UnitZ, w: 0)], emissionGain: .25f);

        Assert.Equal(1f, result[0].W);
        Near((Emission * .25f), result[0], extension);
        Assert.Equal(1f, result[1].Z);
        for (var source = 0; (source < 5); source++) {
            Near(((source == 2) ? (Emission * .25f) : Vector3.Zero), result[(2 + source)], extension);
        }
    }
    private static void VerifyAllowance(GpuDeviceServices services, string extension) {
        var result = Run(services, extension, [Sphere(Center)], [Vector4.Zero, new(value: Vector3.UnitZ, w: 0)],
            SdfIndirectSources.Emission | SdfIndirectSources.Feedback, previous: 1);
        // The preceding bank is requested but the directory has no resident support. Launch really executes;
        // proof refuses missing support. Every reported query must still belong to the same twelve-query budget.
        Assert.True(condition: (result[1].W > 0f), userMessage: $"{extension}: no secondary launch was exercised: {result[1]}");
        Assert.Equal(result[1].X, result[1].Y);
        Assert.InRange(result[1].X, 1f, 12f);
        Assert.Equal(0f, result[0].W);
        AssertWholeFallback(columns: 1, extension: extension, index: 0, result: result);
    }
    private static void VerifyFallback(GpuDeviceServices services, string extension) {
        SdfProgram[] programs = [Sphere(Center), Sphere(Center, screen: true), Sphere(new Vector3(x: 0, y: 0, z: 2))];
        Vector4[] rays = [new(value: Center, w: 0), new(value: Vector3.UnitZ, w: 0), Vector4.Zero, new(value: Vector3.UnitZ, w: 0),
            Vector4.Zero, new(value: Vector3.UnitZ, w: 0)];
        var result = Run(services, extension, programs, rays);

        for (var index = 0; (index < programs.Length); index++) {
            Assert.Equal(0f, result[index].W);
            AssertWholeFallback(result, programs.Length, index, extension);
        }
        var unpublished = Run(services, extension, [Sphere(Center)], [Vector4.Zero, new(value: Vector3.UnitZ, w: 0)],
            SdfIndirectSources.Emission | SdfIndirectSources.Feedback);

        Assert.Equal(0f, unpublished[0].W);
        AssertWholeFallback(columns: 1, extension: extension, index: 0, result: unpublished);
    }
    private static void AssertWholeFallback(Vector4[] result, int columns, int index, string extension) {
        for (var source = 0; (source < 5); source++) {
            Near(((source + 1) * new Vector3(x: 2, y: 3, z: 4)), result[(((2 + source) * columns) + index)], extension);
        }
    }
    private static void Near(Vector3 expected, Vector4 actual, string extension) {
        Assert.InRange(MathF.Abs(x: (expected.X - actual.X)), 0, 0.00001f);
        Assert.InRange(MathF.Abs(x: (expected.Y - actual.Y)), 0, 0.00001f);
        Assert.True(condition: (MathF.Abs(x: (expected.Z - actual.Z)) <= 0.00001f), userMessage: $"{extension}: expected {expected}, actual {actual}");
    }
    private static SdfProgram Sphere(Vector3 center, bool screen = false) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.25f, y: 0.5f, z: 0.75f), Emissive: 2,
            Bleed: new Vector3(x: 1, y: 0.5f, z: 0.25f), Specular: 1, Sheen: 1, Coat: 1, Fill: new Vector3(x: 10, y: 20, z: 30)));

        builder.Translate(offset: center).Sphere(0.05f, (screen ? SdfProgramBuilder.ScreenMaterialId : material));
        return builder.Build();
    }
}
