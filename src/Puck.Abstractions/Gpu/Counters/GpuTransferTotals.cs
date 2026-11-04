namespace Puck.Abstractions.Gpu;

/// <summary>Host-observed transfer work. Host writes are CPU writes into mapped storage, not GPU traffic.
/// Buffer copies include upload, readback and device-local carry copies once each; image copies add an operation
/// but have no inferred byte size. These are operation payloads, never resource allocations or physical bus bytes.</summary>
/// <param name="HostWriteOperations">Successful host-visible write calls, including initial-data creation.</param>
/// <param name="HostWriteBytes">The payload bytes of those CPU writes.</param>
/// <param name="CopyOperations">Successful buffer and image copy calls recorded through the neutral recorder.</param>
/// <param name="BufferCopyBytes">The payload bytes of buffer-range copies; excludes image-copy bytes.</param>
public readonly record struct GpuTransferCounts(long HostWriteOperations, long HostWriteBytes, long CopyOperations, long BufferCopyBytes) {
    /// <summary>Adds disjoint transfer work, refusing numeric overflow.</summary>
    /// <param name="left">The first counts.</param><param name="right">The second counts.</param>
    /// <returns>The combined counts.</returns>
    public static GpuTransferCounts operator +(GpuTransferCounts left, GpuTransferCounts right) => new(
        checked(left.HostWriteOperations + right.HostWriteOperations), checked(left.HostWriteBytes + right.HostWriteBytes),
        checked(left.CopyOperations + right.CopyOperations), checked(left.BufferCopyBytes + right.BufferCopyBytes));
}

/// <summary>A coherent lifetime observation from one ledger and ordered queue. Counts publish at submission/discard checkpoints and never reset on graph
/// reconfiguration, invalidation or sample-ring reuse. The currently open record is excluded until that checkpoint.
/// Recorded includes finalized work not accepted by a queue; discarded
/// is the subset explicitly discarded before submission. Submitted associates host writes and recorded copies with
/// accepted submissions. Completed is a prefix proven by a fence or SubmitAndWait on this same queue, never another
/// node's completion. Host writes already happened on the CPU; their completed association is not GPU execution.
/// Node disappearance is not a zero: a window must retain the same domains or report incomplete coverage.</summary>
/// <param name="Domain">The process-local ledger identity; aliases share it, replacement ledgers do not.</param>
/// <param name="SubmittedThrough">The last accepted logical submission of this ledger.</param>
/// <param name="CompletedThrough">The last accepted submission whose completion is known.</param>
/// <param name="Recorded">Successful transfer calls in records finalized by submission or discard; excludes the currently open record.</param>
/// <param name="Submitted">Transfer work associated with accepted submissions.</param>
/// <param name="Completed">Transfer work in the known-completed ordered prefix.</param>
/// <param name="Discarded">Recorded work explicitly discarded before submission.</param>
public readonly record struct GpuTransferTotals(long Domain, long SubmittedThrough, long CompletedThrough,
    GpuTransferCounts Recorded, GpuTransferCounts Submitted, GpuTransferCounts Completed, GpuTransferCounts Discarded);
