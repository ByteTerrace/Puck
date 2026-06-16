using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native timestamp query pool entry points: creating a pool, recording reset and timestamp-write
/// commands, and reading the results back.
/// </summary>
public interface IVulkanQueryPoolApi {
    /// <summary>Creates a query pool for timestamp queries.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="queryCount">The number of timestamp queries the pool can hold.</param>
    /// <param name="queryPoolHandle">When this method returns, the created native <c>VkQueryPool</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the pool was created successfully.</returns>
    VkResult CreateTimestampPool(nint deviceHandle, uint queryCount, out nint queryPoolHandle);
    /// <summary>Destroys a query pool.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the pool.</param>
    /// <param name="queryPoolHandle">The native <c>VkQueryPool</c> handle to destroy.</param>
    void DestroyQueryPool(nint deviceHandle, nint queryPoolHandle);
    /// <summary>Records a command that resets a range of queries to the unavailable state.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="queryPoolHandle">The native <c>VkQueryPool</c> handle containing the queries.</param>
    /// <param name="firstQuery">The index of the first query to reset.</param>
    /// <param name="queryCount">The number of queries to reset.</param>
    void CmdResetQueryPool(nint deviceHandle, nint commandBufferHandle, nint queryPoolHandle, uint firstQuery, uint queryCount);
    /// <summary>Records a command that writes a timestamp into a query once the given pipeline stage completes.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="pipelineStage">The pipeline stage, as a <c>VkPipelineStageFlagBits</c> value, after which the timestamp is written.</param>
    /// <param name="queryPoolHandle">The native <c>VkQueryPool</c> handle that receives the timestamp.</param>
    /// <param name="query">The index of the query within the pool to write.</param>
    void CmdWriteTimestamp(nint deviceHandle, nint commandBufferHandle, uint pipelineStage, nint queryPoolHandle, uint query);
    /// <summary>Reads timestamp values back from a query pool.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="queryPoolHandle">The native <c>VkQueryPool</c> handle to read from.</param>
    /// <param name="firstQuery">The index of the first query to read.</param>
    /// <param name="queryCount">The number of queries to read.</param>
    /// <param name="results">A span that receives the raw timestamp values.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the results were available and read successfully.</returns>
    VkResult GetTimestampResults(nint deviceHandle, nint queryPoolHandle, uint firstQuery, uint queryCount, Span<ulong> results);
}
