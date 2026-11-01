namespace Puck.Hosting;

/// <summary>How a host decides when a fixed step runs. The launcher registers the pacing of the host loop it composes,
/// and whatever steps on the host's behalf reads it (a replay drive fast-forwarding its recorded ticks), so it paces as
/// the host does.</summary>
public sealed class HostPacing {
    private HostPacing? m_override;

    /// <summary>Creates a live wall-clock inline policy. A window shares this instance with its replay stepper.</summary>
    public HostPacing() { }

    private HostPacing(bool stepsOneTickPerFrame = false, bool unpaced = false, bool threaded = false) {
        StepsOneTickPerFrame = stepsOneTickPerFrame;
        IsUnpaced = unpaced;
        IsThreaded = threaded;
    }

    /// <summary>Gets the pacing of a host whose time is the wall clock (the windowed and headless hosts): a host frame runs
    /// every whole step its sampled interval covers, catching up after a slow frame.</summary>
    public static HostPacing WallClockInline { get; } = new();
    /// <summary>Wall-clock stepping on a separate simulation thread, reserved for the later thread phase.</summary>
    public static HostPacing WallClockThreaded { get; } = new(threaded: true);
    /// <summary>Gets the pacing of a host whose time is its tick count (the offscreen host): one tick per produced frame,
    /// so nothing stepping on its behalf runs several ticks inside one step either.</summary>
    public static HostPacing OneTickPerFrame { get; } = new(stepsOneTickPerFrame: true);
    /// <summary>One step per iteration with no wall-clock wait.</summary>
    public static HostPacing Unpaced { get; } = new(stepsOneTickPerFrame: true, unpaced: true);

    /// <summary>Gets whether the host steps one tick per produced frame.</summary>
    public bool StepsOneTickPerFrame { get => (Volatile.Read(location: ref m_override)?.StepsOneTickPerFrame ?? field); private init; }
    /// <summary>Whether the host runs without a pacing wait.</summary>
    public bool IsUnpaced { get; }
    /// <summary>Whether the pacing policy assigns stepping to its own thread.</summary>
    public bool IsThreaded { get; }

    /// <summary>Changes a live window policy at a tick boundary. Fixed registration policies stay immutable.</summary>
    public void SetTickLocked(bool locked) {
        if (ReferenceEquals(objA: this, objB: WallClockInline) || ReferenceEquals(objA: this, objB: WallClockThreaded) || ReferenceEquals(objA: this, objB: OneTickPerFrame) || ReferenceEquals(objA: this, objB: Unpaced)) {
            throw new InvalidOperationException(message: "A fixed pacing policy cannot be retargeted.");
        }
        Volatile.Write(location: ref m_override, value: (locked ? OneTickPerFrame : null));
    }
}
