using System.Buffers;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;
using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    /// <summary>The host bytes a bake's dispatched answers hold at once at most: the copies of its batches' samples and
    /// the sample sets it loads in place of a dispatch, from the take that admits them until their last answer ends.
    /// <see cref="SdfGpuWork.SlotCount"/> batches of <see cref="SdfWorldPackage.MeshBakeMaxSamples"/> samples carrying two
    /// words each (a final lattice's slabs: a value and a material a sample), so the largest slabs keep every slot busy;
    /// a batch's lattices hold four words a sample at most, so one batch of the most samples is half of it, and with no
    /// answer outstanding any lattice fits.</summary>
    public const long ReadbackBytes = ((((long)SdfGpuWork.SlotCount) * SdfWorldPackage.MeshBakeMaxSamples) * 2L * sizeof(uint));

    // Invariant: every host byte the bake holds for an answer it has not finished with (a batch's copy of its samples, a
    // sample set read in place of a dispatch) is charged to m_readback within its reservation, so the charge never asks
    // the account for room and is never refused. The reservation is promised as it is used: it covers the output bytes
    // granted and not yet freed (m_readbackPending) and what the take in progress has promised and not yet granted
    // (m_readbackTaking), at the charge their storage makes (ChargeOf), and nothing more. The one door that raises it is
    // the take (PromiseReadback), which takes a lattice only once its answer is promised and only while the granted bytes
    // fit ReadbackBytes; the bytes leave m_readbackPending only when their storage is freed (AnsweredSamples.Release, or a
    // batch that never copied), and every change of either sum settles the reservation to them under m_readbackGate. A
    // promise is a progress floor (Owner.TryReserveProgress): the groups the account admitted can be answered past the
    // room they hold themselves, within the host bar.
    private readonly MemoryAccount.Owner m_readback = MemoryAccount.Shared.CreateOwner(allowance: MemoryAllowance.Authoring, memoryClass: MemoryClass.Workspace, name: "mesh-bake-readback");
    // Under m_readbackGate: the output bytes of the batches and probes taken whose storage is not yet freed; the bytes the
    // take in progress promised and has not granted yet; the reservation m_readback holds; and whether a refused promise
    // has been reported since one was last made.
    private long m_readbackPending;
    private long m_readbackTaking;
    private long m_readbackPromised;
    private bool m_readbackReported;
    private readonly Lock m_readbackGate = new();

    // Promises the readback a take's lattices answer with: the bytes granted, and `taking`, everything the take has taken
    // or is about to take that is not granted yet. False when the account has no room for the rise, and the take then takes
    // no lattice past those already promised. Under the gate, on the take.
    private bool PromiseReadback(long taking) {
        lock (m_readbackGate) {
            var promise = ChargeOf(bytes: (m_readbackPending + taking));

            if ((promise > m_readbackPromised) && !m_readback.TryReserveProgress(peak: promise)) {
                if (!m_readbackReported) { SdfMeshBakeReport.ReadbackDeferred(residency: Residency, bytes: promise); m_readbackReported = true; }
                m_accounts.ReadbackWaits++;
                return false;
            }
            m_readbackReported = false;
            m_readbackPromised = Math.Max(val1: m_readbackPromised, val2: promise);
            m_readbackTaking = taking;
            return true;
        }
    }
    // Ends a take: what it promised and did not grant is promised no longer. Under the gate, on the take, after its grants.
    private void EndReadbackTake() {
        lock (m_readbackGate) {
            m_readbackTaking = 0L;
            SettleReadback();
        }
    }
    // Under m_readbackGate: lowers the reservation to the bytes granted and being taken.
    private void SettleReadback() {
        var promise = ChargeOf(bytes: (m_readbackPending + m_readbackTaking));

        if (promise >= m_readbackPromised) { return; }
        _ = m_readback.TryReserve(peak: promise);
        m_readbackPromised = promise;
    }
    // The bytes an answer's storage charges for `bytes` of output: the same bytes from a native allocator, and at most
    // twice them from the rented arrays a bake with none uses, which round a length up to their bucket.
    private long ChargeOf(long bytes) => ((Library.Allocator is null) ? (2L * bytes) : bytes);
    // The readback bytes the take may still grant: what is left of ReadbackBytes.
    private long ReadbackRoom {
        get {
            lock (m_readbackGate) { return (ReadbackBytes - m_readbackPending); }
        }
    }

    // Grants a batch or probe its output bytes, which the take promised (PromiseReadback).
    private void GrantReadback(long bytes) {
        lock (m_readbackGate) {
            m_readbackPending += bytes;
            m_readbackTaking = Math.Max(val1: 0L, val2: (m_readbackTaking - bytes));
        }
    }
    // Gives back the output bytes a batch or probe was granted, once their storage is freed or was never made.
    internal void ReleaseReadback(long bytes) {
        if (bytes == 0L) { return; }
        lock (m_readbackGate) {
            m_readbackPending -= bytes;
            SettleReadback();
        }
    }

    /// <summary>Copies a dispatched batch's samples into storage the bake's readback reservation covers, which the batch's
    /// answers then read in place (<see cref="Complete(SdfMeshBakeBatch, AnsweredSamples)"/>): no answer copies its slice again,
    /// and the storage is freed when the last of them ends.</summary>
    /// <typeparam name="TState">What <paramref name="fill"/> reads.</typeparam>
    /// <param name="batch">The batch its take granted readback bytes.</param>
    /// <param name="length">The bytes to copy: the batch's output.</param>
    /// <param name="state">What <paramref name="fill"/> reads.</param>
    /// <param name="fill">Fills the copy.</param>
    /// <returns>The copy, holding one reference the caller releases (<see cref="AnsweredSamples.Release"/>).</returns>
    internal AnsweredSamples CopySamples<TState>(SdfMeshBakeBatch batch, int length, TState state, SpanAction<byte, TState> fill) where TState : allows ref struct {
        ArgumentNullException.ThrowIfNull(argument: batch);

        var samples = new AnsweredSamples(bake: this, granted: batch.TakeReadback());

        try {
            samples.Fill(length: length, state: state, fill: fill);
            return samples;
        } catch {
            samples.Release();
            throw;
        }
    }

    /// <summary>A copy of answered samples in the bake's readback storage, shared by the answers that read slices of it
    /// and freed, giving its readback bytes back, when the last reference ends.</summary>
    internal sealed class AnsweredSamples {
        private readonly SdfMeshPathBake m_bake;
        private readonly OwnedBuffer<byte> m_bytes;
        private readonly long m_granted;
        private int m_references = 1;

        public AnsweredSamples(SdfMeshPathBake bake, long granted) {
            m_bake = bake;
            m_granted = granted;
            m_bytes = new OwnedBuffer<byte>(allocator: bake.Library.Allocator, observer: bake.m_readback);
        }

        /// <summary>Gets the copied bytes; valid while a reference is held.</summary>
        public ReadOnlySpan<byte> Span => m_bytes.GetSpan();
        /// <summary>Gets the storage, for a reader that fills it itself (a store's sample set) before any answer reads it.</summary>
        public OwnedBuffer<byte> Storage => m_bytes;

        public void Fill<TState>(int length, TState state, SpanAction<byte, TState> fill) where TState : allows ref struct {
            m_bytes.Resize(clear: false, length: length, preserve: false);
            fill(span: m_bytes.GetSpan(), arg: state);
        }
        /// <summary>Takes another reference, for an answer that reads the copy after its caller releases its own.</summary>
        /// <returns>This copy.</returns>
        public AnsweredSamples Share() {
            _ = Interlocked.Increment(location: ref m_references);
            return this;
        }
        /// <summary>Ends a reference; the last frees the copy and gives back its readback bytes.</summary>
        public void Release() {
            if (Interlocked.Decrement(location: ref m_references) != 0) { return; }
            m_bytes.Dispose();
            m_bake.ReleaseReadback(bytes: m_granted);
        }
    }
}
