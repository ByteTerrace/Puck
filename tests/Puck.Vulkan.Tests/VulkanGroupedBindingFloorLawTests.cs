using Puck.Abstractions.Gpu;
using Puck.Vulkan.Factories;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Laws for the Vulkan device floor the grouped binding contract sets
/// (<see cref="VulkanLogicalDeviceFactory.RequireGroupedBinding"/>): a device reporting at least
/// <see cref="GpuPipelineLayoutDescription.GroupCount"/> descriptor sets and
/// <see cref="GpuPipelineLayoutDescription.PushIndexBytes"/> push-constant bytes is accepted, and one below either is
/// refused as unavailable, naming each limit it misses; and a device that does not report
/// <c>shaderSampledImageArrayDynamicIndexing</c>, which the SDF screen shading needs, or <c>fragmentStoresAndAtomics</c>,
/// with which the SDF mesh pass counts its texels, or <c>shaderStorageImageExtendedFormats</c>, which the R8/R8G8 shadow
/// handoff storage images require, is refused by name
/// (<see cref="VulkanLogicalDeviceFactory.FeatureIndicesOf"/>).</summary>
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
    // A device reporting every base feature but a required one, or reporting too few features to name it, is refused
    // naming the feature; one reporting every required feature
    // enables them first, then the optional features it reports.
    [InlineData(34, "shaderSampledImageArrayDynamicIndexing")]
    [InlineData(26, "fragmentStoresAndAtomics")]
    [InlineData(29, "shaderStorageImageExtendedFormats")]
    [Theory]
    public void ADeviceWithoutARequiredBaseFeatureIsRefusedByName(int required, string feature) {
        const int SampledImageArrayDynamicIndexing = 34;
        const int FragmentStoresAndAtomics = 26;
        const int StorageImageExtendedFormats = 29;
        var every = Enumerable.Repeat(count: 55, element: true).ToArray();
        var without = every.ToArray();

        without[required] = false;

        foreach (var support in new[] { without, every[..required] }) {
            var refusal = Assert.Throws<GpuDeviceUnavailableException>(testCode: () => VulkanLogicalDeviceFactory.FeatureIndicesOf(support: support));

            Assert.Contains(
                actualString: refusal.Message,
                expectedSubstring: $"does not report {feature}"
            );
        }

        Assert.Equal(
            actual: VulkanLogicalDeviceFactory.FeatureIndicesOf(support: every).Take(count: 3),
            expected: [((uint)FragmentStoresAndAtomics), ((uint)StorageImageExtendedFormats), ((uint)SampledImageArrayDynamicIndexing)]
        );
        Assert.Equal(
            actual: VulkanLogicalDeviceFactory.FeatureIndicesOf(support: [.. Enumerable.Range(count: 55, start: 0).Select(selector: static index => (index is SampledImageArrayDynamicIndexing or FragmentStoresAndAtomics or StorageImageExtendedFormats))]),
            expected: [((uint)FragmentStoresAndAtomics), ((uint)StorageImageExtendedFormats), ((uint)SampledImageArrayDynamicIndexing)]
        );
    }
}
