namespace Puck.Abstractions;

/// <summary>
/// Observes each captured frame as the sink consumes it — for diagnostics that derive a signal from the
/// pixels rather than store them, such as per-frame content hashing for deterministic-frame regression checks.
/// Observers run in registration order and see the CPU-pixel <see cref="Surface"/> variant.
/// </summary>
public interface ICaptureFrameObserver {
    /// <summary>Called for each captured frame, before or as it is encoded.</summary>
    /// <param name="frame">The captured frame; its <see cref="Surface"/> carries tightly packed CPU pixels.</param>
    void OnFrameCaptured(in CaptureFrame frame);
}
