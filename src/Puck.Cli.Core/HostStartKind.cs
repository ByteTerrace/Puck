namespace Puck.Cli;

/// <summary>What a start is, which sets how much commit it is expected to peak at: the host admission
/// (<c>HostAdmission</c>) admits a start only when the commit headroom covers that peak
/// (<c>HostLoadThresholds.DefaultPeakGb</c>) and what the starts it already admitted still have to grow. The kind comes
/// from the caller for a start it names and from the running process's own arguments for one already running
/// (<c>HostProcesses.RunningStart</c>).</summary>
public enum HostStartKind {
    /// <summary>An ordinary step (a build, a verb that boots no World): its peak is small, and the host does not track it
    /// once it runs.</summary>
    Step,
    /// <summary>A World booted headless (<c>--headless true</c>, or a test batch's single scenarios).</summary>
    HeadlessWorld,
    /// <summary>A World that opens a window and a GPU device.</summary>
    WindowedWorld,
    /// <summary>A heavy test suite (<c>HostProcesses.IsHeavyTestAssembly</c>) or the machine's one heavy lease.</summary>
    HeavySuite,
    /// <summary>A World worker running a test batch, the shape every replica of <c>puck test --reproduce</c> and
    /// <c>--workers</c> takes.</summary>
    ReproduceReplica,
}
