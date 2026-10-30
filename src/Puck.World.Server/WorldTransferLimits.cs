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
    /// <summary>Bounds the destination's transfer-key ledger together with the leases and retained commits that become
    /// rows of it: settled keys above their source's floor and one floor per source. A row is never evicted to admit
    /// new work, since an evicted row would let a key be leased twice; a source's rising floor prunes its rows.</summary>
    public const int TransferKeys = 65536;
    /// <summary>Bounds one source namespace's own ledger rows, its floor included, so a source that never lets its
    /// floor rise, or asks after keys it never reserved, fills its own share and not every source's.</summary>
    public const int TransferKeysPerSource = 4096;
}
