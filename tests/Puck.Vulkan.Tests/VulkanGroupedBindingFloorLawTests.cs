using Puck.Abstractions.Gpu;
using Puck.Vulkan.Factories;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Laws for the Vulkan device floor the grouped binding contract sets
/// (<see cref="VulkanLogicalDeviceFactory.RequireGroupedBinding"/>): a device reporting at least
/// <see cref="GpuPipelineLayoutDescription.GroupCount"/> descriptor sets and
/// <see cref="GpuPipelineLayoutDescription.PushIndexBytes"/> push-constant bytes is accepted, and one below either is
/// refused as unavailable, naming each limit it misses.</summary>
public sealed class VulkanGroupedBindingFloorLawTests {
    private static GpuDeviceCapabilities Device(uint sets, uint pushBytes) => new(
        Backend: "vulkan",
        MaxBoundDescriptorSets: sets,
        MaxPerStageResources: 0U,
        MaxPerStageSampledImages: 0U,
        MaxPerStageSamplers: 0U,
        MaxPerStageStorageBuffers: 0U,
        MaxPerStageStorageImages: 0U,
        MaxPerStageUniformBuffers: 0U,
        MaxPushConstantBytes: pushBytes,
        MaxRootSignatureWords: 0U
    );

    [Fact]
    public void ADeviceAtTheFloorIsAccepted() =>
        VulkanLogicalDeviceFactory.RequireGroupedBinding(capabilities: Device(
            pushBytes: GpuPipelineLayoutDescription.PushIndexBytes,
            sets: GpuPipelineLayoutDescription.GroupCount
        ));
    [InlineData(3U, 4U, "maxBoundDescriptorSets is 3, below the 4 binding groups.")]
    [InlineData(4U, 0U, "maxPushConstantsSize is 0, below the 4-byte pushed index.")]
    [InlineData(1U, 2U, "maxBoundDescriptorSets is 1, below the 4 binding groups; maxPushConstantsSize is 2, below the 4-byte pushed index.")]
    [Theory]
    public void ADeviceBelowTheFloorIsRefusedByNameNamingEachLimit(uint sets, uint pushBytes, string missing) {
        var refusal = Assert.Throws<GpuDeviceUnavailableException>(testCode: () => VulkanLogicalDeviceFactory.RequireGroupedBinding(capabilities: Device(
            pushBytes: pushBytes,
            sets: sets
        )));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: $"The Vulkan device cannot bind Puck's grouped binding contract: {missing}"
        );
    }
}
