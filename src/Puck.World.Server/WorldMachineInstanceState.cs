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
/// <param name="StagedFrames">The complete frames the machine has put in its video output, which a display could
/// show.</param>
/// <param name="ShownFrames">How many times a display took a frame other than the one it took before: beside
/// <paramref name="StagedFrames"/> it tells a display that shows every frame from one that repeats or skips.</param>
/// <param name="BackpressureEvents">The submissions that waited for a full pending-segment window since the content was
/// loaded; zero for a synchronous machine.</param>
/// <param name="StepBusyTime">The host time the worker spent running segments since the content was loaded; zero for a
/// synchronous machine.</param>
/// <param name="TimedInputChanges">The controller changes the machine took inside its segments, each at the offset it
/// was captured at rather than at a segment's start; zero for a synchronous machine.</param>
public readonly record struct WorldMachinePace(long NativeFrames, long StagedFrames, long ShownFrames, long BackpressureEvents, TimeSpan StepBusyTime, long TimedInputChanges = 0L);
