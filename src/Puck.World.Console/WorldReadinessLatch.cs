namespace Puck.World;

/// <summary>
/// Turns the engine's readiness conditions into its readiness: ready once the conditions hold and the host has produced
/// a frame since a read first found them held. The frame that completes the conditions is the engine's slowest (its
/// first render, its first readback), and the windowed host, paced to its display, catches up the ticks it cost in one
/// iteration, so without the frame after it a <c>world.wait</c> of a few ticks issued at readiness could span a single
/// frame. The offscreen host steps one tick per produced frame and owes no catch-up, so there the frame after costs one
/// tick. A read that finds a condition broken starts over. Members are read on the host pump.
/// </summary>
public sealed class WorldReadinessLatch {
    // The produced-frame count when a read first found every condition held, or -1 while one does not.
    private long m_heldSinceFrame = -1;

    /// <summary>Observes the conditions and returns whether the engine is ready. Allocates nothing.</summary>
    /// <param name="conditionsHold">Whether every readiness condition holds now.</param>
    /// <param name="framesProduced">The count of frames the host has produced, which only rises.</param>
    /// <returns><see langword="true"/> when the conditions hold and a frame has been produced since a read first found
    /// them held.</returns>
    public bool Observe(bool conditionsHold, long framesProduced) {
        if (!conditionsHold) {
            m_heldSinceFrame = -1;

            return false;
        }
        if (m_heldSinceFrame < 0) {
            m_heldSinceFrame = framesProduced;
        }

        return (framesProduced > m_heldSinceFrame);
    }
}
