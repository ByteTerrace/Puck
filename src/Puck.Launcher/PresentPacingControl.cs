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
    private uint m_version;
    private bool m_convergesLighting = true;

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
    /// a presented frame, independent of its wall interval, holding a tick until its frame renders. Each frame's
    /// presentation interval is exactly the simulation time it advanced (a step, or nothing) with no interpolation
    /// between ticks, as the offscreen host runs. The World's frame source also presents
    /// the completed tick, advances its animation by the ticks completed and marks its frames
    /// (<c>SdfFrame.TickLocked</c>) so the renderer adapts exposure by them too. Lighting waits are selected separately by
    /// <see cref="ConvergesLighting"/>. With convergence enabled, a capture of the same tick is independent of
    /// the display's rate or the machine's load. A frame that cannot keep up with
    /// the simulation rate slows the simulation rather than skipping ticks; the simulation's own state never depends on
    /// it.</summary>
    public bool TickLocked => HostPacing.StepsOneTickPerFrame;
    /// <summary>Gets whether tick-locked frames synchronously converge indirect lighting. Clock pinning alone can
    /// retain normal budgeted lighting; ordinary display-paced frames never request convergence here.</summary>
    public bool ConvergesLighting => (TickLocked && Volatile.Read(ref m_convergesLighting));
    /// <summary>Selects whether tick-locked frames converge lighting; the default is true for settled stills.</summary>
    /// <param name="enabled">Whether to finish lighting before each tick-locked frame.</param>
    public void SetLightingConvergence(bool enabled) => Volatile.Write(ref m_convergesLighting, enabled);
    /// <summary>The live policy shared by the window loop and authority replay stepping.</summary>
    public Puck.Hosting.HostPacing HostPacing { get; } = new();

    /// <summary>Turns the lockstep presentation of <see cref="TickLocked"/> on or off.</summary>
    /// <param name="locked">Whether presentation runs in lockstep with the simulation.</param>
    public void SetTickLocked(bool locked) => HostPacing.SetTickLocked(locked: locked);
    /// <summary>Returns the interval a frame's presentation spans: under lockstep the simulation time the frame stepped
    /// (a step, or nothing), and the sampled wall interval otherwise or when no simulation steps.</summary>
    /// <param name="tickLocked">Whether the frame presents in lockstep.</param>
    /// <param name="simulates">Whether the host steps a simulation.</param>
    /// <param name="steppedTicks">The engine ticks the frame stepped.</param>
    /// <param name="wallTicks">The sampled wall interval, in engine ticks.</param>
    /// <returns>The frame context's presentation interval.</returns>
    public static ulong FrameDeltaOf(bool tickLocked, bool simulates, ulong steppedTicks, ulong wallTicks) => ((tickLocked && simulates)
        ? steppedTicks
        : wallTicks);
    /// <summary>Returns the rate a frame's presentation time lands on a whole number of steps of: under lockstep the
    /// simulation's tick rate, since a frame spans whole steps, and the display's grid otherwise.</summary>
    /// <param name="tickLocked">Whether the frame presents in lockstep.</param>
    /// <param name="simulates">Whether the host steps a simulation.</param>
    /// <param name="stepTicks">The fixed step, in engine ticks.</param>
    /// <param name="displayGridHertz">The display's grid (<see cref="PresentPacingPolicy.GridHertz"/>), or zero for
    /// none.</param>
    /// <returns>The frame context's grid, in hertz, or zero for none.</returns>
    public static double GridOf(bool tickLocked, bool simulates, ulong stepTicks, double displayGridHertz) => ((tickLocked && simulates && (stepTicks > 0UL))
        ? (((double)Puck.Maths.FixedTickConversion.TicksPerSecond) / stepTicks)
        : displayGridHertz);
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
