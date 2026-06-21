using Puck.Abstractions;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>
/// A render node that produces nothing — the empty surface. Stands in when the showcase cannot build a real
/// producer (e.g. the host starts on Direct3D 12, or Vulkan is not available to host the window).
/// </summary>
internal sealed class NullRenderNode : IRenderNode {
    private readonly NodeDescriptor m_descriptor = new(
        Name: "blank",
        SurfaceId: SurfaceId.New()
    );

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public void Dispose() { }
    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        return default;
    }
}
