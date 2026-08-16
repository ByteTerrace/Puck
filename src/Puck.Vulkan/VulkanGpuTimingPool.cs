using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuTimingPool"/> for Vulkan, owning a <c>VkQueryPool</c> created for timestamp queries.
/// The neutral <see cref="PoolHandle"/> IS the <c>VkQueryPool</c> handle (the recorder verbs take it directly).
/// </summary>
public sealed class VulkanGpuTimingPool : IGpuTimingPool {
    private readonly nint m_deviceHandle;
    private readonly IVulkanQueryPoolApi m_queryPoolApi;

    private bool m_disposed;
    private nint m_poolHandle;

    /// <summary>Initializes a new instance of the <see cref="VulkanGpuTimingPool"/> class.</summary>
    /// <param name="queryPoolApi">The query-pool API used to destroy the pool.</param>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the pool.</param>
    /// <param name="poolHandle">The native <c>VkQueryPool</c> handle.</param>
    /// <param name="capacity">The number of timestamp queries the pool holds.</param>
    public VulkanGpuTimingPool(IVulkanQueryPoolApi queryPoolApi, nint deviceHandle, nint poolHandle, uint capacity) {
        ArgumentNullException.ThrowIfNull(queryPoolApi);

        m_deviceHandle = deviceHandle;
        m_poolHandle = poolHandle;
        m_queryPoolApi = queryPoolApi;
        Capacity = capacity;
    }

    /// <inheritdoc/>
    public uint Capacity { get; }
    /// <inheritdoc/>
    public nint PoolHandle => m_poolHandle;

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_queryPoolApi.DestroyQueryPool(deviceHandle: m_deviceHandle, queryPoolHandle: m_poolHandle);
        m_poolHandle = 0;
    }
}
