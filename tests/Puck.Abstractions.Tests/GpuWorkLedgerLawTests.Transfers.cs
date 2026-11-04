using System.Buffers;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

public sealed partial class GpuWorkLedgerLawTests {
    [Fact]
    public void TransferStagesSeparateRecordingSubmissionCompletionAndDiscard() {
        var rig = new Rig(1);
        using var host = rig.Services.BufferFactory.CreateHostVisible(name: default, sizeBytes: 64, usage: GpuBufferUsage.Storage);
        host.Write<int>([1, 2]);
        Assert.Equal(default, Transfers(rig).Recorded); // An open record has no published checkpoint yet.
        rig.Ledger.Discard();
        var initial = Transfers(rig);
        Assert.Equal(new GpuTransferCounts(1, 8, 0, 0), initial.Recorded);
        Assert.Equal(initial.Recorded, initial.Discarded);
        Assert.Equal(default, initial.Submitted);

        rig.Ledger.Configure(1, ["copy"]);
        rig.Ledger.EnterPass(0);
        host.Write<int>([3]);
        Copy(rig, 12);
        rig.Ledger.LeavePass();
        rig.Services.Recorder.CopyImage(2, 3, 4, 8, 8);
        var fence = rig.NewFence();
        rig.Submit(fence);
        var submitted = Transfers(rig);
        Assert.Equal(new GpuTransferCounts(1, 4, 2, 12), submitted.Submitted);
        Assert.Equal(default, submitted.Completed);
        Assert.Equal(1, submitted.SubmittedThrough);
        fence.Counted.Wait();
        var complete = Transfers(rig);
        Assert.Equal(submitted.Submitted, complete.Completed);
        Assert.Equal(1, complete.CompletedThrough);
        Assert.Equal(initial.Domain, complete.Domain);
        fence.Counted.Wait();
        Assert.Equal(complete, Transfers(rig));
    }

    [Fact]
    public void ANewerFenceCompletesTheWholeTransferPrefixAfterSampleRingReuse() {
        var rig = new Rig(1);
        var first = rig.NewFence();
        var last = rig.NewFence();
        Copy(rig, 5); rig.Submit(first);
        Copy(rig, 7); rig.Services.QueueSubmitter.Submit([]);
        Copy(rig, 11); rig.Submit(last); // The first sample slot has wrapped.
        Assert.Equal(new GpuTransferCounts(0, 0, 3, 23), Transfers(rig).Submitted);
        last.Raw.Completed = true;
        rig.Ledger.Poll();
        var completed = Transfers(rig);
        Assert.Equal(3, completed.CompletedThrough);
        Assert.Equal(completed.Submitted, completed.Completed);
        first.Counted.Wait();
        Assert.Equal(completed, Transfers(rig));
    }

    [Fact]
    public void InvalidationDropsSamplesButNotAcceptedTransfersOrFenceEvidence() {
        var rig = new Rig(1);
        var fence = rig.NewFence();
        var readback = new OrderedReadback();
        Copy(rig, 17);
        rig.Ledger.ReadOnCompletion(readback, 4);
        rig.Submit(fence);
        Copy(rig, 19); // Recorded but never submitted.
        rig.Ledger.Invalidate();
        Assert.Equal(new GpuTransferCounts(0, 0, 1, 19), Transfers(rig).Discarded);
        fence.Counted.Wait();
        Assert.Null(rig.Read());
        Assert.Empty(readback.Slots);
        Assert.Equal(new GpuTransferCounts(0, 0, 1, 17), Transfers(rig).Completed);
        Copy(rig, 23);
        rig.Services.QueueSubmitter.SubmitAndWait([]);
        Assert.Equal(new GpuTransferCounts(0, 0, 2, 40), Transfers(rig).Completed);
    }

    [Fact]
    public void FenceDisposalDoesNotInventCompletionAndReuseDoesNotResetTotals() {
        var rig = new Rig(1);
        var discardedFence = rig.NewFence();
        Copy(rig, 2); rig.Submit(discardedFence);
        discardedFence.Counted.Dispose();
        Assert.Equal(0, Transfers(rig).CompletedThrough);
        var reused = rig.NewFence();
        Copy(rig, 3); rig.Submit(reused); reused.Counted.Wait();
        var prefix = Transfers(rig);
        rig.Ledger.Configure(2, ["replacement"]);
        rig.Ledger.Invalidate();
        Copy(rig, 5); rig.Submit(reused); reused.Counted.Wait();
        var total = Transfers(rig);
        Assert.Equal(prefix.Domain, total.Domain);
        Assert.Equal(new GpuTransferCounts(0, 0, 3, 10), total.Completed);
        Assert.Equal(3, total.CompletedThrough);
    }

    [Fact]
    public void OneLedgerCannotInferCompletionAcrossDifferentQueues() {
        var rig = new Rig(1);
        var other = new Puck.Testing.FakeGpuDevice();
        Assert.Throws<ArgumentException>(() => GpuWorkCounting.Wrap(other.Services.QueueSubmitter, rig.Ledger));
        // A second facade over the same queue is harmless; distinct ledgers are independent domains.
        _ = GpuWorkCounting.Wrap(rig.Gpu.Services.QueueSubmitter, rig.Ledger);
        var second = new Rig(1);
        Copy(rig, 3); rig.Services.QueueSubmitter.Submit([]);
        Copy(second, 7); second.Services.QueueSubmitter.SubmitAndWait([]);
        Assert.NotEqual(Transfers(rig).Domain, Transfers(second).Domain);
        Assert.Equal(0, Transfers(rig).CompletedThrough);
        Assert.Equal(7, Transfers(second).Completed.BufferCopyBytes);
    }

    [Fact]
    public void TransferReadsAndNamedDetailsDoNotDuplicateOrAllocate() {
        var rig = new Rig(1);
        rig.Ledger.Configure(1, ["pass"]);
        rig.Ledger.ConfigureDetails([new(0, "plain"), new(0, "named")]);
        rig.Ledger.EnterPass(0); Copy(rig, 13); rig.Ledger.LeavePass();
        rig.Services.QueueSubmitter.SubmitAndWait([]);
        Assert.Equal(new GpuTransferCounts(0, 0, 1, 13), Transfers(rig).Completed);
        Assert.Equal(0, Puck.Abstractions.Counting.AllocationWindow.Measure(() => { _ = rig.Ledger.TryReadTransfers(out _); }));
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) {
            GpuWorkReport.WriteTransfers(writer, new("same", rig.Ledger, rig.Ledger));
        }
        using var json = JsonDocument.Parse(buffer.WrittenMemory);
        Assert.Equal(13, json.RootElement.GetProperty("transfers").GetProperty("completed").GetProperty("buffer-copy-bytes").GetInt64());
        Assert.Contains("completed-through=1", GpuWorkReport.AppendTransfers(new StringBuilder(), new("same", rig.Ledger, rig.Ledger)).ToString());
    }

    [Fact]
    public void ATransferReadoutReadsSourcesOnceAndSharesOneObservationAcrossAliases() {
        var first = new AdvancingTransferSource(9001);
        var alias = new AdvancingTransferSource(9001);
        GpuWorkNode[] nodes = [new("a", first, null), new("b", first, null), new("c", alias, null)];
        var snapshot = GpuWorkReport.ReadTransfers(nodes);
        Assert.Equal(1, first.Reads);
        Assert.Equal(1, alias.Reads);
        Assert.Equal(snapshot[first], snapshot[alias]);
        using var writer = new Utf8JsonWriter(new ArrayBufferWriter<byte>());
        GpuWorkReport.WriteTransfers(writer, "a", snapshot[first]);
        Assert.Equal(1, first.Reads);
        _ = GpuWorkReport.ReadTransfers(nodes);
        Assert.Equal(2, first.Reads);
        Assert.Equal(2, alias.Reads);
    }

    private sealed class AdvancingTransferSource(long domain) : IGpuWorkSource {
        public int Reads { get; private set; }
        public bool TryReadCompleted(GpuWorkSample sample) => false;
        public bool TryReadTransfers(out GpuTransferTotals totals) {
            var count = ++Reads;
            var transfers = new GpuTransferCounts(0, 0, count, count * 17);
            totals = new(domain, count, count, transfers, transfers, transfers, default);
            return true;
        }
    }

    private static GpuTransferTotals Transfers(Rig rig) {
        Assert.True(rig.Ledger.TryReadTransfers(out var totals));
        return totals;
    }
    private static void Copy(Rig rig, ulong bytes) => rig.Services.Recorder.CopyBuffer(2, 3, 4, bytes);
}
