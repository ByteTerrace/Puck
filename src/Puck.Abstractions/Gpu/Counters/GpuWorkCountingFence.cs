namespace Puck.Abstractions.Gpu;

/// <summary>
/// The submission fence a counting queue submitter hands out. It remembers the submission it was armed with, so a
/// <see cref="Wait"/> completes exactly that submission in the ledger, and the counting submitter passes
/// <see cref="Inner"/> to the backend, which only accepts its own fence type.
/// </summary>
internal sealed class GpuWorkCountingFence(IGpuSubmissionFence inner, GpuWorkLedger ledger) : IGpuSubmissionFence {
    /// <summary>Gets the submission this fence is armed with, or zero when none is outstanding.</summary>
    internal long ArmedSubmission { get; set; }
    internal GpuTransferCounts ArmedTransfers { get; set; }
    /// <summary>Gets the backend's fence.</summary>
    internal IGpuSubmissionFence Inner =>
        inner;
    /// <summary>Gets the ledger whose submissions this fence completes.</summary>
    internal GpuWorkLedger Ledger =>
        ledger;

    /// <inheritdoc/>
    public bool IsSignaled =>
        inner.IsSignaled;

    /// <inheritdoc/>
    public void SignalOnCompletion(EventWaitHandle signal) =>
        inner.SignalOnCompletion(signal: signal);
    /// <inheritdoc/>
    public void Dispose() {
        inner.Dispose();
        ArmedSubmission = 0L;
        ledger.Detach(fence: this);
    }
    /// <inheritdoc/>
    /// <remarks>An abandoned submission (<see cref="GpuSubmissionAbandonedException"/>) never ran, so its record is dropped
    /// rather than published, and the drained fence completes nothing at its next wait.</remarks>
    public void Wait() {
        var submission = ArmedSubmission;
        var transfers = ArmedTransfers;

        try {
            inner.Wait();
        } catch (GpuSubmissionAbandonedException) {
            ArmedSubmission = 0L;
            ArmedTransfers = default;
            ledger.Abandon(submission: submission);

            throw;
        }

        ArmedSubmission = 0L;
        ArmedTransfers = default;

        if (submission != 0L) {
            ledger.Complete(submission: submission, transferPrefix: transfers);
        }
    }
}
