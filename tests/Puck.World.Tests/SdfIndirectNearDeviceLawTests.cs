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
        Vulkan(VerifyAdmission);
    [Fact]
    public void DirectXVisitsEachHighCachePixelParityOnceAndExcludesOtherTiersAndMethods() =>
        DirectX(VerifyAdmission);
    [Fact]
    public void VulkanReplacesIncomingWithOnlyTheSmallObjectsColoredDiffuseEmission() =>
        Vulkan(VerifyEmission);
    [Fact]
    public void DirectXReplacesIncomingWithOnlyTheSmallObjectsColoredDiffuseEmission() =>
        DirectX(VerifyEmission);
    [Fact]
    public void VulkanSharesTwelveQueriesAcrossTransportLaunchAndProof() => Vulkan(VerifyAllowance);
    [Fact]
    public void DirectXSharesTwelveQueriesAcrossTransportLaunchAndProof() => DirectX(VerifyAllowance);
    [Fact]
    public void VulkanKeepsWholeFallbackForInsideUnsupportedAndUnpublishedResponses() => Vulkan(VerifyFallback);
    [Fact]
    public void DirectXKeepsWholeFallbackForInsideUnsupportedAndUnpublishedResponses() => DirectX(VerifyFallback);
    [Fact]
    public void VulkanCountsDirectionAndRejectedContinuationLoadsAndHashes() => Vulkan(VerifyResourceCounts);
    [Fact]
    public void DirectXCountsDirectionAndRejectedContinuationLoadsAndHashes() => DirectX(VerifyResourceCounts);

    private static readonly Vector3 Emission = new(0.5f, 0.5f, 0.375f);
    private static readonly Vector3 Center = new(0, 0, 0.25f);
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
        Vector4[] rays, SdfIndirectSources sources = SdfIndirectSources.Emission, uint previous = 0) {
        var parameters = SdfWorldInterfaces.WorldParameters;
        var values = new byte[parameters.SizeBytes];
        void Write(string name, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(
            values.AsSpan((int)parameters.BlockOffsetOf(name)), value);
        Write(SdfWorldPackage.IndirectTier, (uint)SdfIndirectTier.High);
        Write(SdfWorldPackage.IndirectBodies, (uint)SdfIndirectParticipation.Cast);
        Write(SdfWorldPackage.IndirectSources, (uint)sources);
        Write(SdfWorldPackage.IndirectNearEnabled, 1);
        Write(SdfWorldPackage.IndirectPreviousPublication, previous);
        return SdfIndirectDeviceProbe.Run(services, extension, "sdf-indirect-near-proof.comp", 11,
            programs, rays, passValues: values, worldParameters: true);
    }
    private static void VerifyAdmission(GpuDeviceServices services, string extension) {
        var result = Run(services, extension, [Sphere(Center)], [Vector4.Zero, new(Vector3.UnitZ, 0)]);
        Assert.Equal(new Vector4(1, 2, 4, 8), result[7]);
        Assert.Equal(Vector4.Zero, result[8]);
    }
    private static void VerifyResourceCounts(GpuDeviceServices services, string extension) {
        var result = Run(services, extension, [Sphere(Center)], [Vector4.Zero, new(Vector3.UnitZ, 0)]);
        Assert.Equal(new Vector4(1, 1, 0, 0), result[9]);
        // The fixture's first trace-state word is zero: no ray is eligible, but the actual state load still counts.
        Assert.Equal(new Vector4(1, 0, -1, 0), result[10]);
        Assert.Equal(2f, result[9].X + result[10].X);
        Assert.Equal(1f, result[9].Y + result[10].Y);
    }
    private static void VerifyEmission(GpuDeviceServices services, string extension) {
        var result = Run(services, extension, [Sphere(Center)], [Vector4.Zero, new(Vector3.UnitZ, 0)]);
        Assert.Equal(1f, result[0].W);
        Near(Emission, result[0], extension);
        Assert.Equal(1f, result[1].Z);
        for (var source = 0; source < 5; source++) {
            Near(source == 2 ? Emission : Vector3.Zero, result[2 + source], extension);
        }
    }
    private static void VerifyAllowance(GpuDeviceServices services, string extension) {
        var result = Run(services, extension, [Sphere(Center)], [Vector4.Zero, new(Vector3.UnitZ, 0)],
            SdfIndirectSources.Emission | SdfIndirectSources.Feedback, previous: 1);
        // The preceding bank is requested but the directory has no resident support. Launch really executes;
        // proof refuses missing support. Every reported query must still belong to the same twelve-query budget.
        Assert.True(result[1].W > 0f, $"{extension}: no secondary launch was exercised: {result[1]}");
        Assert.Equal(result[1].X, result[1].Y);
        Assert.InRange(result[1].X, 1f, 12f);
        Assert.Equal(0f, result[0].W);
        AssertWholeFallback(result, 1, 0, extension);
    }
    private static void VerifyFallback(GpuDeviceServices services, string extension) {
        SdfProgram[] programs = [Sphere(Center), Sphere(Center, screen: true), Sphere(new Vector3(0, 0, 2))];
        Vector4[] rays = [new(Center, 0), new(Vector3.UnitZ, 0), Vector4.Zero, new(Vector3.UnitZ, 0),
            Vector4.Zero, new(Vector3.UnitZ, 0)];
        var result = Run(services, extension, programs, rays);
        for (var index = 0; index < programs.Length; index++) {
            Assert.Equal(0f, result[index].W);
            AssertWholeFallback(result, programs.Length, index, extension);
        }
        var unpublished = Run(services, extension, [Sphere(Center)], [Vector4.Zero, new(Vector3.UnitZ, 0)],
            SdfIndirectSources.Emission | SdfIndirectSources.Feedback);
        Assert.Equal(0f, unpublished[0].W);
        AssertWholeFallback(unpublished, 1, 0, extension);
    }
    private static void AssertWholeFallback(Vector4[] result, int columns, int index, string extension) {
        for (var source = 0; source < 5; source++) {
            Near((source + 1) * new Vector3(2, 3, 4), result[(2 + source) * columns + index], extension);
        }
    }
    private static void Near(Vector3 expected, Vector4 actual, string extension) {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0, 0.00001f);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0, 0.00001f);
        Assert.True(MathF.Abs(expected.Z - actual.Z) <= 0.00001f, $"{extension}: expected {expected}, actual {actual}");
    }
    private static SdfProgram Sphere(Vector3 center, bool screen = false) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: new Vector3(0.25f, 0.5f, 0.75f), Emissive: 2,
            Bleed: new Vector3(1, 0.5f, 0.25f), Specular: 1, Sheen: 1, Coat: 1, Fill: new Vector3(10, 20, 30)));
        builder.Translate(center).Sphere(0.05f, screen ? SdfProgramBuilder.ScreenMaterialId : material);
        return builder.Build();
    }
}
