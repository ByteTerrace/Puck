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

[Collection(DebugLayerCollection.Name)]
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfIndirectParticipationDeviceLawTests {
    [Fact]
    public void VulkanAppliesTierAndPlacementPoliciesOnlyToIndirectQueries() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfIndirectParticipationDeviceLawTests));
        Verify(device.Services, ".spv");
    }
    [Fact]
    public void DirectXAppliesTierAndPlacementPoliciesOnlyToIndirectQueries() {
        using var output = new StringWriter();
        using (var device = DirectXTestDevices.Debug(output: output)) { Verify(device.Services, ".dxil"); }
        Assert.DoesNotContain("[d3d12-debug]", output.ToString(), StringComparison.Ordinal);
    }
    private static void Verify(GpuDeviceServices services, string extension) {
        var programs = new List<SdfProgram>();
        foreach (var dynamic in new[] { false, true }) {
            foreach (var policy in Enum.GetValues<SdfIndirectParticipation>()) {
                var builder = new SdfProgramBuilder();
                var material = builder.AddMaterial(new SdfMaterial(Vector3.One));
                if (dynamic) { builder.BeginInstanceDynamic(0, Vector3.Zero, 1f, indirect: policy).ResetPoint().TransformDynamic(0); }
                else { builder.BeginInstance(Vector3.Zero, 1f, indirect: policy).ResetPoint(); }
                builder.Sphere(0.5f, material).EndInstance();
                programs.Add(builder.Build());
            }
        }
        foreach (var (tier, bodies) in new[] {
            (SdfIndirectTier.Medium, SdfIndirectParticipation.Default), (SdfIndirectTier.High, SdfIndirectParticipation.Default),
            (SdfIndirectTier.High, SdfIndirectParticipation.Off), (SdfIndirectTier.Medium, SdfIndirectParticipation.Cast),
        }) {
            var parameters = SdfWorldInterfaces.IndirectParameters;
            var values = new byte[parameters.SizeBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(values.AsSpan((int)parameters.BlockOffsetOf(SdfWorldPackage.IndirectBodies)), (uint)bodies);
            BinaryPrimitives.WriteUInt32LittleEndian(values.AsSpan((int)parameters.BlockOffsetOf(SdfWorldPackage.IndirectTier)), (uint)tier);
            var result = SdfIndirectDeviceProbe.Run(services, extension, "sdf-indirect-participation.comp", 1,
                programs, [Vector4.Zero], [new Vector4(0f, 0f, 0f, 1f), new Vector4(0f, 0f, 0f, 1f), Vector4.Zero], passValues: values);
            for (var index = 0; index < result.Length; index++) {
                var casts = index % 4 == 1 || index % 4 == 0 && (index < 4 || bodies == SdfIndirectParticipation.Cast ||
                    bodies == SdfIndirectParticipation.Default && tier == SdfIndirectTier.High);
                Assert.Equal(-0.5f, result[index].X);
                Assert.Equal(-0.5f, result[index].Z);
                Assert.True(casts ? result[index].Y == -0.5f : result[index].Y > 1e6f,
                    $"{extension} tier {tier}, body default {bodies}, instance {index}: {result[index]}");
                Assert.Equal(casts ? 1f : 0f, result[index].W);
            }
        }
    }
}
