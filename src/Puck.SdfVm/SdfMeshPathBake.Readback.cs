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
    // sample set read in place of a dispatch) is charged to m_readback, whose reservation is ReadbackBytes, so the charge
    // never asks the account for room and is never refused. The one door that grants those bytes is the take: it adds a
    // batch's or a probe's output bytes to m_readbackPending, under the gate, only while they fit what is left of
    // ReadbackBytes, and takes nothing at all until the reservation is held. The bytes leave m_readbackPending only when
    // their storage is freed (AnsweredSamples.Release, or a batch that never copied, ReleaseReadback). The reservation is taken
    // before any group of the bake is admitted and is held while any group is still baking or any answer is held, so the
    // groups the account admitted can always be answered: a readback never waits on room the groups it answers hold.
    private readonly MemoryAccount.Owner m_readback = MemoryAccount.Shared.CreateOwner(allowance: MemoryAllowance.Authoring, memoryClass: MemoryClass.Workspace, name: "mesh-bake-readback");
    // The output bytes of the batches and probes taken whose storage is not yet freed; written with Interlocked.
    private long m_readbackPending;
    // Under m_readbackGate: whether m_readback holds its reservation; whether the latest take found nothing to take (no
    // group still baking and no slice), or the bake's residency was released, so the reservation goes once no answer
    // holds any of it; and whether the wait for the reservation has been reported since it was last held.
    private bool m_readbackReserved;
    private bool m_readbackIdle;
    private bool m_readbackRetired;
    private bool m_readbackReported;
    private readonly Lock m_readbackGate = new();

    // Takes the readback reservation for a take that has lattices to take, or keeps it; false when the account has no
    // room for it (or the residency was released), and the take then takes nothing, so no group of the bake is admitted
    // without it. Under the gate.
    private bool HoldReadback() {
        lock (m_readbackGate) {
            m_readbackIdle = false;
            if (m_readbackReserved) { return true; }
            if (m_readbackRetired) { return false; }
            if (!m_readback.TryReserveProgress(peak: ReadbackBytes)) {
                if (!m_readbackReported) { SdfMeshBakeReport.ReadbackDeferred(residency: Residency, bytes: ReadbackBytes); m_readbackReported = true; }
                m_accounts.ReadbackWaits++;
                return false;
            }
            m_readbackReported = false;
            m_readbackReserved = true;
            return true;
        }
    }
    // Records that a take found nothing to take, and lets the reservation go if no answer holds any of it. Under the gate.
    private void IdleReadback() {
        lock (m_readbackGate) {
            m_readbackIdle = true;
            ReleaseUnheldReadback();
        }
    }
    // Under m_readbackGate: lets the reservation go when the bake needs none (idle or retired) and no answer holds any.
    private void ReleaseUnheldReadback() {
        if (!m_readbackReserved || !(m_readbackIdle || m_readbackRetired) || (Interlocked.Read(location: ref m_readbackPending) != 0L)) { return; }
        m_readback.ReleaseReservation();
        m_readbackReserved = false;
    }
    // The readback bytes the take may still grant: what is left of ReadbackBytes.
    private long ReadbackRoom => (ReadbackBytes - Interlocked.Read(location: ref m_readbackPending));

    // Grants a batch or probe its output bytes, which the take found within ReadbackRoom.
    private void GrantReadback(long bytes) => _ = Interlocked.Add(location1: ref m_readbackPending, value: bytes);
    // Gives back the output bytes a batch or probe was granted, once their storage is freed or was never made.
    internal void ReleaseReadback(long bytes) {
        if (bytes == 0L) { return; }
        if (Interlocked.Add(location1: ref m_readbackPending, value: -bytes) != 0L) { return; }
        lock (m_readbackGate) { ReleaseUnheldReadback(); }
    }
    // Marks the residency released: the reservation goes with the last answer, or now when none is held.
    private void RetireReadback() {
        lock (m_readbackGate) {
            m_readbackRetired = true;
            ReleaseUnheldReadback();
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
