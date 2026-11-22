using System.Globalization;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh region's landing: every placed mesh's words reach the region through the tables' background GPU work
// (SdfGpuWork), in jobs of what that service's upload budget allows (at most SdfGpuWork.MostUploadJobBytes) under its byte,
// host-time and GPU-share rates,
// never through a frame's upload. The meshes the latest staged list names and the region still owes are taken in the
// order the views see them largest (MeshDemand), so the scene fills in from the cameras outward, and after them the
// meshes named ahead of the draws (NameMeshesAhead), which no draw waits on; a large mesh lands in
// several jobs. Each job owes its words to the region (GpuRegion.OweSourced), which stages them from the meshes' own
// arrays into the job's slot of the region (MeshLandingSlot) and records the copy into the job's command buffer.
//
// Invariant: a mesh leaves m_meshesOwed, and so may be named by a staged draw record, only once every word of it at its
// current placement has been copied by a completed submission: CompleteLanding, on the fence the service observed, or
// the frame upload that writes a moved mesh together with the records naming it. StageMeshDraw is the one place a row
// is staged live or absent according to MeshStands. A plan that relocates the arena, moves
// a mesh, or forgets a placement drops that mesh's progress, so a job taken for its old words records none of them, and
// one already in flight completes into a progress no longer kept (LandingProgress identity) and lands nothing; its
// words were queued before any later job's, which rewrites them for the mesh the arena gave them to.
public sealed partial class SdfWorldTables {
    /// <summary>The stages that read the tables' regions, the mesh region included: what a region copy hands its buffer
    /// to.</summary>
    internal const GpuStage RegionReaders = GpuStage.ComputeShader | GpuStage.VertexShader | GpuStage.FragmentShader;

    // Each owed mesh whose words a job has taken, with how many have been taken and how many have landed.
    private readonly Dictionary<SdfMesh, LandingProgress> m_landing = new(comparer: ReferenceEqualityComparer.Instance);
    // The landing order: the owed meshes the latest staged list names (the staged owed meshes), each with its rank, those
    // the views see largest first, and the first that may still have words to take.
    //
    // Invariant: m_landingRanks holds exactly the meshes m_meshesOwed holds that a row of the staged list names, and
    // m_owedWords is the sum of their words. Every change to either side reaches m_landingTouched, through the owed set's
    // one door (OwedMeshes) and the two places a row's mesh changes (StageMeshRegion, Unname); RankLanding settles each
    // touched mesh before any take, and CompleteLanding settles the mesh it lands at once. The ranks themselves may be a
    // few stagings behind the views: a mesh is ranked when it joins, when one of its rows changes, while it is among the
    // first the order takes (LandingHeadRanks), and otherwise in turn, at most LandingSweepRows rows a staging. Order
    // never changes a mesh, only when it lands.
    private readonly Dictionary<SdfMesh, LandingRank> m_landingRanks = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMesh> m_landingTouched = [];
    // The ranks in landing order as of the last merge, any that left since included (LandingRank.Staged false); the
    // meshes in that order, which the takes walk; and the ranks measured since the last merge, in rank order once sorted.
    private readonly List<LandingRank> m_landingRanked = [];
    private readonly List<SdfMesh> m_landingOrder = [];
    private readonly List<LandingRank> m_landingMeasured = [];
    private readonly List<LandingRank> m_landingMerged = [];
    // The ranks in the order they were last measured, each with the measure it was queued at, so the sweep measures the
    // stalest first and passes over an entry a later measure made stale.
    private readonly Queue<(LandingRank Rank, long Measure)> m_landingStalest = new();

    private int m_landingCursor;
    private int m_landingLeft;

    // The meshes the order holds first, measured again every staging, and the most rows the sweep measures a staging.
    private const int LandingHeadRanks = 16;
    private const int LandingSweepRows = 256;

    // The owed meshes named ahead of the draws that the order above does not hold, and the first that may still
    // have words to take: listed again before a take once the set named ahead, or the owed set, changed.
    private readonly List<SdfMesh> m_landingAhead = [];

    private int m_landingAheadCursor;
    private bool m_landingAheadStale = true;
    // Whether a mesh has landed since the draws were last staged: a frame that stands is staged again then
    // (StageLandedMeshes).
    private bool m_meshLandedSinceStaging;
    // The landing jobs submitted and the bytes they carried, the largest one, and those that landed nothing because a
    // plan dropped their progress while they were in flight.
    private long m_landingJobs;
    private long m_landingBytes;
    private long m_landingMostBytes;
    private long m_landingStale;

    // A mesh region slot a background slot stages into: past the upload ring's, each reused only after that background
    // slot's job completed.
    private static int MeshLandingSlot(int backgroundSlot) => (FrameRingSize + backgroundSlot);

    // The most slices one landing job carries: half the runs a region copy keeps apart, leaving the rest to draw records
    // a frame wrote since its upload.
    private const int LandingSlices = (GpuRegion.MaxCopyRuns / 2);

    // Settles the touched meshes into the landing order and measures the ranks it owes a measure: a mesh that joined or
    // one of whose rows changed, and with sweep (a staging) the first the order takes and in turn the stalest, at most
    // LandingSweepRows rows of them. Merges what it measured into the order. Its order is the demand points' largest
    // first (SdfMeshDemand.Priority over the rows naming the mesh), a draw no host box bounds (a skinned body) before any,
    // then the list's order; after it come the owed meshes named ahead (ListLandingAhead). Any other placed mesh no draw
    // names stays owed until a list names it again.
    private void RankLanding(SdfMeshDraws draws, bool sweep) {
        var demand = ((MeshDemand is { Count: > 0 } points) ? points : null);
        var rows = 0;
        var visited = m_landingTouched.Count;

        if (sweep) {
            m_landingCursor = 0;
            m_landingAheadCursor = 0;
        }
        foreach (var mesh in m_landingTouched) { rows += SettleLanding(demand: demand, draws: draws, mesh: mesh); }
        m_landingTouched.Clear();
        // With no demand point every rank is the list's order, which only a row's change moves.
        if (sweep && (demand is not null) && (m_landingRanks.Count > 0)) {
            for (var index = 0; ((index < m_landingRanked.Count) && (index < LandingHeadRanks)); index++) {
                if (m_landingRanked[index] is { Staged: true, Measured: false } head) {
                    rows += MeasureLanding(demand: demand, draws: draws, rank: head);
                    visited++;
                }
            }
            var swept = 0;

            while ((swept < LandingSweepRows) && m_landingStalest.TryPeek(result: out var stalest)) {
                // A queued rank measured in this staging was queued after every entry still ahead of it, so the
                // entries left were all measured in it.
                if ((stalest.Rank.Measures == stalest.Measure) && stalest.Rank is { Staged: true, Measured: true }) { break; }
                _ = m_landingStalest.Dequeue();
                if ((stalest.Rank.Measures != stalest.Measure) || !stalest.Rank.Staged) { continue; }
                swept += MeasureLanding(demand: demand, draws: draws, rank: stalest.Rank);
                visited++;
            }
            rows += swept;
        }
        if ((m_landingMeasured.Count > 0) || (m_landingLeft > m_landingRanks.Count)) { MergeLanding(); }
        if (visited > 0) {
            SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.LandingMeshesVisited, amount: visited);
            SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.LandingRowsOrdered, amount: rows);
        }
    }
    // Brings one touched mesh's place in the landing order to what the owed set and the rows say: a staged owed mesh
    // joins and is measured, one measured already in this staging is not measured again, and one no longer staged or
    // owed leaves. The rows it measured.
    private int SettleLanding(SdfMesh mesh, SdfMeshDraws draws, SdfMeshDemand? demand) {
        var staged = (m_meshesOwed.Contains(mesh: mesh) && (m_rowsByMesh.First(key: mesh) >= 0));

        if (!m_landingRanks.TryGetValue(key: mesh, value: out var rank)) {
            if (!staged) { return 0; }
            rank = new LandingRank(mesh: mesh, words: SdfMeshRegion.WordsOf(mesh: mesh));
            m_landingRanks.Add(key: mesh, value: rank);
            m_landingAheadStale = true;
            Volatile.Write(location: ref m_owedWords, value: (m_owedWords + rank.Words));
        } else if (!staged) {
            _ = LeaveLanding(mesh: mesh);
            return 0;
        }

        return (rank.Measured ? 0 : MeasureLanding(demand: demand, draws: draws, rank: rank));
    }
    // Takes a mesh out of the landing order, its words out of the owed count; false when the order did not hold it.
    private bool LeaveLanding(SdfMesh mesh) {
        if (!m_landingRanks.Remove(key: mesh, value: out var rank)) { return false; }
        rank.Staged = false;
        m_landingLeft++;
        m_landingAheadStale = true;
        Volatile.Write(location: ref m_owedWords, value: (m_owedWords - rank.Words));
        return true;
    }
    // Measures a rank from the rows naming its mesh: the highest priority among them and the first row at it, and queues
    // it to be measured again in turn. The rows it measured.
    private int MeasureLanding(LandingRank rank, SdfMeshDraws draws, SdfMeshDemand? demand) {
        var best = float.NegativeInfinity;
        var first = -1;
        var rows = 0;

        for (var row = m_rowsByMesh.First(key: rank.Mesh); (row >= 0); row = m_rowsByMesh.Next(row: row)) {
            var draw = draws[row];
            var priority = ((demand is null) ? 0f : (SdfMeshDemand.BoxOf(center: out var center, draw: in draw, half: out var half, transforms: m_motionTransforms) ? demand.Priority(center: center, half: half) : float.PositiveInfinity));

            if ((first < 0) || (priority > best) || ((priority == best) && (row < first))) {
                best = priority;
                first = row;
            }
            rows++;
        }
        rank.Priority = best;
        rank.Row = first;
        rank.Measured = true;
        rank.Measures++;
        m_landingMeasured.Add(item: rank);
        // Entries a later measure made stale wait in the queue until the sweep reaches them; past a few per rank the
        // queue is listed again from the ranks themselves.
        if (m_landingStalest.Count > ((4 * m_landingRanks.Count) + LandingSweepRows)) {
            m_landingStalest.Clear();
            foreach (var held in m_landingRanked) {
                if (held is { Staged: true, Measured: false }) { m_landingStalest.Enqueue(item: (held, held.Measures)); }
            }
            foreach (var held in m_landingMeasured) {
                if (held.Staged && !ReferenceEquals(objA: held, objB: rank)) { m_landingStalest.Enqueue(item: (held, held.Measures)); }
            }
        }
        m_landingStalest.Enqueue(item: (rank, rank.Measures));
        return rows;
    }
    // Merges the ranks measured since the last merge, sorted, into the order the rest keep, leaving out those that left.
    private void MergeLanding() {
        var measured = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_landingMeasured);
        var order = new SdfMeshPriorityOrder();
        var next = 0;

        measured.Sort(comparison: static (left, right) => new SdfMeshPriorityOrder().Compare(x: (left.Priority, left.Row), y: (right.Priority, right.Row)));
        m_landingMerged.Clear();
        foreach (var rank in m_landingRanked) {
            if (!rank.Staged || rank.Measured) { continue; }
            for (; ((next < measured.Length) && (order.Compare(x: (measured[next].Priority, measured[next].Row), y: (rank.Priority, rank.Row)) < 0)); next++) {
                if (measured[next].Staged) { m_landingMerged.Add(item: measured[next]); }
            }
            m_landingMerged.Add(item: rank);
        }
        for (; (next < measured.Length); next++) {
            if (measured[next].Staged) { m_landingMerged.Add(item: measured[next]); }
        }
        foreach (var rank in measured) { rank.Measured = false; }
        m_landingMeasured.Clear();
        m_landingRanked.Clear();
        m_landingRanked.AddRange(collection: m_landingMerged);
        m_landingMerged.Clear();
        m_landingOrder.Clear();
        foreach (var rank in m_landingRanked) { m_landingOrder.Add(item: rank.Mesh); }
        m_landingLeft = 0;
        m_landingCursor = 0;
        SdfMeshHistory.ProcessWork.Count(kind: SdfMeshHistory.LandingOrders);
    }
    // Notes a mesh whose place in the landing order may have changed: it was owed or forgotten, or a row named it or
    // stopped naming it. RankLanding settles it before the next take.
    private void TouchLanding(SdfMesh? mesh) {
        if (mesh is not null) { m_landingTouched.Add(item: mesh); }
    }
    // Lists the owed meshes named ahead after the landing order, when the set named ahead or the order changed since:
    // a draw with a hole in the frame lands before a mesh whose draws still read the one it replaces.
    private void ListLandingAhead() {
        if (!m_landingAheadStale) { return; }
        m_landingAheadStale = false;
        m_landingAhead.Clear();
        m_landingAheadCursor = 0;
        foreach (var mesh in m_meshArena.Ahead) {
            if (m_meshesOwed.Contains(mesh: mesh) && !m_landingRanks.ContainsKey(key: mesh)) { m_landingAhead.Add(item: mesh); }
        }
    }
    // Drops the progress of meshes whose words a plan moved or forgot: their next words land from the start, at their
    // current placement.
    private void ForgetLanding(SdfMesh mesh) {
        _ = m_landing.Remove(key: mesh);
        m_landingCursor = 0;
        m_landingAheadCursor = 0;
    }
    private void ForgetAllLanding() {
        m_landing.Clear();
        m_landingCursor = 0;
        m_landingAheadCursor = 0;
    }
    // Takes the next owed words in landing order, up to one chunk, from as many meshes as fit and at most LandingSlices of
    // them, each slice one run of the region's copy, so the copy never merges runs across the words between them; none
    // when every ordered mesh's words are taken.
    private bool TryTakeLanding(int uploadBytes, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out LandingJob? job) {
        job = null;
        if ((m_landingTouched.Count > 0) || (m_landingMeasured.Count > 0)) { RankLanding(draws: m_currentDrawRows, sweep: false); }
        if (m_meshesOwed.Count == 0) { return false; }
        ListLandingAhead();

        var budget = (uploadBytes / sizeof(uint));
        var taken = 0;

        TakeLanding(budget: budget, cursor: ref m_landingCursor, job: ref job, order: m_landingOrder, taken: ref taken);
        TakeLanding(budget: budget, cursor: ref m_landingAheadCursor, job: ref job, order: m_landingAhead, taken: ref taken);
        if (job is not null) { job.Words = taken; }
        return (job is not null);
    }
    // Takes owed words from one order, from its cursor, into a job that may hold slices already.
    private void TakeLanding(List<SdfMesh> order, ref int cursor, int budget, ref int taken, ref LandingJob? job) {
        // A mesh whose storage is still coming back from the store (SdfMesh.Resident) is passed over, and the cursor stays
        // at the first such mesh so a later take finds it restored.
        var waiting = -1;

        while ((cursor < order.Count) && (taken < budget) && ((job?.Slices.Count ?? 0) < LandingSlices)) {
            var mesh = order[cursor];

            if (!m_meshesOwed.Contains(mesh: mesh) || !m_meshArena.Names(mesh: mesh)) { cursor++; continue; }
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
                m_landing.Remove(key: mesh);
                continue;
            }
            if (progress.Pages is null) {
                var placement = m_meshArena.Placements[mesh];

                progress.Pages = m_meshRegion.TryLease((((long)placement.VertexWord) * sizeof(uint)), (((long)progress.Words) * sizeof(uint)));
                if (progress.Pages is null) {
                    if (waiting < 0) { waiting = cursor; }
                    cursor++;
                    continue;
                }
            }
            var take = Math.Min(val1: (progress.Words - progress.Taken), val2: (budget - taken));

            if (take > 0) {
                (job ??= new LandingJob()).Slices.Add(item: new LandingSlice(Mesh: mesh, Progress: progress, Word: progress.Taken, Count: take));
                progress.Taken += take;
                taken += take;
            }
            if (progress.Taken == progress.Words) { cursor++; }
        }
        if (waiting >= 0) { cursor = Math.Min(val1: cursor, val2: waiting); }
    }
    // Owes each slice's words at its mesh's current placement and records their copy into the job's slot of the region.
    // A slice whose progress a plan dropped since the take owes nothing: its mesh was moved or forgotten, and the words
    // it was taken for may be another mesh's.
    private void RecordLanding(LandingJob job, in SdfGpuWorkRecording recording) {
        if (!ReferenceEquals(objA: recording.Queue, objB: m_deviceContext.Services.QueueSubmitter)) {
            throw new InvalidOperationException(message: "Mesh landing must submit on the tables' frame queue; row reuse requires its order.");
        }
        for (var index = 0; (index < job.Slices.Count); index++) {
            var slice = job.Slices[index];

            if (!m_landing.TryGetValue(key: slice.Mesh, value: out var progress) || !ReferenceEquals(objA: progress, objB: slice.Progress) ||
                !m_meshArena.Placements.TryGetValue(key: slice.Mesh, value: out var placement) || (progress.Pages is not { IsCurrent: true })) { continue; }
            job.Slices[index] = slice with { Recorded = true };

            m_meshRegion.OweSourced(
                length: (((long)slice.Count) * sizeof(uint)),
                offset: ((((long)placement.VertexWord) + slice.Word) * sizeof(uint))
            );
        }
        recording.CopyRegion(readers: RegionReaders, region: m_meshRegion, regionSlot: MeshLandingSlot(backgroundSlot: recording.Slot));
        // A mesh sliced over several jobs is written into the scratch once (MeshWordsOf); the scratch lets go of it once
        // its last slice is staged.
        if ((job.Slices[^1] is var last) && ((last.Word + last.Count) == last.Progress.Words)) { m_meshWordsMesh = null; }

        var bytes = (((long)job.Words) * sizeof(uint));

        m_meshWordsWritten += job.Words;
        m_landingJobs++;
        m_landingBytes += bytes;
        m_landingMostBytes = Math.Max(val1: m_landingMostBytes, val2: bytes);
    }
    // Lands each slice whose progress is still the mesh's: a mesh all of whose words have completed leaves the owed set,
    // and the next staging names its draws. A mesh the staged list does not name (one named ahead) held no draw out,
    // so its landing stages nothing again.
    private void CompleteLanding(LandingJob job) {
        foreach (var slice in job.Slices) {
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
            m_meshPages[slice.Mesh] = pages;
            _ = m_landing.Remove(key: slice.Mesh);
            // The staged list's owed words fall as its meshes land, so a residency whose view is held (no staging
            // until it renders again) reads resident once they have; any render stages the landed draws first.
            if (m_meshesOwed.Remove(mesh: slice.Mesh) && LeaveLanding(mesh: slice.Mesh)) {
                m_meshLandedSinceStaging = true;
                for (var row = m_rowsByMesh.First(key: slice.Mesh); (row >= 0); row = m_rowsByMesh.Next(row: row)) {
                    m_landedRows.Add(length: 1, start: row);
                }
            }
        }
        AccountMeshOwed();
    }
    // A job handed back unanswered lands nothing: its meshes' words are taken again from the start.
    private void AbandonLanding(LandingJob job) {
        foreach (var slice in job.Slices) {
            if (m_landing.TryGetValue(key: slice.Mesh, value: out var progress) && ReferenceEquals(objA: progress, objB: slice.Progress)) {
                ForgetLanding(mesh: slice.Mesh);
            }
        }
    }
    // The landing's counts for DescribeLanding.
    private string DescribeLandingJobs() => string.Create(provider: CultureInfo.InvariantCulture,
        handler: $"landing-jobs={m_landingJobs} landing-bytes={m_landingBytes}B landing-most={m_landingMostBytes}B landing-stale={m_landingStale} landing-job-most={SdfGpuWork.MostUploadJobBytes}B");

    // The placed meshes whose words the region owes. An owed mesh the arena names (a draw of the latest list, or ahead
    // of the draws: SdfMeshRegionArena.Names) holds a need on its storage
    // (SdfMesh.Need), so its normals and coarser levels stay in memory, or come back from the store off the frame thread,
    // until its words land; an owed mesh the list stopped naming lets its need go, and takes it again when a list names it
    // (Sync). Landing takes only named meshes, and any other read of a mesh's words goes through a hold (MeshWordsOf).
    // Every mesh that joins or leaves the set is listed in touched, for the landing order to settle (RankLanding).
    private sealed class OwedMeshes(SdfMeshRegionArena arena, List<SdfMesh> touched) {
        private HashSet<SdfMesh> m_set = new(comparer: ReferenceEqualityComparer.Instance);
        private HashSet<SdfMesh> m_spare = new(comparer: ReferenceEqualityComparer.Instance);
        // The owed meshes holding a need: those the latest list names.
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
        public void UnionWith(IEnumerable<SdfMesh> meshes) { foreach (var mesh in meshes) { Add(mesh: mesh); } }
        // Owes exactly these meshes, taking each new need before letting any old one go, so a mesh owed and named both
        // before and after keeps its storage.
        public void ReplaceWith(IEnumerable<SdfMesh> meshes) {
            m_spare.Clear();
            foreach (var mesh in meshes) {
                if (m_spare.Add(item: mesh) && !m_set.Contains(item: mesh)) { touched.Add(item: mesh); }
            }
            foreach (var mesh in m_set) {
                if (!m_spare.Contains(item: mesh)) { touched.Add(item: mesh); }
            }
            (m_set, m_spare) = (m_spare, m_set);
            m_spare.Clear();
            Sync();
        }
        // Takes a need for each owed mesh the latest list names and lets go of each one it no longer names, new needs
        // first.
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
        public void RemoveWhere(Predicate<SdfMesh> match) {
            m_spare.Clear();
            foreach (var mesh in m_set) { if (match(obj: mesh)) { _ = m_spare.Add(item: mesh); } }
            foreach (var mesh in m_spare) { _ = Remove(mesh: mesh); }
            m_spare.Clear();
        }
        public void Clear() {
            foreach (var mesh in m_needed) { mesh.Unneed(); }
            m_needed.Clear();
            touched.AddRange(collection: m_set);
            m_set.Clear();
        }
    }
    // One staged owed mesh's place in the landing order: its priority and first row as last measured, how many times it
    // was measured, whether it was measured since the order was last merged, and whether it is still staged and owed.
    private sealed class LandingRank(SdfMesh mesh, int words) {
        public SdfMesh Mesh { get; } = mesh;
        public int Words { get; } = words;
        public float Priority { get; set; }
        public int Row { get; set; }
        public long Measures { get; set; }
        public bool Measured { get; set; }
        public bool Staged { get; set; } = true;
    }
    private sealed class LandingProgress(int words) {
        public int Landed { get; set; }
        public GpuBufferPages.Lease? Pages { get; set; }
        public int Taken { get; set; }
        public int Words { get; } = words;
    }
    private readonly record struct LandingSlice(SdfMesh Mesh, LandingProgress Progress, int Word, int Count) {
        public bool Recorded { get; init; }
    }
    private sealed class LandingJob {
        public List<LandingSlice> Slices { get; } = [];
        public int Words { get; set; }
    }
    // The background GPU work's client that lands the mesh region's owed words; the tables own its state, and every call
    // runs on the frame thread.
    private sealed class MeshLanding(SdfWorldTables tables) : ISdfGpuWorkClient {
        public GpuQueueCategory Category => GpuQueueCategory.Upload;
        public GpuMemoryBytes OwnedBytes => default;

        // A landing is cut by its bytes: the most an upload job stages copies in under the least frame allowance at
        // SdfGpuWork.UploadCopyBytesPerSecond, so a whole allowance never binds it. While work is queued it is cut to
        // the bytes whose copy fits what is left, and waits when not even the least job's does.
        public bool TryTake(SdfWorldTables current, int uploadBytes, SdfGpuWorkAllowance allowance, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job, out double estimatedSeconds) {
            if (allowance.Queued) {
                if (!allowance.Admits(seconds: SdfGpuWork.UploadSecondsOf(bytes: SdfGpuWork.UploadChunkBytes))) {
                    job = null;
                    // Owed words ask the service to hold room for the least landing.
                    estimatedSeconds = ((current.m_meshesOwed.Count > 0) ? SdfGpuWork.UploadSecondsOf(bytes: SdfGpuWork.UploadChunkBytes) : 0d);
                    return false;
                }
                uploadBytes = ((int)Math.Max(val1: SdfGpuWork.UploadChunkBytes, val2: Math.Min(val1: (allowance.Seconds * SdfGpuWork.UploadCopyBytesPerSecond), val2: uploadBytes)));
            }
            if (current.TryTakeLanding(job: out var landing, uploadBytes: uploadBytes)) {
                job = landing;
                estimatedSeconds = SdfGpuWork.UploadSecondsOf(bytes: (((long)landing.Words) * sizeof(uint)));
                return true;
            }
            job = null;
            estimatedSeconds = 0d;
            return false;
        }
        // While a staged draw waits for its mesh (DrawsAwaitMeshes) the landing fills a hole the frame shows, so it is
        // taken under the owed allowance as the bake's lattice of a hole is; landing orders no hole's mesh first, so it
        // lands all it owes then.
        public bool OwesAll => tables.DrawsAwaitMeshes;
        public int UploadBytes(object job) => (((LandingJob)job).Words * sizeof(uint));
        public void Record(object job, in SdfGpuWorkRecording recording) => tables.RecordLanding(job: ((LandingJob)job), recording: in recording);
        public void Complete(object job, SdfGpuWorkReadback readback) {
            readback.Release();
            tables.CompleteLanding(job: ((LandingJob)job));
        }
        public void Abandon(object job) {
            if (!tables.m_disposed) { tables.AbandonLanding(job: ((LandingJob)job)); }
        }
        public void Dispose() { }
    }
}
