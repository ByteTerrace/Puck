using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Manages the host-visible staging buffers used to read rendered frame contents back to the CPU.
/// </summary>
public interface IVulkanFrameReadbackApi {
    /// <summary>Creates a host-visible readback buffer.</summary>
    /// <param name="request">The readback buffer creation parameters.</param>
    /// <returns>The created <see cref="VulkanFrameReadbackBuffer"/>.</returns>
    VulkanFrameReadbackBuffer CreateBuffer(VulkanFrameReadbackBufferCreateRequest request);
    /// <summary>Destroys a readback buffer and frees its memory.</summary>
    /// <param name="request">The destroy parameters identifying the buffer to release.</param>
    void DestroyBuffer(VulkanFrameReadbackBufferDestroyRequest request);
    /// <summary>Reads the contents of a readback buffer into a managed array.</summary>
    /// <param name="buffer">The readback buffer to read from.</param>
    /// <returns>A copy of the buffer's bytes.</returns>
    byte[] ReadBuffer(VulkanFrameReadbackBuffer buffer);
}
