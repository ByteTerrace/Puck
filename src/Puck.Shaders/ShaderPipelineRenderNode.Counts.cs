namespace Puck.Shaders;

/// <summary>What a node resolves its counted buffers against (<see cref="ShaderPipelineResource.Count"/>): the counts of
/// every basis at a frame extent, and a revision that moves whenever a count that is not the extent's own changes, such
/// as a program growing its instances.</summary>
public interface IShaderPipelineStorageCounter {
    /// <summary>Gets the revision of the counts, which moves whenever <see cref="CountsAt"/> would return another value
    /// at an unchanged extent.</summary>
    long Revision { get; }

    /// <summary>Returns the counts at a frame extent.</summary>
    /// <param name="width">The frame width, in pixels.</param>
    /// <param name="height">The frame height, in pixels.</param>
    /// <returns>The counts, whose <see cref="ShaderPipelineStorageCounts.Width"/> and
    /// <see cref="ShaderPipelineStorageCounts.Height"/> are the extent.</returns>
    ShaderPipelineStorageCounts CountsAt(uint width, uint height);
}
// The counts a graph's counted buffers are allocated by. A graph is built with the counts its counter resolves at the
// extent it is built for, and a counter whose revision moves rebuilds the installed graph beside it, as a resize does, so
// a counted buffer is never resized in place. A node given no counter resolves the extent alone, and a counted buffer
// scaling with any other basis is refused by name when it is built.
public sealed partial class ShaderPipelineRenderNode {
    private IShaderPipelineStorageCounter? m_counter;
    // The counts the installed graph was allocated with and the counter revision they were resolved at, and whether the
    // graph must be rebuilt because the counter moved since.
    private ShaderPipelineStorageCounts m_installedCounts;
    private long m_installedCountRevision;
    private bool m_recountPending;

    /// <summary>Gets or sets what the node resolves its graphs' counted buffers against, or <see langword="null"/> for
    /// the extent alone. Setting another counter, or a counter whose <see cref="IShaderPipelineStorageCounter.Revision"/>
    /// moves, rebuilds the installed graph beside it, as a resize does.</summary>
    /// <exception cref="ObjectDisposedException">The node is disposed.</exception>
    public IShaderPipelineStorageCounter? StorageCounter {
        get => m_counter;
        set {
            ObjectDisposedException.ThrowIf(
                condition: m_disposed,
                instance: this
            );

            if (ReferenceEquals(
                objA: m_counter,
                objB: value
            )) {
                return;
            }

            m_counter = value;
            m_recountPending = (m_ready && (m_pipeline is not null));
        }
    }

    // The counts a graph built now at an extent is allocated by.
    private ShaderPipelineStorageCounts CountsAt((uint Width, uint Height) extent) => (m_counter?.CountsAt(
        height: extent.Height,
        width: extent.Width
    ) ?? new ShaderPipelineStorageCounts(
        Height: extent.Height,
        Width: extent.Width
    ));
    // Marks the installed graph for a rebuild when its counter has moved since it was allocated.
    private void CheckCounts() {
        if (
            m_ready &&
            (m_pipeline is not null) &&
            (m_counter is { } counter) &&
            (counter.Revision != m_installedCountRevision)
        ) {
            m_recountPending = true;
        }
    }
    // The instances a storage is allocated as: one for a transient storage, which every frame slot shares, and one per
    // frame slot for every other.
    private static int InstancesOf(ShaderPipelinePlannedStorage storage, uint inFlight) => (storage.Declaration.Transient
        ? 1
        : ((int)inFlight));
    // The instance a slot's index reaches in a storage: its own, or the one instance of a storage that has one.
    private static int InstanceAt(RuntimeResource resource, int index) => ((resource.Count == 1)
        ? 0
        : index);
}
