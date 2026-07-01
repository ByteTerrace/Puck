namespace Puck.Abstractions;

/// <summary>
/// An optional <see cref="ISurfacePresenter"/> capability: the presenter can rebuild its graphics device and all
/// device-derived presentation resources after a <see cref="DeviceLostException"/>, so rendering resumes on a fresh
/// device. The host pump checks for this capability when it catches a device loss; a presenter that does not implement
/// it cannot recover (the loss propagates). Implementations must tolerate teardown against an already-lost device (e.g.
/// a wait-for-idle that itself fails), and — because the device may be a shared, capability-published singleton — should
/// rebuild it IN PLACE so render nodes that hold the same context reference remain valid (they release and rebuild their
/// own resources via Puck.Hosting's <c>IRenderNode.OnDeviceLost</c>).
/// </summary>
public interface IDeviceLostRecoverable {
    /// <summary>Rebuilds the graphics device and presentation resources after a device loss. Called on the pump thread,
    /// after the offending frame was abandoned and before the render tree is told to release its device resources.</summary>
    /// <param name="binding">The native surface binding to re-bind the recreated swapchain to.</param>
    /// <param name="width">The current surface width, in pixels.</param>
    /// <param name="height">The current surface height, in pixels.</param>
    void RecoverFromDeviceLoss(NativeSurfaceBinding binding, uint width, uint height);
}
