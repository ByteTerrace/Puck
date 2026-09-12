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
/// <param name="Fault">The queued worker's fault, if present.</param>
public readonly record struct WorldMachineInstanceState(string Name, ulong Generation, string Engine, bool Running,
    MachineRuntimeStatus Status, long FramesStepped, long PendingSteps, string? Fault);
