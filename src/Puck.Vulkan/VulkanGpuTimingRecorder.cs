using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuTimingRecorder"/> for Vulkan by forwarding to <see cref="IVulkanQueryPoolApi"/>. The
/// pool handle is the <c>VkQueryPool</c> directly. <see cref="ResolveTimestamps"/> is a no-op (results are read
/// straight from the pool); <see cref="ReadTimestamps"/> waits for and reads the raw ticks.
/// </summary>
public sealed class VulkanGpuTimingRecorder(IVulkanQueryPoolApi queryPoolApi) : IGpuTimingRecorder {
    /// <inheritdoc/>
    public void ResetTimestamps(nint deviceHandle, nint commandBufferHandle, nint poolHandle, uint firstQuery, uint queryCount) =>
        queryPoolApi.CmdResetQueryPool(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            firstQuery: firstQuery,
            queryCount: queryCount,
            queryPoolHandle: poolHandle
        );

    /// <inheritdoc/>
    public void WriteTimestamp(nint deviceHandle, nint commandBufferHandle, nint poolHandle, uint queryIndex, uint stageFlags) =>
        queryPoolApi.CmdWriteTimestamp(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            pipelineStage: ToVulkanStage(stage: stageFlags),
            query: queryIndex,
            queryPoolHandle: poolHandle
        );

    /// <inheritdoc/>
    public void ResolveTimestamps(nint deviceHandle, nint commandBufferHandle, nint poolHandle, uint firstQuery, uint queryCount) {
        // Vulkan reads results directly from the query pool — no resolve step.
    }

    /// <inheritdoc/>
    public uint ReadTimestamps(nint deviceHandle, nint poolHandle, uint firstQuery, uint queryCount, Span<ulong> rawTicks) {
        return queryPoolApi.GetTimestampResults(
            deviceHandle: deviceHandle,
            firstQuery: firstQuery,
            queryCount: queryCount,
            queryPoolHandle: poolHandle,
            results: rawTicks
        ).IsSuccess()
            ? queryCount
            : 0u;
    }

    private static uint ToVulkanStage(uint stage) {
        return (0 != (stage & GpuTimingStage.BottomOfPipe))
            ? VulkanPipelineStageFlags.BottomOfPipe
            : VulkanPipelineStageFlags.TopOfPipe;
    }
}
