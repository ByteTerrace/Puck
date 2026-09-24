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
public sealed class GpuPipelineCacheWork : IWorkCounterSource {
    private static readonly WorkKind[] Kinds = [GpuWork.PipelinesCreated, GpuWork.PipelineCacheHits, GpuWork.PipelineCacheMisses, GpuWork.PipelineCachePruned];

    private readonly WorkCounterSet m_counts;

    /// <summary>Initializes a new instance of the <see cref="GpuPipelineCacheWork"/> class.</summary>
    /// <param name="backend">The backend's name, as a report labels it (<c>vulkan</c>, <c>directx</c>).</param>
    /// <exception cref="ArgumentException"><paramref name="backend"/> is empty, or the composed name
    /// <c>pipeline-cache.&lt;backend&gt;</c> is not a dotted work name.</exception>
    public GpuPipelineCacheWork(string backend) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: backend);

        Backend = backend;
        m_counts = new WorkCounterSet(
            kinds: Kinds,
            name: $"pipeline-cache.{backend}"
        );
    }

    /// <summary>Gets the backend's name.</summary>
    public string Backend { get; }
    /// <summary>Gets the name a counters report heads this source's section with, <c>pipeline-cache.&lt;backend&gt;</c>.</summary>
    public string Name => m_counts.Name;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => Kinds;

    /// <summary>Counts one created pipeline.</summary>
    /// <param name="cacheHit">Whether the driver reported answering the creation from its pipeline cache; a creation
    /// the driver reports nothing about counts as a miss.</param>
    public void Count(bool cacheHit) {
        m_counts.Count(kind: GpuWork.PipelinesCreated);
        m_counts.Count(kind: (cacheHit
            ? GpuWork.PipelineCacheHits
            : GpuWork.PipelineCacheMisses
        ));
    }
    /// <summary>Counts one cache file deleted beyond <see cref="GpuPipelineCacheFile.RetainedFiles"/> when a device's
    /// cache file was opened.</summary>
    public void CountPruned() =>
        m_counts.Count(kind: GpuWork.PipelineCachePruned);
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) =>
        m_counts.TryRead(
            kind: kind,
            value: out value
        );
}
