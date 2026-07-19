namespace Puck.World;

/// <summary>
/// The render-capacity oracle the server consults before it applies a scene/screen mutation — the seam that keeps the
/// "capacity honesty" contract: the render envelope (program words, instances) was probed once at construction for the
/// boot definition, so an applied mutation that would exceed it is REJECTED at apply time with a loud line naming the
/// ceiling (never a crash, never a silent clamp). A shared DI singleton: <see cref="Puck.World.Client.WorldFrameSource"/> configures it
/// with the probed floors and a measurer once the render node is built (before any stdin mutation lands), and
/// <see cref="Puck.World.Server.WorldServer"/> reads it while draining its mutation queue.
/// </summary>
/// <remarks>Single-threaded: configured on the window-pump thread during startup, read on the same thread while the
/// server drains its pre-tick queue, so no lock guards it. Unconfigured (a mutation somehow racing startup) reads as
/// "fits" — the boot definition is what the envelope was probed for, so it always fits.</remarks>
internal sealed class WorldRenderEnvelope {
    private int m_programWordCapacity;
    private int m_instanceCapacity;
    private Func<WorldDefinition, (int Words, int Instances)>? m_measure;

    /// <summary>Records the probed envelope floors and the worst-case program measurer.</summary>
    /// <param name="programWordCapacity">The probed program-word ceiling (all avatars + the boot scene/screens/placements).</param>
    /// <param name="instanceCapacity">The probed instance ceiling.</param>
    /// <param name="measure">Measures a candidate definition's render-relevant sections (scene, screens, placements +
    /// their creations) against the same worst-case avatar build, returning its program-word and instance counts.</param>
    /// <exception cref="ArgumentNullException"><paramref name="measure"/> is <see langword="null"/>.</exception>
    public void Configure(int programWordCapacity, int instanceCapacity, Func<WorldDefinition, (int Words, int Instances)> measure) {
        ArgumentNullException.ThrowIfNull(argument: measure);

        m_programWordCapacity = programWordCapacity;
        m_instanceCapacity = instanceCapacity;
        m_measure = measure;
    }

    /// <summary>Tests whether a candidate definition's render-relevant sections fit the probed render envelope.</summary>
    /// <param name="candidate">The composed candidate definition.</param>
    /// <param name="reason">On a miss, the loud ceiling reason; empty otherwise.</param>
    /// <returns><see langword="true"/> when the candidate fits (or the envelope is not yet configured).</returns>
    public bool TryFit(WorldDefinition candidate, out string reason) {
        if (m_measure is not { } measure) {
            reason = string.Empty;

            return true;
        }

        var (words, instances) = measure(arg: candidate);

        if (words > m_programWordCapacity) {
            reason = $"program words {words} exceed the probed render envelope {m_programWordCapacity}";

            return false;
        }

        if (instances > m_instanceCapacity) {
            reason = $"instances {instances} exceed the probed render envelope {m_instanceCapacity}";

            return false;
        }

        reason = string.Empty;

        return true;
    }
}
