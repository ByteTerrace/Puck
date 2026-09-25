using Puck.Abstractions.Presentation;
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
    /// <summary>Releases the node's device-derived GPU resources after the graphics device was lost, so the next
    /// <see cref="ProduceFrame"/> rebuilds them against the replacement device. The default is a no-op (for nodes that own
    /// no device resources). A node that HOSTS children MUST override this to forward the call to each child (so the whole
    /// subtree resets), a node that owns GPU resources must release them and clear any "resources built" latch here, and a
    /// node holding an armed capture refuses it (<c>CaptureRequestSlot.RefuseForDeviceLoss</c>), since the frame it was
    /// owed is not produced on the lost device. Called on the pump thread during device-loss recovery, before the device
    /// is rebuilt in place, so every object is released while its device still exists; it must NOT touch the
    /// simulation.</summary>
    void OnDeviceLost() { }
}
