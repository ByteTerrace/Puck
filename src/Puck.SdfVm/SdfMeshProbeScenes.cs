using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Channels;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;

namespace Puck.SdfVm;

/// <summary>
/// The probe trace's scene words of every residency attached to one presentation mesh region
/// (<see cref="SdfMeshRegionStore.ProbeScenes"/>): one buffer and one arena, in which a trace geometry
/// (<see cref="SdfMeshIrradianceScene.Geometry"/>) any number of residencies' scenes hold is placed and written once, beside
/// each residency's own prefix and root words. A residency's probe client (its claimant) begins an upload of each scene
/// it publishes (<see cref="Begin"/>), and every residency's upload of a frame carries the words the uploads owe, a chunk
/// at a time, in the order they were begun (<see cref="RecordOwed"/>).
/// </summary>
/// <remarks>
/// <para>Invariant: a word a claimant's query reads was written by a completed copy. A claimant reads only through the
/// root words of an export it published, and it publishes an export only once the upload carrying that export's root
/// words has completed (<see cref="Upload.Rooted"/>). Every other word the export names was owed before the root words,
/// either by this export (Changed) or by another claimant's export whose words a completed copy wrote (the geometry's
/// written prefix), and the store records an upload's chunks before its root, in one queue order, so the root's
/// completion proves them. Enforced by <see cref="RecordOwed"/>, the one place the region's words are recorded, which
/// takes an upload's root only once its worker has handed over every chunk, and by <see cref="Upload.Rooted"/>, set only
/// from that slot's completion.</para>
/// <para>Invariant: no allocation a claimant's query may read is freed or written with other words. A claimant's prefix
/// is freed only as it publishes a replacement, which its client does with no query in flight, or as it lets go once its
/// submissions have completed (<see cref="Release"/>); a geometry is freed only when no claimant claims it
/// (<c>GpuArena</c>). A copy into a freed range lands, in queue order, before the copies of whatever is placed there
/// next, since chunks taken after a free are recorded in a later or the same upload, later in it.</para>
/// <para>The region's copies ride whichever residency's upload opens next (<see cref="SdfWorldTables.OpenUpload"/>),
/// which relies on every residency's upload of a frame reaching the queue before any work of that frame that reads the
/// region records, as the mesh region's owed copies do. Thread-safe: every member takes the store's gate; a claimant's
/// worker creates its export under it.</para>
/// </remarks>
internal sealed class SdfMeshProbeScenes : IDisposable {
    // The region's slots, each an upload's staging until that upload completes.
    private const int Slots = 4;
    private static readonly uint[] Zeros = new uint[(1 << 12)];

    private readonly Lock m_gate = new();
    private readonly IGpuDeviceContext m_device;
    private readonly GpuRegion m_region;
    private readonly MemoryAccount.Owner m_hostCopies = MemoryAccount.Shared.CreateOwner(name: "sdf.probe-scene.host-copies");
    private readonly SdfMeshIrradianceScene.GpuArena m_arena = new();
    private readonly GpuRetirementQueue<IDisposable> m_retired = new();
    private readonly Stack<int> m_freeSlots = new();
    // The uploads begun and not yet rooted or withdrawn, in the order their chunks are recorded.
    private readonly List<Upload> m_uploads = [];
    // The chunks the region owes and has not flushed: those the latest take moved here, kept across a refused flush.
    private readonly List<Chunk> m_staged = [];
    private int m_unsubmitted;
    private bool m_disposed;
    // Whether an allocation was made or freed since the region's chunks were last fitted to the live allocations (Cover),
    // and which chunks the latest fit found covered.
    private bool m_coverOwed;
    private bool[] m_covered = [];
    private long m_unmappedBytes;
    private long m_begun;
    private long m_rooted;
    private long m_withdrawn;
    private long m_recorded;
    private long m_recordedBytes;
    private long m_slotMisses;
    private long m_refusals;

    internal SdfMeshProbeScenes(IGpuDeviceContext device, IGpuComputePipeline copyPipeline) {
        m_device = device;

        var services = device.Services;
        var first = ((services.BufferFactory.MappedChunkBytes is var chunk and > 0UL) ? ((long)chunk) : ((long)SdfGpuWork.UploadChunkBytes));

        m_region = new GpuRegion(
            bindings: services.Bindings,
            buffers: services.BufferFactory,
            byteCount: first,
            copyPipeline: copyPipeline,
            memory: GpuResidency.RingMemory(profile: device.MemoryProfile),
            name: new GpuObjectName(owner: SdfWorldTables.ObjectOwner, part: "probe-scene"),
            policy: GpuResidencyPolicy.Staged,
            recorder: services.Recorder,
            slotCount: Slots,
            hostCopies: m_hostCopies,
            // Grows in place where the device backs a buffer a chunk at a time, so a growing scene never holds two copies.
            mapped: true,
            // Every word comes from an upload's chunks, so the region keeps none of them on the host.
            hostCopyBytes: 0L
        );
        for (var slot = (Slots - 1); (slot >= 0); slot--) { m_freeSlots.Push(item: slot); }
    }

    /// <summary>Gets the buffer every probe client binds at the scene's member. A region that grows by replacement answers
    /// another buffer once it has grown; a client reads it as it records each job.</summary>
    internal IGpuBuffer Buffer { get { lock (m_gate) { return m_region.Buffer(slot: 0); } } }

    /// <summary>Gets the device bytes the region holds. Safe to read from any thread.</summary>
    internal GpuMemoryBytes OwnedBytes { get { lock (m_gate) { return (m_disposed ? default : m_region.OwnedBytes); } } }

    /// <summary>Begins the upload of a claimant's scene: its worker creates the export in the store's arena, claiming each
    /// geometry it names, and cuts the words the arena does not hold yet into chunks, which residencies' uploads record in
    /// the order uploads were begun.</summary>
    /// <param name="claimant">The probe client the export is published for.</param>
    /// <param name="scene">The scene; the upload holds a claim on it until it is disposed.</param>
    /// <param name="placements">The mesh placements the export's instance records address.</param>
    /// <param name="placementVersion">The placements' version.</param>
    /// <param name="rooted">Called on the frame thread once the upload's root words have landed.</param>
    /// <returns>The upload, which the claimant publishes (<see cref="Publish"/>) or withdraws (<see cref="Withdraw"/>),
    /// then disposes.</returns>
    internal Upload Begin(object claimant, SdfMeshIrradianceScene scene, IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements, long placementVersion, Action rooted) {
        var upload = new Upload(store: this, claimant: claimant, scene: scene, placements: placements, placementVersion: placementVersion, rooted: rooted);

        lock (m_gate) {
            m_uploads.Add(item: upload);
            m_begun++;
        }
        upload.Start();
        return upload;
    }
    // Creates an upload's export under the gate, refusing one withdrawn first, so a withdrawn upload never holds an
    // allocation.
    private SdfMeshIrradianceScene.GpuExport CreateExport(Upload upload) {
        lock (m_gate) {
            if (upload.Withdrawn || m_disposed) { throw new OperationCanceledException(); }
            m_coverOwed = true;
            return upload.Scene.CreateGpuExport(placements: upload.Placements, arena: m_arena, claimant: upload.Claimant);
        }
    }
    /// <summary>Publishes a rooted upload's export for its claimant in place of the export it published before: frees
    /// that one's prefix and lets go of the geometries only it named. The claimant has no query in flight.</summary>
    /// <param name="upload">The rooted upload.</param>
    /// <param name="previous">The export the claimant published before, or <see langword="null"/>.</param>
    internal void Publish(Upload upload, SdfMeshIrradianceScene.GpuExport? previous) {
        lock (m_gate) {
            if (m_disposed) { return; }
            m_arena.Publish(claimant: upload.Claimant, export: upload.Export!, previous: previous);
            m_rooted++;
            m_coverOwed = true;
        }
    }
    /// <summary>Withdraws an upload its claimant will not publish: no chunk of it is taken again, and its prefix is freed.
    /// A chunk already owed in the region still lands. The claimant keeps its claims on the geometries.</summary>
    /// <param name="upload">The upload.</param>
    internal void Withdraw(Upload upload) {
        lock (m_gate) {
            if (upload.Withdrawn) { return; }
            upload.Withdrawn = true;
            upload.Cancel();
            m_withdrawn++;
            _ = m_uploads.Remove(item: upload);
            if (!m_disposed && (upload.Export is { } export)) { m_arena.Abandon(export: export); m_coverOwed = true; }
        }
    }
    /// <summary>Lets go of everything a claimant holds: its published prefix and its claims. Called once its
    /// submissions have completed.</summary>
    /// <param name="claimant">The claimant.</param>
    /// <param name="published">The export it published, or <see langword="null"/>.</param>
    internal void Release(object claimant, SdfMeshIrradianceScene.GpuExport? published) {
        lock (m_gate) {
            if (!m_disposed) { m_arena.Release(claimant: claimant, published: published); m_coverOwed = true; }
        }
    }

    /// <summary>Records the words the begun uploads owe into a residency's upload, ahead of its passes: about
    /// <see cref="SdfGpuWork.UploadChunkBytes"/> of chunks, taken in the order the uploads were begun, an upload's root
    /// words after its last chunk. Records nothing when nothing is owed or no slot is free.</summary>
    /// <param name="recorder">The recorder the upload records through.</param>
    /// <param name="commandBuffer">The upload's command buffer.</param>
    /// <param name="readers">The shader stages that read the region.</param>
    /// <returns>Whether a copy was recorded, so the residency reports the upload's submission
    /// (<see cref="SubmittedOwed"/>).</returns>
    internal bool RecordOwed(IGpuRecorder recorder, nint commandBuffer, GpuStage readers) {
        lock (m_gate) {
            if (m_disposed) { return false; }
            if (m_retired.Count > 0) { m_retired.Poll(timeline: m_device.Services.QueueSubmitter.Timeline, release: static item => item.Dispose()); }
            if ((m_staged.Count == 0) && !HasOwed()) { return false; }
            if (!m_freeSlots.TryPop(result: out var slot)) {
                m_slotMisses++;
                return false;
            }
            try {
                if (m_staged.Count == 0) {
                    // Grown first, so every word an export allocated lies inside the region before any is owed.
                    var need = ((long)m_arena.Words * sizeof(uint));

                    if (need > m_region.ByteCount) {
                        var replaced = m_region.Grow(byteCount: (sizeof(uint) * GpuCapacity.Headroom(required: m_arena.Words, ceiling: (GpuRegion.MaxByteCount / sizeof(uint)), divisor: 8)));

                        m_retired.Hold(item: replaced, pendingUse: m_region.PendingCarry);
                        m_coverOwed = true;
                    }
                    if (m_coverOwed) { Cover(); }
                    // The region let the exact source go when its last copy left nothing owed.
                    m_region.Shadow(source: new Source(owner: this), shadowBytes: 0L, exact: true);
                    TakeChunks();
                }
                m_region.Flush(slot: slot);
            } catch (GpuMemoryRefusedException) {
                // What the region owes stays owed, and the next upload tries again.
                m_freeSlots.Push(item: slot);
                m_refusals++;
                return false;
            }

            var copies = new GpuRegionCopyRecording(recorder: recorder, readers: readers, begin: () => commandBuffer);

            copies.Record(flushed: true, handsToReaders: true, region: m_region, slot: slot);
            _ = copies.Finish();

            var carried = m_staged.ToArray();

            m_staged.Clear();
            m_unsubmitted++;
            m_retired.Hold(item: new SlotUse(store: this, slot: slot, chunks: carried), pendingUse: () => (m_unsubmitted > 0));
            return true;
        }
    }
    // Fits a mapped region's chunks to the arena's live allocations: gives back every whole chunk no allocation touches,
    // which no query reads and whose owed words the region drops, then backs every chunk an allocation touches, so a
    // freed prefix or geometry costs no device memory once its chunks hold nothing live. A chunk given back is released
    // once the uploads recorded before now have been submitted and completed. Throws GpuMemoryRefusedException, leaving
    // the fit owed, when the device refuses a chunk. Under the gate.
    private void Cover() {
        if (!m_region.Mapped) { m_coverOwed = false; return; }

        var chunk = m_region.MappedChunkBytes;
        var size = m_region.ByteCount;
        var chunks = ((int)((size + (chunk - 1)) / chunk));
        var chunkWords = (chunk / sizeof(uint));

        if (m_covered.Length < chunks) { m_covered = new bool[chunks]; }
        Array.Clear(array: m_covered, index: 0, length: chunks);
        foreach (var (start, words) in m_arena.Live) {
            var last = Math.Min(val1: (chunks - 1), val2: ((int)((start + words - 1L) / chunkWords)));

            for (var index = ((int)(start / chunkWords)); (index <= last); index++) { m_covered[index] = true; }
        }
        for (var index = 0; (index < chunks); index++) {
            var offset = (index * chunk);
            var bytes = Math.Min(val1: chunk, val2: (size - offset));

            if (m_covered[index] || (bytes != chunk) || !m_region.IsBacked(bytes: bytes, offset: offset)) { continue; }
            var release = m_region.UnmapRange(bytes: bytes, offset: offset);

            m_unmappedBytes += ((long)release.Bytes);
            m_retired.Hold(item: release, pendingUse: () => (m_unsubmitted > 0));
        }
        for (var index = 0; (index < chunks); index++) {
            var offset = (index * chunk);
            var bytes = Math.Min(val1: chunk, val2: (size - offset));

            if (m_covered[index] && !m_region.IsBacked(bytes: bytes, offset: offset)) { m_region.MapRange(bytes: bytes, offset: offset); }
        }
        m_coverOwed = false;
    }
    // Whether a begun upload has a chunk or its root ready to take.
    private bool HasOwed() {
        foreach (var upload in m_uploads) {
            if (upload.Ready) { return true; }
        }
        return false;
    }
    // Moves chunks from the begun uploads, in order, into the region's owed words, until about one chunk's bytes.
    private void TakeChunks() {
        var bytes = 0L;

        for (var index = 0; (index < m_uploads.Count) && (bytes < SdfGpuWork.UploadChunkBytes);) {
            var upload = m_uploads[index];

            if (!upload.TryTake(chunk: out var chunk)) {
                index++;
                continue;
            }
            m_region.OweSourced(offset: (chunk.Word * 4L), length: (chunk.Count * 4L));
            m_staged.Add(item: chunk);
            bytes += (chunk.Count * 4L);
            upload.Bytes += (chunk.Count * 4L);
            m_recorded++;
            m_recordedBytes += (chunk.Count * 4L);
            // An upload whose root is taken owes nothing more.
            if (chunk.IsRoot) { m_uploads.RemoveAt(index: index); }
        }
    }
    /// <summary>Reports that the upload a residency recorded the region's owed copies into has been submitted.</summary>
    internal void SubmittedOwed() {
        lock (m_gate) { if (m_unsubmitted > 0) { m_unsubmitted--; } }
    }
    /// <summary>Releases what the device has finished reading, on the frame thread: the slots of completed uploads, which
    /// land their chunks and root the uploads whose root words they carried.</summary>
    internal void Service() {
        if (!m_gate.TryEnter()) { return; }
        try {
            if (m_disposed) { return; }
            if (m_retired.Count > 0) { m_retired.Poll(timeline: m_device.Services.QueueSubmitter.Timeline, release: static item => item.Dispose()); }
            // Gives back the chunks frees left uncovered; a chunk the device refuses is backed at the next upload's fit.
            if (m_coverOwed) {
                try { Cover(); } catch (GpuMemoryRefusedException) { m_refusals++; }
            }
        } finally {
            m_gate.Exit();
        }
    }

    /// <summary>Describes the store for <c>world.residency</c>: its region's bytes, its arena's words and geometries, the
    /// geometries more than one claimant holds, and its uploads, chunks and refusals.</summary>
    /// <returns>One line.</returns>
    internal string Describe() {
        lock (m_gate) {
            if (m_disposed) { return "probe-scenes disposed"; }

            var bytes = m_region.OwnedBytes;
            var shared = 0;
            var sharedWords = 0L;

            foreach (var (geometry, placement) in m_arena.Geometries) {
                if (placement.Claims.Count < 2) { continue; }
                shared++;
                sharedWords += geometry.GpuWords;
            }
            return string.Create(provider: CultureInfo.InvariantCulture, handler: $"probe-scenes device-bytes={bytes.DeviceLocal} upload-bytes={bytes.HostVisible} words={m_arena.Words} live-bytes={m_arena.LiveWords * sizeof(uint)} unmapped-bytes={m_unmappedBytes} geometries={m_arena.Geometries.Count} geometries-shared={shared} shared-bytes={sharedWords * sizeof(uint)} uploads={m_begun} rooted={m_rooted} withdrawn={m_withdrawn} pending={m_uploads.Count} chunks={m_recorded} recorded-bytes={m_recordedBytes} slot-misses={m_slotMisses} refusals={m_refusals}");
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (m_gate) {
            if (m_disposed) { return; }
            m_disposed = true;
            m_retired.Release(completed: ulong.MaxValue, release: static item => item.Dispose());
            foreach (var chunk in m_staged) { chunk.Dispose(); }
            m_staged.Clear();
            m_uploads.Clear();
            m_region.Dispose();
            m_hostCopies.Dispose();
        }
    }

    // An upload slot coming back once the upload that recorded into it has completed: its chunks have landed.
    private sealed class SlotUse(SdfMeshProbeScenes store, int slot, Chunk[] chunks) : IDisposable {
        public void Dispose() {
            foreach (var chunk in chunks) {
                var upload = chunk.Upload;

                if (!store.m_disposed) {
                    if (!chunk.IsRoot) {
                        store.m_arena.Complete(export: upload.Export!, word: chunk.Word, count: chunk.Count);
                    } else if (!upload.Withdrawn) {
                        upload.Rooted = true;
                        upload.Wake();
                    }
                }
                chunk.Dispose();
            }
            if (store.m_disposed) { return; }
            store.m_region.Retire(slot: slot);
            store.m_freeSlots.Push(item: slot);
        }
    }

    // The region's exact source while a flush stages the owed chunks: it answers each run from the chunks that owe it.
    // Two claimants' chunks may owe the same geometry words, which hold the same values, so either answers.
    private sealed class Source(SdfMeshProbeScenes owner) : IGpuRegionSource {
        public void WriteWords(IGpuStorageBuffer buffer, ulong destinationOffsetBytes, int word, int count) {
            var chunks = owner.m_staged;

            while (count > 0) {
                Chunk? covering = null;
                var next = int.MaxValue;

                foreach (var chunk in chunks) {
                    if ((chunk.Word <= word) && (word < (chunk.Word + chunk.Count))) { covering = chunk; break; }
                    if (chunk.Word > word) { next = Math.Min(val1: next, val2: chunk.Word); }
                }
                // An exact region asks only for words a chunk owed; a word no chunk holds is written zero.
                var take = ((covering is null)
                    ? Math.Min(val1: Math.Min(val1: count, val2: (next - word)), val2: Zeros.Length)
                    : Math.Min(val1: count, val2: ((covering.Word + covering.Count) - word)));

                buffer.Write<uint>(data: ((covering is null) ? Zeros.AsSpan(start: 0, length: take) : covering.Words.AsSpan(start: (word - covering.Word), length: take)), destinationOffsetBytes: destinationOffsetBytes);
                destinationOffsetBytes += (((ulong)take) * sizeof(uint));
                word += take;
                count -= take;
            }
        }
    }

    /// <summary>One claimant's scene on its way into the store: its export, created by a worker, the chunks that worker
    /// cuts from the words the arena does not hold yet, and its root words last.</summary>
    internal sealed class Upload : IDisposable {
        private readonly SdfMeshProbeScenes m_store;
        private readonly Action m_rooted;
        private readonly Channel<Chunk> m_chunks = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(SdfGpuWork.SlotCount) { SingleReader = true, SingleWriter = true });
        private readonly CancellationTokenSource m_cancel = new();
        private Chunk? m_root;
        private Task? m_work;

        internal Upload(SdfMeshProbeScenes store, object claimant, SdfMeshIrradianceScene scene, IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements, long placementVersion, Action rooted) {
            m_store = store;
            m_rooted = rooted;
            Claimant = claimant;
            Scene = scene.Acquire();
            PlacementVersion = placementVersion;
            Placements = scene.CaptureGpuPlacements(placements);
        }

        internal object Claimant { get; }
        internal SdfMeshIrradianceScene Scene { get; }
        internal long PlacementVersion { get; }
        internal IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> Placements { get; }
        internal SdfMeshIrradianceScene.GpuExport? Export { get; private set; }
        // The bytes recorded for the upload, under the store's gate.
        internal long Bytes { get; set; }
        // Whether the claimant withdrew it, under the store's gate.
        internal bool Withdrawn { get; set; }
        /// <summary>Gets whether the upload's root words have landed: the claimant may publish it.</summary>
        internal bool Rooted { get => Volatile.Read(ref m_rootedFlag); set => Volatile.Write(ref m_rootedFlag, value); }
        private bool m_rootedFlag;
        internal Task Work => m_work!;
        internal bool Cancelled => m_cancel.IsCancellationRequested;
        // Whether a chunk or the root is ready to take, under the store's gate.
        internal bool Ready => (m_chunks.Reader.Count > 0) || (m_work is { IsCompletedSuccessfully: true } && m_chunks.Reader.Completion.IsCompleted && (Volatile.Read(ref m_root) is not null));

        internal void Start() => m_work = Task.Run(async () => {
            var export = m_store.CreateExport(upload: this);

            Export = export;
            foreach (var (word, count) in export.Changed) {
                for (var offset = 0; offset < count;) {
                    m_cancel.Token.ThrowIfCancellationRequested();
                    var take = Math.Min(count - offset, SdfGpuWork.UploadChunkBytes / sizeof(uint));
                    var chunk = new Chunk(upload: this, word: word + offset, words: RentedArrays.Rent<uint>(minimumLength: take), count: take, rented: true);
                    try {
                        export.Read(word + offset, chunk.Words.AsSpan(0, take));
                        await m_chunks.Writer.WriteAsync(chunk, m_cancel.Token).ConfigureAwait(false);
                    } catch { chunk.Dispose(); throw; }
                    offset += take;
                }
            }
            Volatile.Write(ref m_root, new Chunk(upload: this, word: export.Prefix, words: [export.Root, export.Instances], count: SdfMeshProbeTrace.RootWords, rented: false));
            m_chunks.Writer.Complete();
        }, m_cancel.Token);

        // Takes the next chunk, then the root once the worker has handed over every chunk. Under the store's gate.
        internal bool TryTake([NotNullWhen(true)] out Chunk? chunk) {
            if (m_chunks.Reader.TryRead(out chunk)) { return true; }
            // Worker completion alone can race an empty TryRead: the writer may have queued its last item since it.
            chunk = ((m_work is { IsCompletedSuccessfully: true } && m_chunks.Reader.Completion.IsCompleted) ? Interlocked.Exchange(ref m_root, null) : null);
            return chunk is not null;
        }
        internal void Wake() => m_rooted();
        internal void Cancel() => m_cancel.Cancel();

        public void Dispose() {
            Cancel();
            try { m_work?.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch when (m_work?.IsFaulted ?? false) { } // The claimant reports a packing fault; teardown still releases.
            finally {
                while (m_chunks.Reader.TryRead(out var chunk)) { chunk.Dispose(); }
                Interlocked.Exchange(ref m_root, null)?.Dispose();
                Scene.Dispose();
                m_cancel.Dispose();
            }
        }
    }

    /// <summary>A run of an upload's words, read from its export on the upload's worker, or its root words.</summary>
    internal sealed class Chunk(Upload upload, int word, uint[] words, int count, bool rented) : IDisposable {
        private int m_disposed;
        internal Upload Upload { get; } = upload;
        internal int Word { get; } = word;
        internal uint[] Words { get; } = words;
        internal int Count { get; } = count;
        internal bool IsRoot => !rented;
        public void Dispose() {
            if ((Interlocked.Exchange(ref m_disposed, 1) == 0) && rented) { RentedArrays.Return(array: Words); }
        }
    }
}
