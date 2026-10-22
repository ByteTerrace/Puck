namespace Puck.Launcher;

/// <summary>
/// The LIVE present-rate control the window pump's display-aware pacer reads — a small mutable holder so a console verb can
/// retarget the present cadence mid-session without a restart. This is PRESENTATION pacing ONLY: the fixed-step
/// simulation runs at its own rate and is never touched, so retargeting the present rate never affects determinism.
/// The stored value is a target present rate in Hz, with 0 meaning automatic display pacing: a verified VRR range is
/// used when available, otherwise the active signal rate is used. A monotonically increasing <see cref="Version"/> lets the pump re-resolve its render
/// period only when the target actually changed — exactly as it re-resolves on a display-configuration change — rather
/// than recomputing every frame. Seeded from <see cref="LauncherOptions.TargetRenderRate"/> at composition; the demo's
/// enumerated present-rate tier feeds that seed, and the <c>present-rate</c> verb writes here.
/// </summary>
public sealed class PresentPacingControl {
    private double m_targetHertz;
    private bool m_tickLocked;
    private uint m_version;

    /// <summary>Initializes the control with the boot target rate.</summary>
    /// <param name="initialTargetHertz">The seed target rate in Hz, or <see langword="null"/> for automatic display pacing.</param>
    public PresentPacingControl(double? initialTargetHertz) {
        var targetHertz = (initialTargetHertz ?? 0.0);

        ArgumentOutOfRangeException.ThrowIfNegative(targetHertz);

        if (!double.IsFinite(d: targetHertz)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(initialTargetHertz),
                actualValue: initialTargetHertz,
                message: "The target present rate must be finite."
            );
        }

        m_targetHertz = targetHertz;
    }

    /// <summary>The current target present rate in Hz; 0 means automatic display pacing.</summary>
    public double TargetHertz => Volatile.Read(location: ref m_targetHertz);
    /// <summary>A monotonically increasing counter bumped whenever <see cref="SetTargetHertz"/> changes the target, so the
    /// pump re-resolves its render period only on an actual change.</summary>
    public uint Version => Volatile.Read(location: ref m_version);

    /// <summary>Gets whether the window pump runs presentation in lockstep with the simulation: at most one fixed step
    /// a presented frame, and each frame's presentation interval exactly the simulation time it advanced (a step, or
    /// nothing) with no interpolation between ticks, as the offscreen host runs. Presentation animation (gait, effectors,
    /// exposure adaptation) then integrates the same intervals at the same ticks on every run, so a capture of a tick
    /// shows the same image whatever the display's rate or the machine's load. A frame that cannot keep up with the
    /// simulation rate slows the simulation rather than skipping ticks; the simulation's own state never depends on
    /// it.</summary>
    public bool TickLocked => Volatile.Read(location: ref m_tickLocked);

    /// <summary>Turns the lockstep presentation of <see cref="TickLocked"/> on or off.</summary>
    /// <param name="locked">Whether presentation runs in lockstep with the simulation.</param>
    public void SetTickLocked(bool locked) => Volatile.Write(location: ref m_tickLocked, value: locked);

    /// <summary>Retargets the present rate. A no-op (no version bump) when the value is unchanged.</summary>
    /// <param name="targetHertz">The new target in Hz, or 0 for automatic display pacing.</param>
    public void SetTargetHertz(double targetHertz) {
        ArgumentOutOfRangeException.ThrowIfNegative(targetHertz);

        if (!double.IsFinite(d: targetHertz)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(targetHertz),
                actualValue: targetHertz,
                message: "The target present rate must be finite."
            );
        }

        if (Volatile.Read(location: ref m_targetHertz) == targetHertz) {
            return;
        }

        Volatile.Write(
            location: ref m_targetHertz,
            value: targetHertz
        );
        Volatile.Write(
            location: ref m_version,
            value: (Volatile.Read(location: ref m_version) + 1U)
        );
    }
}
