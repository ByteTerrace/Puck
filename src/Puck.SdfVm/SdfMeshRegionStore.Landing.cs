using System.Globalization;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;

namespace Puck.SdfVm;

// The store's landing: every placed mesh's words reach the region through a residency's background GPU work
// (SdfGpuWork), in jobs of what that service's upload budget allows (at most SdfGpuWork.MostUploadJobBytes), never through
// a frame's upload. The owed meshes a draw of any residency names are taken in the order the residencies' views see them
// largest (SdfMeshDemand), so the scenes fill in from the cameras outward, and after them the meshes named ahead of the
// draws, which no draw waits on; a large mesh lands in several jobs. A worker packs each job's words from the meshes'
// own arrays into a host-visible buffer of the store's ahead of the take (TryTakePackedLanding), so the frame thread
// writes no mesh word; the job's recording gathers them from it into the region (GpuRegion.TryOweGather) in a slot of
// the store's own, after any move the region owes.
//
// Invariant: a mesh leaves the owed set, and so may be named by a staged draw record, only once every word of it at its
// current placement has been copied by a completed submission: CompleteLanding, on the fence the service observed, or
// the recorded move of a standing mesh (MoveMesh), which every upload records ahead of its passes. A plan that moves a
// mesh or forgets a placement drops that mesh's progress, so a job taken for its old words records none of them, and
// one already in flight completes into a progress no longer kept (LandingProgress identity) and lands nothing; its
// words were queued before any later job's, which rewrites them for the mesh the arena gave them to.
public sealed partial class SdfMeshRegionStore {
    /// <summary>The stages that read the region: what a region copy hands its buffer to.</summary>
    internal const GpuStage Readers = GpuStage.ComputeShader | GpuStage.VertexShader | GpuStage.FragmentShader;

    // The placed meshes whose words the region still owes.
    private readonly OwedMeshes m_owed;
    // Each owed mesh whose words a job has taken, with how many have been taken and how many have landed.
    private readonly Dictionary<SdfMesh, LandingProgress> m_landing = new(comparer: ReferenceEqualityComparer.Instance);
    // The landing order: the owed meshes a draw row of any residency names, each with its rank, those the views see
    // largest first, and the first that may still have words to take.
    //
    // Invariant: m_landingRanks holds exactly the meshes the owed set holds that a row names, and each residency's owed
    // words are the sum of the ranked meshes its rows name. Every change to either side reaches m_landingTouched,
    // through the owed set's one door (OwedMeshes) and the two places a row's name changes (Name, Unname); RankLanding
    // settles each touched mesh before any take, and CompleteLanding settles the mesh it lands at once. The ranks
    // themselves may be a few stagings behind the views: a mesh is ranked when it joins, when a row's name of it
    // changes, while it is among the first the order takes (LandingHeadRanks), and otherwise in turn under the sweep's
    // budget (LandingSweepRows). Order never changes a mesh, only when it lands.
    private readonly Dictionary<SdfMesh, LandingRank> m_landingRanks = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMesh> m_landingTouched = [];
    // The ranks in landing order as of the last merge, any that left since included (LandingRank.Staged false), which
    // the takes walk; the list the next merge writes; and the ranks a measure moved since the last merge.
    private List<LandingRank> m_landingRanked = [];
    private List<LandingRank> m_landingMerged = [];
    private readonly List<LandingRank> m_landingMoved = [];
    // The ranks in the order they were last measured, each with the measure it was queued at, so the sweep measures the
    // stalest first and passes over an entry a later measure made stale.
    private readonly Queue<(LandingRank Rank, long Measure)> m_landingStalest = new();

    private int m_landingCursor;
    private int m_landingLeft;
    // One more for every RankLanding: a rank whose LandingRank.Pass is the current one was measured in it.
    private long m_landingPass;

    // The meshes the order holds first, measured again every staging, and the most rows the sweep measures a staging.
    private const int LandingHeadRanks = 16;
    private const int LandingSweepRows = 256;

    // The owed meshes named ahead of the draws that the order above does not hold, and the first that may still have
    // words to take: listed again before a take once the set named ahead, or the owed set, changed.
    private readonly List<SdfMesh> m_landingAhead = [];

    private int m_landingAheadCursor;
    private bool m_landingAheadStale = true;
    // The landing jobs submitted and the bytes they carried, the largest one, and those that landed nothing because a
    // plan dropped their progress while they were in flight.
    private long m_landingJobs;
    private long m_landingBytes;
    private long m_landingMostBytes;
    private long m_landingStale;

    // The most slices one landing job carries: half the runs a region copy keeps apart.
    private const int LandingSlices = (GpuRegion.MaxCopyRuns / 2);

    /// <summary>Gets how many meshes have a landing job's words taken and not yet landed.</summary>
    internal int LandingCount { get { lock (m_gate) { return m_landing.Count; } } }
    /// <summary>Gets how many placed meshes the region still owes.</summary>
    internal int OwedCount { get { lock (m_gate) { return m_owed.Count; } } }

    /// <summary>Settles the touched meshes into the landing order and measures the ranks it owes a measure: a mesh that
    /// joined or a row's name of which changed, and with <paramref name="sweep"/> (a residency's staging) the first the
    /// order takes and in turn the stalest, under the sweep's budget.</summary>
    /// <param name="sweep">Whether a residency staged its draws, so the head of the order and the stalest ranks are
    /// measured again.</param>
    internal void RankLanding(bool sweep) {
        lock (m_gate) {
            var rows = 0;
            var visited = m_landingTouched.Count;

            m_landingPass++;
            if (sweep) {
                m_landingCursor = 0;
                m_landingAheadCursor = 0;
            }
            foreach (var mesh in m_landingTouched) { rows += SettleLanding(mesh: mesh); }
            m_landingTouched.Clear();
            if (sweep && (m_landingRanks.Count > 0)) {
                for (var index = 0; ((index < m_landingRanked.Count) && (index < LandingHeadRanks)); index++) {
                    if ((m_landingRanked[index] is { Staged: true } head) && (head.Pass != m_landingPass)) {
                        rows += MeasureLanding(rank: head);
                        visited++;
                    }
                }
                var swept = 0;

                while ((swept < LandingSweepRows) && m_landingStalest.TryPeek(result: out var stalest)) {
                    var current = ((stalest.Rank.Measures == stalest.Measure) && stalest.Rank.Staged);

                    // A queued rank measured in this pass was queued after every entry still ahead of it, so the entries
                    // left were all measured in it.
                    if (current && (stalest.Rank.Pass == m_landingPass)) { break; }
                    _ = m_landingStalest.Dequeue();
                    if (!current) { continue; }
                    swept += Math.Max(val1: 1, val2: MeasureLanding(rank: stalest.Rank));
                    visited++;
                }
                rows += swept;
            }
            if ((m_landingMoved.Count > 0) || (m_landingLeft > m_landingRanks.Count)) { MergeLanding(); }
            if (visited > 0) {
                SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.LandingMeshesVisited, amount: visited);
                SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.LandingRowsOrdered, amount: rows);
            }
        }
    }
    // Brings one touched mesh's place in the landing order to what the owed set and the rows say: an owed mesh a row
    // names joins and is measured, one measured already in this pass is not measured again, and one no row names or no
    // longer owed leaves. The rows it measured.
    private int SettleLanding(SdfMesh mesh) {
        var staged = (m_owed.Contains(mesh: mesh) && m_uses.ContainsKey(key: mesh));

        if (!m_landingRanks.TryGetValue(key: mesh, value: out var rank)) {
            if (!staged) { return 0; }
            rank = new LandingRank(mesh: mesh, words: SdfMeshRegion.WordsOf(mesh: mesh));
            m_landingRanks.Add(key: mesh, value: rank);
            m_landingAheadStale = true;
            AddOwedWords(mesh: mesh, words: rank.Words);
        } else if (!staged) {
            _ = LeaveLanding(mesh: mesh);
            return 0;
        }

        return ((rank.Pass == m_landingPass) ? 0 : MeasureLanding(rank: rank));
    }
    // Adds a ranked mesh's words to, or takes them from, the owed words of every residency whose rows name it.
    private void AddOwedWords(SdfMesh mesh, long words) {
        if (!m_uses.TryGetValue(key: mesh, value: out var use)) { return; }
        for (var index = 0; (index < use.Count); index++) { use.Clients[index].AddOwedWords(delta: words); }
    }
    // Takes a mesh out of the landing order, its words out of the residencies' owed counts; false when the order did not
    // hold it.
    private bool LeaveLanding(SdfMesh mesh) {
        if (!m_landingRanks.Remove(key: mesh, value: out var rank)) { return false; }
        rank.Staged = false;
        m_landingLeft++;
        m_landingAheadStale = true;
        AddOwedWords(mesh: mesh, words: -rank.Words);
        return true;
    }
    // Measures a rank from the rows naming its mesh in every residency: the highest priority among them and the first
    // row at it. A rank that joined, or whose priority or row the measure changed, moves at the next merge; every one is
    // queued to be measured again in turn. The rows it measured.
    private int MeasureLanding(LandingRank rank) {
        var best = float.NegativeInfinity;
        var first = -1;
        var rows = 0;

        if (m_uses.TryGetValue(key: rank.Mesh, value: out var use)) {
            for (var index = 0; (index < use.Count); index++) { rows += use.Clients[index].MeasureLanding(mesh: rank.Mesh, best: ref best, first: ref first); }
        }
        if (((rank.Measures == 0) || (best != rank.Priority) || (first != rank.Row)) && !rank.Moved) {
            rank.Moved = true;
            m_landingMoved.Add(item: rank);
        }
        rank.Priority = best;
        rank.Row = first;
        rank.Pass = m_landingPass;
        rank.Measures++;
        // Entries a later measure made stale wait in the queue until the sweep reaches them; past a few per rank the
        // queue is listed again from the ranks themselves, those measured in this pass last.
        if (m_landingStalest.Count > ((4 * m_landingRanks.Count) + LandingSweepRows)) {
            m_landingStalest.Clear();
            foreach (var held in m_landingRanks.Values) {
                if (held.Pass != m_landingPass) { m_landingStalest.Enqueue(item: (held, held.Measures)); }
            }
            foreach (var held in m_landingRanks.Values) {
                if ((held.Pass == m_landingPass) && !ReferenceEquals(objA: held, objB: rank)) { m_landingStalest.Enqueue(item: (held, held.Measures)); }
            }
        }
        m_landingStalest.Enqueue(item: (rank, rank.Measures));
        return rows;
    }
    // Merges the ranks a measure moved since the last merge, sorted, into the order the rest keep, leaving out those
    // that left.
    private void MergeLanding() {
        var moved = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_landingMoved);
        var order = new SdfMeshPriorityOrder();
        var next = 0;

        moved.Sort(comparison: static (left, right) => new SdfMeshPriorityOrder().Compare(x: (left.Priority, left.Row), y: (right.Priority, right.Row)));
        m_landingMerged.Clear();
        foreach (var rank in m_landingRanked) {
            if (!rank.Staged || rank.Moved) { continue; }
            for (; ((next < moved.Length) && (order.Compare(x: (moved[next].Priority, moved[next].Row), y: (rank.Priority, rank.Row)) < 0)); next++) {
                if (moved[next].Staged) { m_landingMerged.Add(item: moved[next]); }
            }
            m_landingMerged.Add(item: rank);
        }
        for (; (next < moved.Length); next++) {
            if (moved[next].Staged) { m_landingMerged.Add(item: moved[next]); }
        }
        foreach (var rank in moved) { rank.Moved = false; }
        m_landingMoved.Clear();
        (m_landingRanked, m_landingMerged) = (m_landingMerged, m_landingRanked);
        m_landingMerged.Clear();
        m_landingLeft = 0;
        m_landingCursor = 0;
        SdfMeshHistory.ProcessWork.Count(kind: SdfMeshHistory.LandingOrders);
    }
    /// <summary>Notes that a residency's draw of a mesh changed, so its place in the landing order may have.</summary>
    /// <param name="mesh">The mesh, or <see langword="null"/> for none.</param>
    internal void TouchLanding(SdfMesh? mesh) {
        if (mesh is null) { return; }
        lock (m_gate) { if (m_landingRanks.Count > 0) { m_landingTouched.Add(item: mesh); } }
    }
    // Lists the owed meshes named ahead after the landing order, when the set named ahead or the order changed since:
    // a draw with a hole in the frame lands before a mesh whose draws still read the one it replaces.
    private void ListLandingAhead() {
        if (!m_landingAheadStale) { return; }
        m_landingAheadStale = false;
        m_landingAhead.Clear();
        m_landingAheadCursor = 0;
        m_scratch.Clear();
        m_arena.ListAhead(meshes: m_scratch);
        foreach (var mesh in m_scratch) {
            if (m_owed.Contains(mesh: mesh) && !m_landingRanks.ContainsKey(key: mesh)) { m_landingAhead.Add(item: mesh); }
        }
        m_scratch.Clear();
    }
    // Drops the progress of a mesh whose words a plan moved or forgot: its next words land from the start, at its
    // current placement.
    private void ForgetLanding(SdfMesh mesh) {
        _ = m_landing.Remove(key: mesh);
        m_landingCursor = 0;
        m_landingAheadCursor = 0;
    }
    // Takes the next owed words in landing order, up to one chunk, from as many meshes as fit and at most LandingSlices of
    // them, each slice one run of the region's copy; none when every ordered mesh's words are taken.
    private bool TryTakeLanding(int uploadBytes, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out LandingJob? job) {
        job = null;
        if (m_landingTouched.Count > 0) { RankLanding(sweep: false); }
        if (m_owed.Count == 0) { return false; }
        ListLandingAhead();

        var budget = (uploadBytes / sizeof(uint));
        var taken = 0;

        TakeLanding(budget: budget, cursor: ref m_landingCursor, job: ref job, order: new RankedLandingOrder(Ranks: m_landingRanked), taken: ref taken);
        TakeLanding(budget: budget, cursor: ref m_landingAheadCursor, job: ref job, order: new MeshLandingOrder(Meshes: m_landingAhead), taken: ref taken);
        if (job is not null) { job.Words = taken; }
        return (job is not null);
    }
    // Takes owed words from one order, from its cursor, into a job that may hold slices already.
    private void TakeLanding<TOrder>(TOrder order, ref int cursor, int budget, ref int taken, ref LandingJob? job) where TOrder : struct, ILandingOrder {
        // A mesh whose storage is still coming back from the store (SdfMesh.Resident) is passed over, and the cursor stays
        // at the first such mesh so a later take finds it restored.
        var waiting = -1;

        while ((cursor < order.Count) && (taken < budget) && ((job?.Slices.Count ?? 0) < LandingSlices)) {
            var mesh = order.MeshAt(index: cursor);

            if (!m_owed.Contains(mesh: mesh) || !m_arena.Names(mesh: mesh) || !m_placements.TryGetValue(key: mesh, value: out var placement)) { cursor++; continue; }
            if (!mesh.Resident) {
                if (waiting < 0) { waiting = cursor; }
                cursor++;
                continue;
            }
            if (!m_landing.TryGetValue(key: mesh, value: out var progress)) {
                progress = new LandingProgress(words: SdfMeshRegion.WordsOf(mesh: mesh));
                m_landing.Add(key: mesh, value: progress);
            }

            if (progress.Pages is { IsCurrent: false }) {
                // Already visiting this mesh: do not rewind the whole order for every stale page lease.
                _ = m_landing.Remove(key: mesh);
                continue;
            }
            if (progress.Pages is null) {
                progress.Pages = m_region.TryLease((((long)placement.VertexWord) * sizeof(uint)), (((long)progress.Words) * sizeof(uint)));
                if (progress.Pages is null) {
                    if (waiting < 0) { waiting = cursor; }
                    cursor++;
                    continue;
                }
            }
            var take = Math.Min(val1: (progress.Words - progress.Taken), val2: (budget - taken));

            if (take > 0) {
                // The job's words are packed one slice after another, each from the word the slices before it end at.
                (job ??= new LandingJob(store: this)).Slices.Add(item: new LandingSlice(Mesh: mesh, Progress: progress, Word: progress.Taken, Count: take,
                    Source: taken, Placement: placement));
                progress.Taken += take;
                taken += take;
            }
            if (progress.Taken == progress.Words) { cursor++; }
        }
        if (waiting >= 0) { cursor = Math.Min(val1: cursor, val2: waiting); }
    }
    /// <summary>Records a packed job's copy into the job's slot of the region: each slice whose words still belong where
    /// they were packed for is gathered from the job's buffer, after any move the region owes, in one recording of the
    /// region. A slice whose progress a plan dropped since the take, or whose mesh no longer lies where its words were
    /// packed for, owes nothing.</summary>
    /// <param name="job">The job a take handed over.</param>
    /// <param name="recording">The service's recording.</param>
    /// <exception cref="InvalidOperationException">The recording submits on another queue than the device's.</exception>
    internal void RecordLanding(object job, in SdfGpuWorkRecording recording) {
        lock (m_gate) {
            var landing = ((LandingJob)job);

            if (!ReferenceEquals(objA: recording.Queue, objB: m_device.Services.QueueSubmitter)) {
                throw new InvalidOperationException(message: "Mesh landing must submit on the device's frame queue; row reuse requires its order.");
            }

            var words = 0;

            for (var index = 0; (index < landing.Slices.Count); index++) {
                var slice = landing.Slices[index];

                if (!m_landing.TryGetValue(key: slice.Mesh, value: out var progress) || !ReferenceEquals(objA: progress, objB: slice.Progress) ||
                    !m_placements.TryGetValue(key: slice.Mesh, value: out var placement) || (placement.VertexWord != slice.Placement.VertexWord) ||
                    (progress.Pages is not { IsCurrent: true }) || !landing.PackedSlices[index]) { continue; }
                if (!m_region.TryOweGather(destinationOffset: ((((long)placement.VertexWord) + slice.Word) * sizeof(uint)), source: landing.Buffer!,
                    sourceWord: slice.Source, words: slice.Count)) { continue; }
                landing.Slices[index] = slice with { Recorded = true };
                words += slice.Count;
            }
            recording.CopyRegion(readers: Readers, region: m_region, regionSlot: landing.Slot);

            var bytes = (((long)words) * sizeof(uint));

            m_wordsWritten += words;
            m_landingJobs++;
            m_landingBytes += bytes;
            m_landingMostBytes = Math.Max(val1: m_landingMostBytes, val2: bytes);
        }
    }
    /// <summary>Takes the job a worker packed when it is ready and fits what the service allows now, and starts packing
    /// the next, so one job packs while the one before it is recorded and copied. While a job packs, none is owed, or no
    /// slot of the region is free, it hands over none. A ready job priced past what is left while work is queued waits,
    /// its price held.</summary>
    /// <param name="uploadBytes">The most bytes the next job may carry.</param>
    /// <param name="allowance">What the service allows the job.</param>
    /// <param name="wake">The service a finished packing wakes.</param>
    /// <param name="job">The job, when this returns <see langword="true"/>.</param>
    /// <param name="estimatedSeconds">The job's price, or the price of one the service should hold room for.</param>
    /// <returns><see langword="true"/> when a job was handed over.</returns>
    internal bool TryTakePackedLanding(int uploadBytes, SdfGpuWorkAllowance allowance, SdfGpuWork wake, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job, out double estimatedSeconds) {
        lock (m_gate) {
            job = null;
            estimatedSeconds = 0d;
            if (m_disposed) { return false; }
            if (m_landingPacking is { } packing) {
                if (!packing.Pack!.IsCompleted || (m_freeLandingSlots.Count == 0)) { return false; }

                var bytes = (((long)packing.Words) * sizeof(uint));
                var price = SdfGpuWork.UploadSecondsOf(bytes: bytes);

                // The job was cut to the upload bytes its packing began under; the budget's credit carries what it takes
                // past what is allowed now.
                if (allowance.Queued && !allowance.Admits(seconds: price)) {
                    estimatedSeconds = price;
                    return false;
                }
                m_landingPacking = null;
                ReleasePackClaims(job: packing);
                // A packer that faulted lands nothing, and its meshes' words are taken again; the fault reaches the service.
                if (packing.Pack.Exception is { } fault) {
                    AbandonLandingJob(job: packing);
                    ReturnLandingBuffer(buffer: packing.Buffer, used: false);
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(source: fault.GetBaseException());
                }
                packing.Slot = m_freeLandingSlots.Pop();
                job = packing;
                estimatedSeconds = price;
                StartPackingLanding(uploadBytes: uploadBytes, wake: wake);
                return true;
            }
            StartPackingLanding(uploadBytes: uploadBytes, wake: wake);
            return false;
        }
    }
    // Takes the next owed words in landing order and hands them to a worker that packs them into a host-visible buffer of
    // the landing's own, from the meshes' own arrays. Each slice's mesh is claimed until the packing ends, so its storage
    // stays while the worker reads it; a mesh whose storage was already released lands nothing and is taken again.
    private void StartPackingLanding(int uploadBytes, SdfGpuWork wake) {
        if ((m_landingPacking is not null) || m_disposed) { return; }
        if (!TryTakeLanding(job: out var job, uploadBytes: uploadBytes)) {
            // A landing with nothing owed keeps no packed buffer: the region holds no word it could land.
            if (m_owed.Count == 0) { while (m_landingFree.TryPop(result: out var free)) { _ = m_landingBuffers.Remove(item: free); free.Dispose(); } }
            return;
        }

        job.PackedSlices = new bool[job.Slices.Count];
        for (var index = 0; (index < job.Slices.Count); index++) {
            var mesh = job.Slices[index].Mesh;

            if (job.Claimed.Contains(item: mesh)) { job.PackedSlices[index] = true; continue; }
            if (!mesh.TryAcquire(cached: false)) { ForgetLanding(mesh: mesh); continue; }
            _ = job.Claimed.Add(item: mesh);
            job.PackedSlices[index] = true;
        }
        try {
            job.Buffer = TakeLandingBuffer(bytes: (((ulong)job.Words) * sizeof(uint)));
        } catch (GpuMemoryRefusedException) {
            // A host pool with no room for the packed words lands nothing now: the words stay owed, taken again once a
            // later frame finds room, as a faulted packer's are.
            ReleasePackClaims(job: job);
            AbandonLandingJob(job: job);
            return;
        }
        job.Packer = m_landingPacker;
        job.Wake = wake;
        m_landingPacking = job;
        job.Pack = Task.Run(action: job.PackWords);
    }
    // Lets go of the claims a packing job held on its meshes, once the worker has finished.
    private static void ReleasePackClaims(LandingJob job) {
        foreach (var mesh in job.Claimed) { mesh.Release(cached: false); }
        job.Claimed.Clear();
    }
    // The landing's packed buffers: the free ones, and every one it holds, which its dispose releases once no job reads
    // them. A buffer comes back free only when the job that read it has completed.
    private readonly Stack<IGpuStorageBuffer> m_landingFree = new();
    private readonly List<IGpuStorageBuffer> m_landingBuffers = [];
    private readonly MeshWords m_landingPacker = new();
    private LandingJob? m_landingPacking;
    // The most free packed buffers the landing keeps between jobs.
    private const int LandingFreeBuffers = 2;

    private IGpuStorageBuffer TakeLandingBuffer(ulong bytes) {
        var size = Math.Max(val1: bytes, val2: (ulong)SdfGpuWork.MostUploadJobBytes);

        while (m_landingFree.TryPop(result: out var free)) {
            if (free.SizeBytes >= size) { return free; }
            _ = m_landingBuffers.Remove(item: free);
            free.Dispose();
        }

        var buffer = m_device.Services.BufferFactory.CreateHostVisible(name: new GpuObjectName(owner: SdfWorldTables.ObjectOwner, part: "mesh-landing"), sizeBytes: size, usage: GpuBufferUsage.Storage);

        m_landingBuffers.Add(item: buffer);
        return buffer;
    }
    // Returns a job's packed buffer: free again once its copy has completed (used, the job answered) or was never
    // recorded; one a submission may still read waits for the device's timeline behind the store's retirement.
    private void ReturnLandingBuffer(IGpuStorageBuffer? buffer, bool used, bool retire = false) {
        if (buffer is null) { return; }
        if (retire || (m_landingFree.Count >= LandingFreeBuffers)) {
            _ = m_landingBuffers.Remove(item: buffer);
            if (retire && used) { m_retired.Hold(item: buffer); } else { buffer.Dispose(); }
            return;
        }
        m_landingFree.Push(item: buffer);
    }
    // Waits the worker packing a job, if any, and releases what the landing holds: called as the store goes, after the
    // last residency's service has waited and handed back every job in flight, so no submission reads a buffer.
    private void DisposeLanding() {
        if (m_landingPacking is { } packing) {
            try { packing.Pack?.Wait(); } catch (AggregateException) { }
            ReleasePackClaims(job: packing);
            m_landingPacking = null;
        }
        foreach (var buffer in m_landingBuffers) { buffer.Dispose(); }
        m_landingBuffers.Clear();
        m_landingFree.Clear();
    }
    /// <summary>Lands each slice of a completed job whose progress is still the mesh's: a mesh all of whose words have
    /// completed leaves the owed set, and every residency whose rows name it is told to stage them.</summary>
    /// <param name="job">The job whose submission completed.</param>
    internal void CompleteLanding(object job) {
        lock (m_gate) {
            var landing = ((LandingJob)job);

            if (m_disposed) { return; }
            ReturnLandingSlot(job: landing);
            foreach (var slice in landing.Slices) {
                if (!m_landing.TryGetValue(key: slice.Mesh, value: out var progress) || !ReferenceEquals(objA: progress, objB: slice.Progress)) {
                    m_landingStale++;
                    continue;
                }
                if (!slice.Recorded || (progress.Pages is not { IsCurrent: true } pages)) {
                    m_landingStale++;
                    ForgetLanding(mesh: slice.Mesh);
                    continue;
                }
                progress.Landed += slice.Count;
                if (progress.Landed < progress.Words) { continue; }
                m_pages[slice.Mesh] = pages;
                _ = m_landing.Remove(key: slice.Mesh);
                if (m_owed.Remove(mesh: slice.Mesh)) {
                    _ = LeaveLanding(mesh: slice.Mesh);
                    Notify(mesh: slice.Mesh);
                }
            }
            ReturnLandingBuffer(buffer: landing.Buffer, used: true);
        }
    }
    /// <summary>Hands back a job a service could not answer: it lands nothing, its meshes' words are taken again from the
    /// start, and its buffer, which a submission may still read, waits for the device's timeline.</summary>
    /// <param name="job">The job.</param>
    internal void AbandonLanding(object job) {
        lock (m_gate) {
            var landing = ((LandingJob)job);

            if (m_disposed) { return; }
            ReturnLandingSlot(job: landing);
            AbandonLandingJob(job: landing);
            ReturnLandingBuffer(buffer: landing.Buffer, retire: true, used: true);
        }
    }
    private void ReturnLandingSlot(LandingJob job) {
        if (job.Slot < 0) { return; }
        m_freeLandingSlots.Push(item: job.Slot);
        job.Slot = -1;
    }
    private void AbandonLandingJob(LandingJob job) {
        foreach (var slice in job.Slices) {
            if (m_landing.TryGetValue(key: slice.Mesh, value: out var progress) && ReferenceEquals(objA: progress, objB: slice.Progress)) {
                ForgetLanding(mesh: slice.Mesh);
            }
        }
    }
    /// <summary>Returns the store a landing job belongs to, which records, completes or abandons it.</summary>
    /// <param name="job">The job a take handed over.</param>
    /// <returns>The store.</returns>
    internal static SdfMeshRegionStore StoreOf(object job) => ((LandingJob)job).Store;
    /// <summary>Returns the bytes a landing job stages.</summary>
    /// <param name="job">The job.</param>
    /// <returns>The bytes.</returns>
    internal static int UploadBytes(object job) => (((LandingJob)job).Words * sizeof(uint));
    // The landing's counts for Describe.
    private string DescribeLandingJobs() => string.Create(provider: CultureInfo.InvariantCulture,
        handler: $"landing-jobs={m_landingJobs} landing-bytes={m_landingBytes}B landing-most={m_landingMostBytes}B landing-stale={m_landingStale} landing-job-most={SdfGpuWork.MostUploadJobBytes}B");
    /// <summary>Describes what keeps a residency's named meshes from standing, for its residency line: the meshes in
    /// landing, those waiting on a restore from the mesh store, and the meshes the arena refuses.</summary>
    /// <returns>A few <c>key=value</c> pairs.</returns>
    internal string DescribePending() {
        lock (m_gate) {
            var restoring = 0;

            foreach (var mesh in m_landingRanks.Keys) { if (!mesh.Resident) { restoring++; } }
            return string.Create(provider: CultureInfo.InvariantCulture, handler: $"{m_landingRanks.Count} ranked meshes ({restoring} restoring from the store, {m_landing.Count} in landing) moved={m_moved} refused={m_arena.Refused.Count}");
        }
    }

    // The placed meshes whose words the region owes. An owed mesh the arena names (a draw of any residency, or ahead of
    // the draws: SdfMeshRegionArena.Names) holds a need on its storage (SdfMesh.Need), so its normals and coarser levels
    // stay in memory, or come back from the store off the frame thread, until its words land; an owed mesh every name
    // let go of lets its need go, and takes it again when one names it (Sync). Landing takes only named meshes.
    // Every mesh that joins or leaves the set is listed in touched, for the landing order to settle (RankLanding).
    private sealed class OwedMeshes(SdfMeshRegionArena arena, List<SdfMesh> touched) {
        private readonly HashSet<SdfMesh> m_set = new(comparer: ReferenceEqualityComparer.Instance);
        // The owed meshes holding a need: those a name holds.
        private readonly HashSet<SdfMesh> m_needed = new(comparer: ReferenceEqualityComparer.Instance);
        private readonly List<SdfMesh> m_changes = [];

        public int Count => m_set.Count;
        public IReadOnlySet<SdfMesh> Set => m_set;

        public bool Contains(SdfMesh mesh) => m_set.Contains(item: mesh);
        public void Add(SdfMesh mesh) {
            if (!m_set.Add(item: mesh)) { return; }
            touched.Add(item: mesh);
            if (arena.Names(mesh: mesh) && m_needed.Add(item: mesh)) { mesh.Need(); }
        }
        public bool Remove(SdfMesh mesh) {
            if (!m_set.Remove(item: mesh)) { return false; }
            touched.Add(item: mesh);
            if (m_needed.Remove(item: mesh)) { mesh.Unneed(); }
            return true;
        }
        // Takes a need for each owed mesh a name holds and lets go of each one no name holds any more, new needs first.
        public void Sync() {
            m_changes.Clear();
            foreach (var mesh in m_set) {
                if (arena.Names(mesh: mesh) && m_needed.Add(item: mesh)) { mesh.Need(); }
            }
            foreach (var mesh in m_needed) {
                if (!m_set.Contains(item: mesh) || !arena.Names(mesh: mesh)) { m_changes.Add(item: mesh); }
            }
            foreach (var mesh in m_changes) {
                _ = m_needed.Remove(item: mesh);
                mesh.Unneed();
            }
            m_changes.Clear();
        }
        public void Clear() {
            foreach (var mesh in m_needed) { mesh.Unneed(); }
            m_needed.Clear();
            touched.AddRange(collection: m_set);
            m_set.Clear();
        }
    }
    // One owed mesh's place in the landing order: its priority and first row as last measured, how many times it was
    // measured and in which pass (RankLanding), whether a measure moved it since the order was last merged, and whether
    // it is still ranked.
    private sealed class LandingRank(SdfMesh mesh, int words) {
        public SdfMesh Mesh { get; } = mesh;
        public int Words { get; } = words;
        public float Priority { get; set; }
        public int Row { get; set; }
        public long Measures { get; set; }
        public long Pass { get; set; }
        public bool Moved { get; set; }
        public bool Staged { get; set; } = true;
    }
    // An order the takes walk (TakeLanding): the ranked order, or the meshes named ahead after it.
    private interface ILandingOrder {
        int Count { get; }
        SdfMesh MeshAt(int index);
    }
    private readonly record struct RankedLandingOrder(List<LandingRank> Ranks) : ILandingOrder {
        public int Count => Ranks.Count;
        public SdfMesh MeshAt(int index) => Ranks[index].Mesh;
    }
    private readonly record struct MeshLandingOrder(List<SdfMesh> Meshes) : ILandingOrder {
        public int Count => Meshes.Count;
        public SdfMesh MeshAt(int index) => Meshes[index];
    }
    private sealed class LandingProgress(int words) {
        public int Landed { get; set; }
        public GpuBufferPages.Lease? Pages { get; set; }
        public int Taken { get; set; }
        public int Words { get; } = words;
    }
    // A slice of one mesh's words: from its word Word, Count words, packed into its job's buffer from word Source, for the
    // placement the mesh held when it was taken.
    private readonly record struct LandingSlice(SdfMesh Mesh, LandingProgress Progress, int Word, int Count, int Source, SdfMeshRegionMesh Placement) {
        public bool Recorded { get; init; }
    }
    // A landing job: its slices and words, the region slot it records into, the buffer a worker packs them into and the
    // packing, the meshes it claims while the worker reads them, and which slices were packed.
    //
    // Invariant: the worker owns the buffer and the scratch it packs through (Packer) from the start of the packing until
    // the packing completes, and reads only the slices and the claimed meshes; the store touches the job again only once
    // Pack has completed (TryTakePackedLanding), and the buffer is free again only once the copy that reads it has
    // completed (CompleteLanding), or was never recorded.
    private sealed class LandingJob(SdfMeshRegionStore store) {
        public SdfMeshRegionStore Store { get; } = store;
        public List<LandingSlice> Slices { get; } = [];
        public int Words { get; set; }
        public int Slot { get; set; } = -1;
        public HashSet<SdfMesh> Claimed { get; } = new(comparer: ReferenceEqualityComparer.Instance);
        public bool[] PackedSlices { get; set; } = [];
        public IGpuStorageBuffer? Buffer { get; set; }
        public Task? Pack { get; set; }
        public MeshWords? Packer { get; set; }
        public SdfGpuWork? Wake { get; set; }

        // Packs each claimed slice's words into the buffer from its source word, then wakes the work so the job is taken.
        public void PackWords() {
            try {
                for (var index = 0; (index < Slices.Count); index++) {
                    if (!PackedSlices[index]) { continue; }

                    var slice = Slices[index];
                    var words = Packer!.Of(mesh: slice.Mesh, placement: slice.Placement);

                    Buffer!.Write<uint>(data: words.Slice(start: slice.Word, length: slice.Count), destinationOffsetBytes: (((ulong)slice.Source) * sizeof(uint)));
                }
            } finally {
                Wake?.Wake();
            }
        }
    }
    // One placed mesh's words from its first vertex word, written into a scratch of its own unless it holds that mesh's
    // already: a mesh's words relative to its first vertex word follow from the mesh alone, so a mesh sliced over several
    // copies is written once. The words are read through a hold (SdfMesh.Hold), the one door to its normals and levels.
    // A mesh the store could not restore writes zeros; its groups bake again (SdfMeshLibrary.Lose). One instance serves
    // one thread at a time: the landing's packer.
    private sealed class MeshWords {
        private static readonly uint[] ZeroWords = new uint[(1 << 16)];

        private SdfMesh? m_mesh;
        private uint[] m_words = [];

        public ReadOnlySpan<uint> Of(SdfMesh mesh, SdfMeshRegionMesh placement) {
            var words = SdfMeshRegion.WordsOf(mesh: mesh);

            if (ReferenceEquals(objA: mesh, objB: m_mesh)) { return m_words.AsSpan(length: words, start: 0); }

            using var held = mesh.Hold();

            if (!held.Readable) { return ((words <= ZeroWords.Length) ? ZeroWords.AsSpan(length: words, start: 0) : new uint[words]); }
            ArrayCapacity.EnsureScratch(ref m_words, words);

            var destination = m_words.AsSpan(length: words, start: 0);

            SdfMeshRegion.WriteMesh(
                destination: destination,
                held: in held,
                placement: new SdfMeshRegionMesh(
                    IndexCount: placement.IndexCount,
                    IndexWord: (placement.IndexWord - placement.VertexWord),
                    MaterialWord: (placement.MaterialWord - placement.VertexWord),
                    VertexWord: 0,
                    SkinWord: ((placement.SkinWord == 0) ? 0 : (placement.SkinWord - placement.VertexWord))
                )
            );
            m_mesh = mesh;

            return destination;
        }
    }
}
