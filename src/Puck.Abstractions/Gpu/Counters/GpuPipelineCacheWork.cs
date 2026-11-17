using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// One backend's pipeline creations and how its persistent pipeline cache answered them, over the process's life:
/// <see cref="GpuWork.PipelinesCreated"/>, <see cref="GpuWork.PipelineCacheHits"/> and
/// <see cref="GpuWork.PipelineCacheMisses"/>, plus the least recently written cache files it deleted beyond
/// <see cref="GpuPipelineCacheFile.RetainedFiles"/>, <see cref="GpuWork.PipelineCachePruned"/>. Every created pipeline is exactly one hit or one miss. The counts
/// survive device loss, because the backend's recreated devices count into the same instance. Pipelines are created on
/// build threads as well as the frame thread, so every count is written interlocked.
/// </summary>
public sealed class GpuPipelineCacheWork : WorkLedger<WorkCounterSet> {
    private static readonly WorkKind[] Kinds = [GpuWork.PipelinesCreated, GpuWork.PipelineCacheHits, GpuWork.PipelineCacheMisses, GpuWork.PipelineCachePruned];

    /// <summary>Initializes a new instance of the <see cref="GpuPipelineCacheWork"/> class.</summary>
    /// <param name="backend">The backend's name, as a report labels it (<c>vulkan</c>, <c>directx</c>).</param>
    /// <exception cref="ArgumentException"><paramref name="backend"/> is empty, or the composed name
    /// <c>pipeline-cache.&lt;backend&gt;</c> is not a dotted work name.</exception>
    public GpuPipelineCacheWork(string backend) : base(counters: new(
        kinds: Kinds,
        name: $"pipeline-cache.{RequireBackend(backend: backend)}"
    )) {
        Backend = backend;
    }

    /// <summary>Gets the backend's name.</summary>
    public string Backend { get; }

    /// <summary>Counts one created pipeline.</summary>
    /// <param name="cacheHit">Whether the driver reported answering the creation from its pipeline cache; a creation
    /// the driver reports nothing about counts as a miss.</param>
    public void Count(bool cacheHit) {
        Counters.Count(kind: GpuWork.PipelinesCreated);
        Counters.Count(kind: (cacheHit
            ? GpuWork.PipelineCacheHits
            : GpuWork.PipelineCacheMisses
        ));
    }
    /// <summary>Counts one cache file deleted beyond <see cref="GpuPipelineCacheFile.RetainedFiles"/> when a device's
    /// cache file was opened.</summary>
    public void CountPruned() =>
        Counters.Count(kind: GpuWork.PipelineCachePruned);

    private static string RequireBackend(string backend) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: backend);

        return backend;
    }
}
