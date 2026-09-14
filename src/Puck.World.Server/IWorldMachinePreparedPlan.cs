namespace Puck.World.Server;

/// <summary>
/// The opaque prepare/commit transaction handle crossing the <see cref="IWorldMachineHost"/> seam — this
/// project carries it between <see cref="IWorldMachineHost.TryPrepare"/> and <see cref="IWorldMachineHost.Commit"/>
/// without knowing its concrete shape, so this project never depends on concrete machine implementations.
/// Every non-commit exit must dispose an outstanding plan synchronously.
/// </summary>
public interface IWorldMachinePreparedPlan : IDisposable {
    /// <summary>Gets the machine count this plan installs once committed.</summary>
    int MachineCount { get; }
}
