namespace Puck.Hosting;

/// <summary>
/// One node in the recursive host tree. The contract is the same at every level: a node renders its own
/// pixels into a <see cref="Surface"/> and returns it, and it <em>may</em> itself host child nodes and
/// composite their surfaces into its own — a host "without knowing what produced them". The outermost
/// driver (the launcher/terminal) is just the node-driver that blits the root node's surface to the
/// swapchain.
/// </summary>
public interface IRenderNode : IDisposable {
    /// <summary>The node's identity.</summary>
    NodeDescriptor Descriptor { get; }

    /// <summary>Renders one frame and returns the surface the parent composites. The returned surface is
    /// valid until the next call.</summary>
    Surface ProduceFrame(in FrameContext context);
}
