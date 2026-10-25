namespace Puck.World.Server;

/// <summary>Admission ceilings for retained transfer work, independent of authored body capacity.</summary>
public static class WorldTransferLimits {
    /// <summary>Two minutes permits slow handoffs while preventing a source from reserving bodies indefinitely.</summary>
    public const int LeaseSeconds = 120;
    /// <summary>Bounds leases, exact commit retry payloads, and uncertain commits together. Live history is never
    /// evicted to admit new work: acknowledgements and retirement of travelers recover capacity safely.</summary>
    public const int Transactions = 16384;
    /// <summary>Bounds authenticated source aliases and pending arrivals. Retiring travelers releases their replay
    /// protection; dropping live history merely to admit another traveler would allow replayed ownership.</summary>
    public const int MobilityAdmissions = 65536;
}
