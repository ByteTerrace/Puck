namespace Puck.World.Silo;

/// <summary>One grain's own read-back of its row — the payload <see cref="IWorldGrain.StatusAsync"/> answers with.</summary>
[GenerateSerializer]
public sealed class WorldGrainStatus {
    /// <summary>Gets the row's key, <c>owner/{oid}/{world}</c>.</summary>
    [Id(0)]
    public required string Key { get; init; }
    /// <summary>Gets the world id.</summary>
    [Id(1)]
    public required string World { get; init; }
    /// <summary>Gets the authored simulation rate, in Hz.</summary>
    [Id(2)]
    public int RateHz { get; init; }
    /// <summary>Gets the row's own completed-tick count.</summary>
    [Id(3)]
    public ulong Tick { get; init; }
    /// <summary>Gets the row's own exact engine-tick elapsed clock.</summary>
    [Id(4)]
    public ulong ElapsedEngineTicks { get; init; }
    /// <summary>Gets the row's banked schedule accumulator, in engine ticks.</summary>
    [Id(5)]
    public ulong ScheduleAccumulatorTicks { get; init; }
    /// <summary>Gets the master lag this row has fallen behind, in engine ticks.</summary>
    [Id(6)]
    public ulong BehindTicks { get; init; }
    /// <summary>Gets a value indicating whether this row is held pending its adjacency mirrors.</summary>
    [Id(7)]
    public bool AwaitingMirrors { get; init; }
    /// <summary>Gets a value indicating whether this row's live pause lever is set.</summary>
    [Id(8)]
    public bool Paused { get; init; }
    /// <summary>Gets the row's own door endpoint, or empty when it has none or has not started.</summary>
    [Id(9)]
    public required string DoorEndpoint { get; init; }
    /// <summary>Gets the row's authenticated federation subject.</summary>
    [Id(10)]
    public required string FederationSubject { get; init; }
    /// <summary>Gets the last checkpoint's ordinal, or -1 for a row never checkpointed.</summary>
    [Id(11)]
    public long LastCheckpointOrdinal { get; init; }
    /// <summary>Gets the last checkpoint's tick, valid only when <see cref="LastCheckpointOrdinal"/> is not -1.</summary>
    [Id(12)]
    public ulong LastCheckpointTick { get; init; }
    /// <summary>Gets the last checkpoint attempt's outcome, human-readable.</summary>
    [Id(13)]
    public required string LastCheckpointOutcome { get; init; }
    /// <summary>Gets how many capture requests this row has deferred because its pending slice was non-empty at the
    /// boundary.</summary>
    [Id(14)]
    public int CheckpointDeferredCount { get; init; }
    /// <summary>Gets the number of durable-journal appends this row has scheduled but not yet had acknowledged by
    /// the store — the append lag: journaling is asynchronous with a bounded lag, never a block on the tick
    /// thread, so a nonzero count here is a fact about outstanding store I/O, not a failure.</summary>
    [Id(15)]
    public int PendingJournalAppends { get; init; }
    /// <summary>Gets the most recently acknowledged durable-journal append's outcome, human-readable.</summary>
    [Id(16)]
    public required string LastJournalOutcome { get; init; }
}
