namespace Puck.Abstractions.Gpu;

public sealed partial class GpuWorkLedger {
    private static long NextTransferDomain;
    private readonly long m_transferDomain = Interlocked.Increment(ref NextTransferDomain);
    private readonly Lock m_transferGate = new();
    private IGpuQueueSubmitter? m_transferQueue;
    private GpuTransferCounts m_transfersRecorded;
    private GpuTransferCounts m_transfersSubmitted;
    private GpuTransferCounts m_transfersCompleted;
    private GpuTransferCounts m_transfersDiscarded;
    private long m_transferSubmittedThrough;
    private long m_transferCompletedThrough;

    /// <inheritdoc/>
    public bool TryReadTransfers(out GpuTransferTotals totals) {
        lock (m_transferGate) {
            totals = new(m_transferDomain, m_transferSubmittedThrough, m_transferCompletedThrough,
                m_transfersRecorded, m_transfersSubmitted, m_transfersCompleted, m_transfersDiscarded);
        }
        return true;
    }

    // Prefix inference is valid only on one ordered queue. All counting submitter creation passes this door.
    internal void BindTransferQueue(IGpuQueueSubmitter queue) {
        lock (m_transferGate) {
            if (m_transferQueue is not null && !ReferenceEquals(m_transferQueue, queue)) {
                throw new ArgumentException(message: "A GPU work ledger counts one ordered queue; use a separate ledger for another submitter.", paramName: nameof(queue));
            }
            m_transferQueue = queue;
        }
    }

    private void CountTransfer(Record record, int column, long amount) {
        var delta = column switch {
            GpuWork.HostVisibleUploadBytesColumn => new GpuTransferCounts(1, amount, 0, 0),
            GpuWork.CopiesColumn => new GpuTransferCounts(0, 0, amount, 0),
            GpuWork.BufferCopyBytesColumn => new GpuTransferCounts(0, 0, 0, amount),
            _ => default,
        };
        if (delta == default) { return; }
        record.Transfers += delta;
    }
    private void DiscardTransfers(Record? record) {
        if (record is null) { return; }
        lock (m_transferGate) {
            m_transfersRecorded += record.Transfers;
            m_transfersDiscarded += record.Transfers;
        }
    }
    private void SealTransfers(Record record) {
        lock (m_transferGate) {
            m_transfersRecorded += record.Transfers;
            m_transfersSubmitted += record.Transfers;
            m_transferSubmittedThrough = record.Submission;
            record.TransferPrefix = m_transfersSubmitted;
        }
        if (record.Fence is { } fence) {
            fence.ArmedTransfers = record.TransferPrefix;
            fence.ArmedSubmission = record.Submission;
        }
    }
    private void CompleteTransfers(long submission, GpuTransferCounts prefix) {
        lock (m_transferGate) {
            if (submission <= m_transferCompletedThrough) { return; }
            m_transferCompletedThrough = submission;
            m_transfersCompleted = prefix;
        }
    }
}
