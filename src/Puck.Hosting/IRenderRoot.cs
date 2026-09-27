using Puck.Abstractions.Presentation;
namespace Puck.Hosting;

/// <summary>
/// A host's render root: the one object a host produces each frame's surface from, presents or captures, releases on
/// device loss and disposes at teardown. The World's is the render graph runtime's node, and everything it shows is an
/// instance of that graph, so a root hosts no children of its own.
/// </summary>
public interface IRenderRoot : IDisposable {
    /// <summary>Renders one frame and returns the surface the host presents. The returned surface is valid until the next
    /// call.</summary>
    /// <param name="context">The frame's fixed-step and presentation context.</param>
    /// <returns>The frame's surface, or an empty surface when the root has nothing to present yet.</returns>
    Surface ProduceFrame(in FrameContext context);
    /// <summary>Releases the root's device-derived GPU resources after the graphics device was lost, so the next
    /// <see cref="ProduceFrame"/> rebuilds them against the replacement device. The default is a no-op (for a root that
    /// owns no device resources). A root that owns GPU resources releases them and clears any "resources built" latch
    /// here, and a root holding an armed capture refuses it (<c>CaptureRequestSlot.RefuseForDeviceLoss</c>), since the
    /// frame it was owed is not produced on the lost device. Called on the pump thread during device-loss recovery,
    /// before the device is rebuilt in place, so every object is released while its device still exists; it never
    /// touches the simulation.</summary>
    void OnDeviceLost() { }
}
