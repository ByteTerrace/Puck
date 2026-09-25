using System.Text;

using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuDeviceCapabilities"/>: Direct3D 12's per-stage limits follow its resource binding tier's
/// documented table, a runtime that reports no heap sizes reports the sizes every tier guarantees, and the one-line
/// fields leave out what a backend does not report.
/// </summary>
public sealed class GpuDeviceCapabilitiesLawTests {
    [Theory]
    [InlineData(1U, 16U, 14U, 128U, 64U)]
    [InlineData(2U, 4096U, 14U, 2000000U, 64U)]
    [InlineData(3U, 4096U, 2000000U, 2000000U, 2000000U)]
    public void DirectXStageLimitsFollowTheBindingTier(uint tier, uint samplers, uint constantBuffers, uint shaderResources, uint unorderedAccess) {
        var capabilities = GpuDeviceCapabilities.FromDirectX(
            resourceBindingTier: tier,
            rootSignatureVersion: "1.1",
            samplerHeapSize: 4096U,
            shaderModel: "6.6",
            viewHeapSize: 2000000U
        );

        Assert.Equal(
            expected: (samplers, constantBuffers, shaderResources, unorderedAccess, unorderedAccess),
            actual: (capabilities.MaxPerStageSamplers, capabilities.MaxPerStageUniformBuffers, capabilities.MaxPerStageSampledImages, capabilities.MaxPerStageStorageBuffers, capabilities.MaxPerStageStorageImages)
        );
        Assert.Equal(
            expected: (0U, 64U, 256U, 0U),
            actual: (capabilities.MaxBoundDescriptorSets, capabilities.MaxRootSignatureWords, capabilities.MaxPushConstantBytes, capabilities.MaxPerStageResources)
        );
    }
    [Fact]
    public void AnUnansweredHeapQueryReportsTheGuaranteedSizes() {
        var capabilities = GpuDeviceCapabilities.FromDirectX(
            resourceBindingTier: 3U,
            rootSignatureVersion: "1.1",
            samplerHeapSize: 0U,
            shaderModel: "6.6",
            viewHeapSize: 0U
        );

        Assert.Equal(
            expected: (GpuDeviceCapabilities.DirectXMinimumViewHeapSize, GpuDeviceCapabilities.DirectXMinimumSamplerHeapSize),
            actual: (capabilities.ViewHeapSize, capabilities.SamplerHeapSize)
        );
    }
    [Theory]
    [InlineData(0U)]
    [InlineData(4U)]
    public void AnUnknownBindingTierIsRefused(uint tier) =>
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GpuDeviceCapabilities.FromDirectX(
            resourceBindingTier: tier,
            rootSignatureVersion: "1.1",
            samplerHeapSize: 0U,
            shaderModel: "6.6",
            viewHeapSize: 0U
        ));
    [Fact]
    public void TheFieldsLeaveOutWhatTheBackendDoesNotReport() =>
        Assert.Equal(
            expected: " backend=vulkan descriptor-sets=32 push-constant-bytes=256 stage.samplers=1 stage.uniform-buffers=2 stage.storage-buffers=3 stage.sampled-images=4 stage.storage-images=5 stage.resources=6",
            actual: new GpuDeviceCapabilities(
                Backend: "vulkan",
                MaxBoundDescriptorSets: 32U,
                MaxPerStageResources: 6U,
                MaxPerStageSampledImages: 4U,
                MaxPerStageSamplers: 1U,
                MaxPerStageStorageBuffers: 3U,
                MaxPerStageStorageImages: 5U,
                MaxPerStageUniformBuffers: 2U,
                MaxPushConstantBytes: 256U,
                MaxRootSignatureWords: 0U
            ).AppendFields(builder: new StringBuilder()).ToString()
        );
}
