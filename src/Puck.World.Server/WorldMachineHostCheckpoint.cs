namespace Puck.World.Server;

/// <summary>One named machine's opaque runtime image and world-owned generation bookkeeping.</summary>
/// <param name="Name">The declared machine name.</param>
/// <param name="Engine">The provider identity.</param>
/// <param name="Generation">The instance generation used to reject stale operations.</param>
/// <param name="CompletedSteps">The host's synchronous step count.</param>
/// <param name="RuntimeState">The complete provider-owned checkpoint encoding.</param>
public sealed record WorldMachineCheckpoint(string Name, string Engine, ulong Generation, long CompletedSteps, byte[] RuntimeState);

/// <summary>Durable state owned by a world's machine host, captured at an authoritative world boundary.</summary>
/// <param name="Revision">The machine inventory revision.</param>
/// <param name="NextGeneration">The next generation available to a replacement machine.</param>
/// <param name="AnyEverPumped">Whether this host has advanced any machine.</param>
/// <param name="Instances">The complete named runtime inventory.</param>
public sealed record WorldMachineHostCheckpoint(ulong Revision, ulong NextGeneration, bool AnyEverPumped,
    IReadOnlyList<WorldMachineCheckpoint> Instances) {
    /// <summary>An empty machine inventory for compositions without a machine host.</summary>
    public static WorldMachineHostCheckpoint Empty { get; } = new(0, 1, false, []);
}

/// <summary>Optional complete-state persistence for a world machine host. Unsupported live state refuses capture;
/// a failed restore requires disposal of the private, newly created host.</summary>
public interface IWorldMachineCheckpointHost {
    /// <summary>Captures each runtime after all its accepted steps have completed.</summary>
    /// <returns>The complete machine inventory and its runtime images.</returns>
    WorldMachineHostCheckpoint CaptureCheckpoint();
    /// <summary>Restores exactly the declared machine inventory before its first simulation step.</summary>
    /// <param name="checkpoint">The captured host and runtime state.</param>
    void RestoreCheckpoint(WorldMachineHostCheckpoint checkpoint);
}
