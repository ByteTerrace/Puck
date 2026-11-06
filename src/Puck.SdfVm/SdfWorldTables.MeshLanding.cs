using System.Globalization;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh region's landing: every placed mesh's words reach the region through the tables' background GPU work
// (SdfGpuWork), in jobs of at most SdfGpuWork.UploadChunkBytes under that service's byte, host-time and GPU-share budget,
// never through a frame's upload. The meshes the latest staged list names and the region still owes are taken in the
// order the views see them largest (MeshDemand), so the scene fills in from the cameras outward; a large mesh lands in
// several jobs. Each job owes its words to the region (GpuRegion.OweSourced), which stages them from the meshes' own
// arrays into the job's slot of the region (MeshLandingSlot) and records the copy into the job's command buffer.
//
// Invariant: a mesh leaves m_meshesOwed, and so may be named by a staged draw record, only once every word of it at its
// current placement has been copied by a completed submission: CompleteLanding, on the fence the service observed, or
// the frame upload that writes a moved mesh together with the records naming it. The landing filter in
// StageMeshRegionCore is the one place a draw is held out while its mesh is owed. A plan that relocates the arena, moves
// a mesh, or trims a placement drops that mesh's progress, so a job in flight for its old words completes into a
// progress no longer kept (LandingProgress identity) and lands nothing; its words were queued before any later job's,
// which rewrites them.
public sealed partial class SdfWorldTables {
    /// <summary>The stages that read the tables' regions, the mesh region included: what a region copy hands its buffer
    /// to.</summary>
    internal const GpuStage RegionReaders = (GpuStage.ComputeShader | GpuStage.VertexShader | GpuStage.FragmentShader);

    // Each owed mesh whose words a job has taken, with how many have been taken and how many have landed.
    private readonly Dictionary<SdfMesh, LandingProgress> m_landing = new(comparer: ReferenceEqualityComparer.Instance);
    // The owed meshes the latest staged list names, those the views see largest first, and the first that may still have
    // words to take.
    private readonly List<SdfMesh> m_landingOrder = [];
    private readonly HashSet<SdfMesh> m_landingOrdered = new(comparer: ReferenceEqualityComparer.Instance);
    private int m_landingCursor;
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
    // box bounds (a skinned body) before any, then the list's order. A placed mesh no draw names stays owed until a list
    // names it again.
    private void OrderLanding(IReadOnlyList<SdfMeshDraw> draws) {
        m_landingOrder.Clear();
        m_landingOrdered.Clear();
        m_landingCursor = 0;
        if (m_meshesOwed.Count == 0) { return; }

        var demand = ((MeshDemand is { Count: > 0 } points) ? points : null);

        m_owedOrder.Clear();
        for (var index = 0; (index < draws.Count); index++) {
            var draw = draws[index];

            if (!m_meshesOwed.Contains(item: draw.Mesh)) { continue; }
            m_owedOrder.Add(item: (((demand is null)
                ? 0f
                : (SdfMeshDemand.BoxOf(center: out var center, draw: in draw, half: out var half) ? demand.Priority(center: center, half: half) : float.PositiveInfinity)), index));
        }
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_owedOrder).Sort(comparison: ByPriority);
        foreach (var (_, index) in m_owedOrder) {
            var mesh = draws[index].Mesh;

            if (m_landingOrdered.Add(item: mesh)) { m_landingOrder.Add(item: mesh); }
        }
    }

    // Drops the progress of meshes whose words a plan moved or forgot: their next words land from the start, at their
    // current placement.
    private void ForgetLanding(SdfMesh mesh) {
        _ = m_landing.Remove(key: mesh);
        m_landingCursor = 0;
    }
    private void ForgetAllLanding() {
        m_landing.Clear();
        m_landingCursor = 0;
    }

    // Takes the next owed words in landing order, up to one chunk, from as many meshes as fit and at most LandingSlices of
    // them, each slice one run of the region's copy, so the copy never merges runs across the words between them; none
    // when every ordered mesh's words are taken.
    private bool TryTakeLanding([System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out LandingJob? job) {
        job = null;
        if (m_meshesOwed.Count == 0) { return false; }

        const int Budget = (SdfGpuWork.UploadChunkBytes / sizeof(uint));
        var taken = 0;

        while ((m_landingCursor < m_landingOrder.Count) && (taken < Budget) && ((job?.Slices.Count ?? 0) < LandingSlices)) {
            var mesh = m_landingOrder[m_landingCursor];

            if (!m_meshesOwed.Contains(item: mesh)) { m_landingCursor++; continue; }
            if (!m_landing.TryGetValue(key: mesh, value: out var progress)) {
                progress = new LandingProgress(words: SdfMeshRegion.WordsOf(mesh: mesh));
                m_landing.Add(key: mesh, value: progress);
            }

            var take = Math.Min(val1: (progress.Words - progress.Taken), val2: (Budget - taken));

            if (take > 0) {
                (job ??= new LandingJob()).Slices.Add(item: new LandingSlice(Mesh: mesh, Progress: progress, Word: progress.Taken, Count: take));
                progress.Taken += take;
                taken += take;
            }
            if (progress.Taken == progress.Words) { m_landingCursor++; }
        }
        if (job is not null) { job.Words = taken; }
        return (job is not null);
    }

    // Owes each slice's words at its mesh's current placement and records their copy into the job's slot of the region.
    private void RecordLanding(LandingJob job, in SdfGpuWorkRecording recording) {
        foreach (var slice in job.Slices) {
            var placement = m_meshArena.Placements[slice.Mesh];

            m_meshRegion.OweSourced(
                length: (((long)slice.Count) * sizeof(uint)),
                offset: ((((long)placement.VertexWord) + slice.Word) * sizeof(uint))
            );
        }
        recording.CopyRegion(readers: RegionReaders, region: m_meshRegion, regionSlot: MeshLandingSlot(backgroundSlot: recording.Slot));
        // A mesh sliced over several jobs is written into the scratch once (MeshWordsOf); the scratch lets go of it once
        // its last slice is staged.
        if (job.Slices[^1] is var last && ((last.Word + last.Count) == last.Progress.Words)) { m_meshWordsMesh = null; }

        var bytes = (((long)job.Words) * sizeof(uint));

        m_meshWordsWritten += job.Words;
        m_landingJobs++;
        m_landingBytes += bytes;
        m_landingMostBytes = Math.Max(val1: m_landingMostBytes, val2: bytes);
    }

    // Lands each slice whose progress is still the mesh's: a mesh all of whose words have completed leaves the owed set,
    // and the next staging names its draws.
    private void CompleteLanding(LandingJob job) {
        foreach (var slice in job.Slices) {
            if (!m_landing.TryGetValue(key: slice.Mesh, value: out var progress) || !ReferenceEquals(objA: progress, objB: slice.Progress)) {
                m_landingStale++;
                continue;
            }
            progress.Landed += slice.Count;
            if (progress.Landed < progress.Words) { continue; }
            _ = m_landing.Remove(key: slice.Mesh);
            if (m_meshesOwed.Remove(item: slice.Mesh)) {
                m_meshStaged = false;
                m_meshLandedSinceStaging = true;
                // The staged list's owed words fall as its meshes land, so a residency whose view is held (no staging
                // until it renders again) reads resident once they have; any render stages the landed draws first.
                if (m_activeOwed.Remove(item: slice.Mesh)) {
                    Volatile.Write(location: ref m_owedWords, value: (m_owedWords - progress.Words));
                }
            }
        }
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
        handler: $"landing-jobs={m_landingJobs} landing-bytes={m_landingBytes}B landing-most={m_landingMostBytes}B landing-stale={m_landingStale} landing-chunk={SdfGpuWork.UploadChunkBytes}B");

    private sealed class LandingProgress(int words) {
        public int Words { get; } = words;
        public int Taken { get; set; }
        public int Landed { get; set; }
    }
    private readonly record struct LandingSlice(SdfMesh Mesh, LandingProgress Progress, int Word, int Count);
    private sealed class LandingJob {
        public List<LandingSlice> Slices { get; } = [];
        public int Words { get; set; }
    }

    // The background GPU work's client that lands the mesh region's owed words; the tables own its state, and every call
    // runs on the frame thread.
    private sealed class MeshLanding(SdfWorldTables tables) : ISdfGpuWorkClient {
        public GpuMemoryBytes OwnedBytes => default;
        public GpuQueueCategory Category => GpuQueueCategory.Upload;

        public bool TryTake(SdfWorldTables current, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job, out double estimatedSeconds) {
            if (current.TryTakeLanding(job: out var landing)) {
                job = landing;
                estimatedSeconds = SdfGpuWork.UploadSecondsOf(bytes: (((long)landing.Words) * sizeof(uint)));
                return true;
            }
            job = null;
            estimatedSeconds = 0d;
            return false;
        }
        public int UploadBytes(object job) => (((LandingJob)job).Words * sizeof(uint));
        public void Record(object job, in SdfGpuWorkRecording recording) => tables.RecordLanding(job: (LandingJob)job, recording: in recording);
        public void Complete(object job, SdfGpuWorkReadback readback) {
            readback.Release();
            tables.CompleteLanding(job: (LandingJob)job);
        }
        public void Abandon(object job) {
            if (!tables.m_disposed) { tables.AbandonLanding(job: (LandingJob)job); }
        }
        public void Dispose() { }
    }
}
