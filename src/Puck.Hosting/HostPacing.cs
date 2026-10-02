namespace Puck.Hosting;

/// <summary>How a host decides when a fixed step runs. The launcher registers the pacing of the host loop it composes,
/// and whatever steps on the host's behalf reads it (a replay drive fast-forwarding its recorded ticks), so it paces as
/// the host does.</summary>
public sealed class HostPacing {
    private HostPacing(bool stepsOneTickPerFrame) => StepsOneTickPerFrame = stepsOneTickPerFrame;

    /// <summary>Gets the pacing of a host whose time is the wall clock (the windowed and headless hosts): a host frame runs
    /// every whole step its sampled interval covers, catching up after a slow frame.</summary>
    public static HostPacing WallClock { get; } = new(stepsOneTickPerFrame: false);
    /// <summary>Gets the pacing of a host whose time is its tick count (the offscreen host): one tick per produced frame,
    /// so nothing stepping on its behalf runs several ticks inside one step either.</summary>
    public static HostPacing OneTickPerFrame { get; } = new(stepsOneTickPerFrame: true);

    /// <summary>Gets whether the host steps one tick per produced frame.</summary>
    public bool StepsOneTickPerFrame { get; }
}
