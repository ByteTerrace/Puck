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
    // The owed meshes the latest staged list names, those the views see largest first, and the first that may still have
    // words to take.
    private readonly List<SdfMesh> m_landingOrder = [];
    private readonly HashSet<SdfMesh> m_landingOrdered = new(comparer: ReferenceEqualityComparer.Instance);

    private int m_landingCursor;

    // The owed meshes named ahead of the draws that the order above does not hold, and the first that may still
    // have words to take: listed again whenever the set named ahead changes, whether or not a draw list is staged.
    private readonly List<SdfMesh> m_landingAhead = [];

    private int m_landingAheadCursor;
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

    // Orders the owed meshes the draw list names for landing: those the demand points see largest first, a draw no host
    // box bounds (a skinned body) before any, then the list's order, and after them the owed meshes named ahead
    // (OrderLandingAhead). Any other placed mesh no draw names stays owed until a list names it again.
    private void OrderLanding(SdfMeshDraws draws) {
        m_landingOrder.Clear();
        m_landingOrdered.Clear();
        m_landingCursor = 0;
        m_landingAhead.Clear();
        m_landingAheadCursor = 0;
        if (m_meshesOwed.Count == 0) { return; }

        var demand = ((MeshDemand is { Count: > 0 } points) ? points : null);

        m_owedOrder.Clear();
        foreach (var mesh in m_meshesOwed.Set) {
            for (var index = m_rowsByMesh.First(key: mesh); (index >= 0); index = m_rowsByMesh.Next(row: index)) {
                var draw = draws[index];

                m_owedOrder.Add(item: (((demand is null) ? 0f : (SdfMeshDemand.BoxOf(center: out var center, draw: in draw, half: out var half) ? demand.Priority(center: center, half: half) : float.PositiveInfinity)), index));
            }
        }
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_owedOrder).Sort(comparison: ByPriority);
        foreach (var (_, index) in m_owedOrder) {
            var mesh = draws[index].Mesh;

            if (m_landingOrdered.Add(item: mesh)) { m_landingOrder.Add(item: mesh); }
        }
        OrderLandingAhead();
    }
    // Lists the owed meshes named ahead after the landing order: a draw with a hole in the frame lands before a mesh
    // whose draws still read the one it replaces.
    private void OrderLandingAhead() {
        m_landingAhead.Clear();
        m_landingAheadCursor = 0;
        foreach (var mesh in m_meshArena.Ahead) {
            if (m_meshesOwed.Contains(mesh: mesh) && !m_landingOrdered.Contains(item: mesh)) { m_landingAhead.Add(item: mesh); }
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
        if (m_meshesOwed.Count == 0) { return false; }

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
        foreach (var slice in job.Slices) {
            if (!m_landing.TryGetValue(key: slice.Mesh, value: out var progress) || !ReferenceEquals(objA: progress, objB: slice.Progress) ||
                !m_meshArena.Placements.TryGetValue(key: slice.Mesh, value: out var placement)) { continue; }

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
            progress.Landed += slice.Count;
            if (progress.Landed < progress.Words) { continue; }
            _ = m_landing.Remove(key: slice.Mesh);
            // The staged list's owed words fall as its meshes land, so a residency whose view is held (no staging
            // until it renders again) reads resident once they have; any render stages the landed draws first.
            if (m_meshesOwed.Remove(mesh: slice.Mesh) && m_activeOwed.Remove(item: slice.Mesh)) {
                m_meshLandedSinceStaging = true;
                for (var row = m_rowsByMesh.First(key: slice.Mesh); (row >= 0); row = m_rowsByMesh.Next(row: row)) {
                    m_landedRows.Add(length: 1, start: row);
                }
                Volatile.Write(location: ref m_owedWords, value: (m_owedWords - progress.Words));
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
    private sealed class OwedMeshes(SdfMeshRegionArena arena) {
        private HashSet<SdfMesh> m_set = new(comparer: ReferenceEqualityComparer.Instance);
        private HashSet<SdfMesh> m_spare = new(comparer: ReferenceEqualityComparer.Instance);
        // The owed meshes holding a need: those the latest list names.
        private readonly HashSet<SdfMesh> m_needed = new(comparer: ReferenceEqualityComparer.Instance);
        private readonly List<SdfMesh> m_changes = [];

        public int Count => m_set.Count;
        public IReadOnlySet<SdfMesh> Set => m_set;

        public bool Contains(SdfMesh mesh) => m_set.Contains(item: mesh);
        public void Add(SdfMesh mesh) {
            if (m_set.Add(item: mesh) && arena.Names(mesh: mesh) && m_needed.Add(item: mesh)) { mesh.Need(); }
        }
        public bool Remove(SdfMesh mesh) {
            if (!m_set.Remove(item: mesh)) { return false; }
            if (m_needed.Remove(item: mesh)) { mesh.Unneed(); }
            return true;
        }
        public void UnionWith(IEnumerable<SdfMesh> meshes) { foreach (var mesh in meshes) { Add(mesh: mesh); } }
        // Owes exactly these meshes, taking each new need before letting any old one go, so a mesh owed and named both
        // before and after keeps its storage.
        public void ReplaceWith(IEnumerable<SdfMesh> meshes) {
            m_spare.Clear();
            foreach (var mesh in meshes) { _ = m_spare.Add(item: mesh); }
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
            m_set.Clear();
        }
    }
    private sealed class LandingProgress(int words) {
        public int Landed { get; set; }
        public int Taken { get; set; }
        public int Words { get; } = words;
    }
    private readonly record struct LandingSlice(SdfMesh Mesh, LandingProgress Progress, int Word, int Count);
    private sealed class LandingJob {
        public List<LandingSlice> Slices { get; } = [];
        public int Words { get; set; }
    }
    // The background GPU work's client that lands the mesh region's owed words; the tables own its state, and every call
    // runs on the frame thread.
    private sealed class MeshLanding(SdfWorldTables tables) : ISdfGpuWorkClient {
        public GpuQueueCategory Category => GpuQueueCategory.Upload;
        public GpuMemoryBytes OwnedBytes => default;

        public bool TryTake(SdfWorldTables current, int uploadBytes, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job, out double estimatedSeconds) {
            if (current.TryTakeLanding(job: out var landing, uploadBytes: uploadBytes)) {
                job = landing;
                estimatedSeconds = SdfGpuWork.UploadSecondsOf(bytes: (((long)landing.Words) * sizeof(uint)));
                return true;
            }
            job = null;
            estimatedSeconds = 0d;
            return false;
        }
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
