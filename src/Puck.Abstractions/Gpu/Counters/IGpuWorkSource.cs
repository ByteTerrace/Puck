namespace Puck.Abstractions.Gpu;

/// <summary>
/// A render node's completed GPU work, per pass, for the newest of its submissions known to have finished on the
/// GPU. A reader supplies its own <see cref="GpuWorkSample"/> and reuses it across reads, so reading allocates
/// nothing once the sample has grown to the node's pass count.
/// </summary>
public interface IGpuWorkSource {
    /// <summary>Copies the newest completed submission's counts into <paramref name="sample"/>.</summary>
    /// <param name="sample">The caller's sample, overwritten by every call; it holds no submission when the method
    /// returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when a completed submission is available; <see langword="false"/> before the
    /// first completion and after a reset, resize, or reload until a newer submission completes. Repeated calls with
    /// no newer completion return the same <see cref="GpuWorkSample.Submission"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sample"/> is <see langword="null"/>.</exception>
    bool TryReadCompleted(GpuWorkSample sample);
}
