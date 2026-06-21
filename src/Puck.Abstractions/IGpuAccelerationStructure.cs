namespace Puck.Abstractions;

/// <summary>
/// A backend-neutral ray-tracing acceleration structure for the ray-query world: one static unit-AABB bottom-level
/// structure shared by every instance, and a top-level structure rebuilt over a persistently mapped instance buffer
/// each frame. It hides the backend's acceleration-structure build (Vulkan <c>VK_KHR_acceleration_structure</c> or
/// Direct3D 12 DXR 1.1) so a single ray-query render node drives either backend. Owned for its lifetime.
/// </summary>
public interface IGpuAccelerationStructure : IDisposable {
    /// <summary>Whether the device supports inline ray tracing (the gate for the whole ray-query path). When
    /// <see langword="false"/>, no other member may be called.</summary>
    bool IsSupported { get; }
    /// <summary>The backend-defined reference to the top-level structure, for binding it as a descriptor: a Vulkan
    /// <c>VkAccelerationStructureKHR</c> handle, or a Direct3D 12 TLAS GPU virtual address. Valid after
    /// <see cref="EnsureCreated"/>.</summary>
    nint TlasReference { get; }
    /// <summary>Creates the shared unit-AABB bottom-level structure, the per-frame top-level structure, and the
    /// persistently mapped instance buffer (capacity <paramref name="maxInstanceCount"/>). Idempotent.</summary>
    /// <param name="maxInstanceCount">The instance-buffer / top-level structure instance capacity.</param>
    void EnsureCreated(uint maxInstanceCount);
    /// <summary>Writes one instance into the mapped instance buffer: a per-axis scale (half-extent) plus translation
    /// over the shared unit-AABB structure, with the caller's instance index and visibility mask.</summary>
    /// <param name="index">The instance index within the buffer to write.</param>
    /// <param name="halfExtentX">The instance's half-extent along the world X axis.</param>
    /// <param name="halfExtentY">The instance's half-extent along the world Y axis.</param>
    /// <param name="halfExtentZ">The instance's half-extent along the world Z axis.</param>
    /// <param name="centerX">The instance's world-space center X coordinate.</param>
    /// <param name="centerY">The instance's world-space center Y coordinate.</param>
    /// <param name="centerZ">The instance's world-space center Z coordinate.</param>
    /// <param name="instanceIndex">The instance index reported by ray queries that hit this instance.</param>
    /// <param name="visibilityMask">The 8-bit visibility mask gating which rays may intersect this instance.</param>
    void WriteInstance(int index, float halfExtentX, float halfExtentY, float halfExtentZ, float centerX, float centerY, float centerZ, uint instanceIndex, uint visibilityMask);
    /// <summary>Records the per-frame top-level build (and, when <paramref name="includeBlasBuild"/>, the static
    /// bottom-level build) into the command buffer, with the barriers ordering the build before the ray-query
    /// dispatch.</summary>
    /// <param name="commandBufferHandle">The command-buffer handle the build is recorded into.</param>
    /// <param name="instanceCount">The number of leading instance-buffer entries to build over.</param>
    /// <param name="includeBlasBuild">Whether to prepend the static unit-AABB bottom-level build.</param>
    void RecordBuild(nint commandBufferHandle, uint instanceCount, bool includeBlasBuild);
}
