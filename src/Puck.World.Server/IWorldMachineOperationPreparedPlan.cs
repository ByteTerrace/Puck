namespace Puck.World.Server;

/// <summary>Opaque host transaction for one named machine operation.</summary>
public interface IWorldMachineOperationPreparedPlan : IDisposable {
    /// <summary>The declaration the operation was prepared against.</summary>
    WorldMachine Current { get; }
    /// <summary>The canonical declaration to publish if the operation applies.</summary>
    WorldMachine Candidate { get; }
    /// <summary>The expected live generation.</summary>
    ulong Generation { get; }
}
