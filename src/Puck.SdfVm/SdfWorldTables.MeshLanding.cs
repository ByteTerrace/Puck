using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The residency's part in landing meshes: its background GPU work (SdfGpuWork) takes the presentation's mesh region's
// landing jobs (SdfMeshRegionStore.Landing.cs) as one of its job sources, so the store's owed words land through
// whichever residency's service has room for them, under that service's byte, host-time and GPU-share rates. The jobs
// are the store's: a job any residency's service took lands a mesh for every residency that names it.
public sealed partial class SdfWorldTables {
    /// <summary>The stages that read the tables' regions and the mesh region: what a region copy hands its buffer
    /// to.</summary>
    internal const GpuStage RegionReaders = SdfMeshRegionStore.Readers;

    // The background GPU work's client that lands the store's owed words. Every call runs on the frame thread; the
    // jobs' words are packed on a worker ahead of the take (SdfMeshRegionStore.TryTakePackedLanding).
    private sealed class MeshLanding(SdfWorldTables tables) : ISdfGpuWorkClient {
        public GpuQueueCategory Category => GpuQueueCategory.Upload;
        public GpuMemoryBytes OwnedBytes => default;

        // A landing is cut by its bytes: the most an upload job stages copies in under the least frame allowance at
        // SdfGpuWork.UploadCopyBytesPerSecond, so a whole allowance never binds it. While work is queued it is cut to
        // the bytes whose copy fits what is left, and waits when not even the least job's does.
        public bool TryTake(SdfWorldTables current, int uploadBytes, SdfGpuWorkAllowance allowance, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job, out double estimatedSeconds) {
            var store = current.m_meshStore;

            if (allowance.Queued) {
                if (!allowance.Admits(seconds: SdfGpuWork.UploadSecondsOf(bytes: SdfGpuWork.UploadChunkBytes))) {
                    job = null;
                    // Owed words ask the service to hold room for the least landing.
                    estimatedSeconds = ((store.OwedCount > 0) ? SdfGpuWork.UploadSecondsOf(bytes: SdfGpuWork.UploadChunkBytes) : 0d);
                    return false;
                }
                uploadBytes = ((int)Math.Max(val1: SdfGpuWork.UploadChunkBytes, val2: Math.Min(val1: (allowance.Seconds * SdfGpuWork.UploadCopyBytesPerSecond), val2: uploadBytes)));
            }
            return store.TryTakePackedLanding(allowance: allowance, estimatedSeconds: out estimatedSeconds, job: out job, uploadBytes: uploadBytes, wake: current.m_gpuWork);
        }
        // While a staged draw waits for its mesh (DrawsAwaitMeshes) the landing fills a hole the frame shows, so it is
        // taken under the owed allowance as the bake's lattice of a hole is.
        public bool OwesAll => tables.DrawsAwaitMeshes;
        public int UploadBytes(object job) => SdfMeshRegionStore.UploadBytes(job: job);
        public void Record(object job, in SdfGpuWorkRecording recording) => SdfMeshRegionStore.StoreOf(job: job).RecordLanding(job: job, recording: in recording);
        public void Complete(object job, SdfGpuWorkReadback readback) {
            readback.Release();
            SdfMeshRegionStore.StoreOf(job: job).CompleteLanding(job: job);
        }
        // A job handed back may have been submitted, so its buffer waits for the device's timeline.
        public void Abandon(object job) => SdfMeshRegionStore.StoreOf(job: job).AbandonLanding(job: job);
        public void Dispose() { }
    }
}
