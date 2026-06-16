using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Queries the compile-time statistics of a pipeline's executables, exposed through the
/// <c>VK_KHR_pipeline_executable_properties</c> extension when it is available.
/// </summary>
public interface IVulkanPipelineStatisticsApi {
    /// <summary>Determines whether pipeline executable statistics can be queried on the given device.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <returns><see langword="true"/> if the device supports pipeline executable statistics; otherwise, <see langword="false"/>.</returns>
    bool IsSupported(nint deviceHandle);
    /// <summary>Queries the executable statistics of a pipeline.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="pipelineHandle">The native <c>VkPipeline</c> handle to query.</param>
    /// <returns>The statistics reported for each of the pipeline's executables.</returns>
    IReadOnlyList<VulkanPipelineExecutableStatistic> QueryStatistics(nint deviceHandle, nint pipelineHandle);
}
