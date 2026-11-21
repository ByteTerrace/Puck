using Puck.Abstractions.Machines;

namespace Puck.World.Server;

/// <summary>A named instance's execution state, independent of any display signal.</summary>
/// <param name="Name">The authored instance name.</param>
/// <param name="Generation">The incarnation used to reject stale operations after replacement.</param>
/// <param name="Engine">The registered provider.</param>
/// <param name="Running">Whether authoritative ticks are enabled.</param>
/// <param name="Status">The provider's current hardware status.</param>
/// <param name="FramesStepped">Completed advance segments in this incarnation.</param>
/// <param name="PendingSteps">Queued advance segments still outstanding.</param>
/// <param name="Fault">The preparation refusal or queued worker's fault, if present.</param>
/// <param name="Content">The content path the incarnation mounted, as its configuration names it, or
/// <see langword="null"/> when it runs with no cartridge.</param>
public readonly record struct WorldMachineInstanceState(string Name, ulong Generation, string Engine, bool Running,
    MachineRuntimeStatus Status, long FramesStepped, long PendingSteps, string? Fault, string? Content = null);
/// <summary>A named instance's host-side pace, which no checkpoint or replay carries: what a machine's worker did on this
/// host, read to judge whether and by how much it keeps up with real time.</summary>
/// <param name="NativeFrames">The machine's last completed native video frame, or -1 when it reports none.</param>
/// <param name="BackpressureEvents">The submissions that waited for a full pending-segment window since the content was
/// loaded; zero for a synchronous machine.</param>
/// <param name="StepBusyTime">The host time the worker spent running segments since the content was loaded; zero for a
/// synchronous machine.</param>
public readonly record struct WorldMachinePace(long NativeFrames, long BackpressureEvents, TimeSpan StepBusyTime);
