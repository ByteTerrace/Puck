using Puck.Hosting;

namespace Puck.World;

/// <summary>The desktop capture answer shared by its private slot adapter and its laws. CPU captures answer from their
/// conversion; GPU captures answer from the platform's completed copy. Reading an answer produces no work.</summary>
public static class WorldCaptureFrame {
    /// <summary>Answers the current capture image, with an ended source taking precedence over an earlier frame.</summary>
    /// <param name="pixels">The CPU route's captured pixels.</param>
    /// <param name="gpuRoute">Whether the sampled image is a platform GPU copy.</param>
    /// <param name="gpuHandle">The currently attached ring's latest published image, or zero before its first copy.</param>
    /// <param name="ended">Whether the capture source is unavailable.</param>
    /// <param name="fault">The source's reason for waiting or refusing, or <see langword="null"/>.</param>
    /// <returns>The sampled image's answer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pixels"/> is <see langword="null"/>.</exception>
    public static FrameRender Answer(WorldCapturePixels pixels, bool gpuRoute, nint gpuHandle, bool ended, string? fault) {
        ArgumentNullException.ThrowIfNull(argument: pixels);

        if (ended) {
            return FrameRender.Refused(reason: (fault ?? "capture is unavailable"));
        }

        return (gpuRoute
            ? ((gpuHandle != 0)
                ? FrameRender.Rendered
                : FrameRender.Waiting(reason: (fault ?? "capture awaiting a compositor frame")))
            : pixels.Answer());
    }
}
