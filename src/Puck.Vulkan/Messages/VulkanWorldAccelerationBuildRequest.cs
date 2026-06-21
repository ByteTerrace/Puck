namespace Puck.Vulkan.Messages;

/// <summary>
/// The world acceleration-structure work for one frame (ray-query mode only): the recorder replays the TLAS build
/// once, before any view's compute dispatch, over the instance-buffer entries the world wrote this frame. The
/// static unit-AABB BLAS rides along in the first recording generation; once a recording carrying it is confirmed
/// submitted it can be dropped, since recordings happen post-fence-wait and a submitted build has retired before
/// any later generation's TLAS build consumes the BLAS.
/// </summary>
/// <param name="Resources">The acceleration-structure resources the build targets.</param>
/// <param name="InstanceCount">The number of leading instance-buffer entries to build the TLAS over this frame.</param>
/// <param name="IncludeBlasBuild">Whether to prepend the static unit-AABB BLAS build (needed only until a recording carrying it is confirmed submitted).</param>
public readonly record struct VulkanWorldAccelerationBuildRequest(
    VulkanWorldAccelerationResources Resources,
    uint InstanceCount,
    bool IncludeBlasBuild = true
);
