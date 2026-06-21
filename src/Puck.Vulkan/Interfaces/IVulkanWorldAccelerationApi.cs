using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// The ray-query world scene model expressed over the generic <see cref="IVulkanAccelerationStructureApi"/>: one
/// static unit-AABB BLAS shared by every instance, a TLAS rebuilt over the instance buffer each frame, and the
/// per-axis-scale instancing the world uses to place primitives. All the scene policy (the unit AABB, the
/// fast-trace BLAS / fast-build TLAS choice, the compute↔build barrier choreography, the instance
/// transform/visibility-mask layout) lives here, not in the generic wrapper. Vulkan-only — ray-query has no
/// Direct3D 12 / DXIL counterpart.
/// </summary>
public interface IVulkanWorldAccelerationApi {
    /// <summary>Whether the logical device was created with the acceleration-structure command set (the gate for the whole ray-query world path).</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <returns><see langword="true"/> if the device supports ray-query acceleration structures; otherwise, <see langword="false"/>.</returns>
    bool SupportsDevice(nint deviceHandle);
    /// <summary>Creates the shared unit-AABB BLAS, the per-frame TLAS, their backing and scratch buffers, and the persistently mapped instance buffer.</summary>
    /// <param name="request">The creation parameters, including the device handles and instance capacity.</param>
    /// <returns>The created acceleration-structure resources.</returns>
    VulkanWorldAccelerationResources CreateResources(VulkanWorldAccelerationCreateRequest request);
    /// <summary>Destroys the acceleration structures, unmaps the instance buffer, and frees all backing and scratch buffers.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the resources.</param>
    /// <param name="resources">The acceleration-structure resources to destroy.</param>
    void DestroyResources(nint deviceHandle, VulkanWorldAccelerationResources resources);
    /// <summary>Records the per-frame TLAS build over the first <paramref name="instanceCount"/> instance-buffer
    /// entries, barriered so the previous frame's ray queries retire first and the new TLAS is visible to the
    /// compute dispatches that follow. <paramref name="includeBlasBuild"/> prepends the static unit-AABB BLAS
    /// build — needed only in the first recording after the resources are created (its input never changes).</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the build commands are recorded into.</param>
    /// <param name="resources">The acceleration-structure resources to build into.</param>
    /// <param name="instanceCount">The number of leading instance-buffer entries to build the TLAS over.</param>
    /// <param name="includeBlasBuild">Whether to prepend the static unit-AABB BLAS build.</param>
    void RecordWorldAccelerationBuild(
        nint deviceHandle,
        nint commandBufferHandle,
        in VulkanWorldAccelerationResources resources,
        uint instanceCount,
        bool includeBlasBuild
    );
    /// <summary>Writes one <c>VkAccelerationStructureInstanceKHR</c> (64 bytes) into the mapped instance buffer:
    /// a per-axis scale plus translation transform over the shared unit-AABB BLAS, with the caller's custom index
    /// and visibility mask (the mask lets camera and shadow rays trace disjoint instance subsets).</summary>
    /// <param name="instanceBufferMappedPointer">The persistently mapped instance-buffer pointer to write through.</param>
    /// <param name="index">The instance index within the buffer to write.</param>
    /// <param name="scaleX">The instance's half-extent along the world X axis.</param>
    /// <param name="scaleY">The instance's half-extent along the world Y axis.</param>
    /// <param name="scaleZ">The instance's half-extent along the world Z axis.</param>
    /// <param name="worldCenterX">The instance's world-space center X coordinate.</param>
    /// <param name="worldCenterY">The instance's world-space center Y coordinate.</param>
    /// <param name="worldCenterZ">The instance's world-space center Z coordinate.</param>
    /// <param name="instanceCustomIndex">The application-defined 24-bit custom index reported by ray queries that hit this instance.</param>
    /// <param name="visibilityMask">The 8-bit visibility mask gating which rays may intersect this instance.</param>
    /// <param name="blasDeviceAddress">The device address of the bottom-level structure this instance references.</param>
    void WriteInstance(
        nint instanceBufferMappedPointer,
        int index,
        float scaleX,
        float scaleY,
        float scaleZ,
        float worldCenterX,
        float worldCenterY,
        float worldCenterZ,
        uint instanceCustomIndex,
        uint visibilityMask,
        ulong blasDeviceAddress
    );
}
