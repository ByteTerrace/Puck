namespace Puck.Shaders;

/// <summary>What a node resolves its counted buffers against (<see cref="ShaderPipelineResource.Count"/>): the counts of
/// every basis at a frame extent, and a revision that moves whenever a count that is not the extent's own changes, such
/// as a program growing its instances. A package states it for the instances running it
/// (<see cref="IRenderGraphPackageFactory.CounterOf"/>).</summary>
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
// The counts a graph's counted buffers are allocated by. A graph is built with the counts the counter its package passes'
// factories state for this instance resolves at the extent it is built for, and a counter whose revision moves rebuilds
// the installed graph beside it, as a resize does, so a counted buffer is never resized in place. A graph whose packages
// state no counter resolves the extent alone, and a counted buffer scaling with any other basis is refused by name when
// it is built.
public sealed partial class ShaderPipelineRenderNode {
    // The counter the installed graph was allocated by, the counts it resolved and their revision, and whether the graph
    // must be rebuilt because the counter moved since.
    private IShaderPipelineStorageCounter? m_installedCounter;
    private ShaderPipelineStorageCounts m_installedCounts;
    private long m_installedCountRevision;
    // The last revision queued, so a refused recount is retried by the refusal policy, not every frame.
    private long m_requestedCountRevision;
    private bool m_recountPending;

    // The counter a plan's package passes' factories state for this instance, or null when none does.
    private IShaderPipelineStorageCounter? CounterOf(ShaderPipelinePlan plan) {
        foreach (var planned in plan.Passes) {
            if (
                (planned.Package is { } step) &&
                m_packages.TryGetFactory(
                    factory: out var factory,
                    package: step.Package
                ) &&
                (factory.CounterOf(instance: m_name) is { } counter)
            ) {
                return counter;
            }
        }

        return null;
    }
    // The counts a plan built now at an extent is allocated by.
    private ShaderPipelineStorageCounts CountsAt(ShaderPipelinePlan plan, (uint Width, uint Height) extent) => (CounterOf(plan: plan)?.CountsAt(
        height: extent.Height,
        width: extent.Width
    ) ?? new ShaderPipelineStorageCounts(
        Height: extent.Height,
        Width: extent.Width
    ));

    // A changed counter can mean larger scratch or a replaced residency. Its old recorders cannot render current data.
    private bool CountsChanged => (
        (m_installedCounter is { } counter) &&
        (counter.Revision != m_installedCountRevision)
    );

    // Marks the installed graph for a rebuild when its counter has moved since it was allocated.
    private void CheckCounts() {
        if (
            m_ready &&
            (m_pipeline is not null) &&
            CountsChanged &&
            (m_installedCounter!.Revision != m_requestedCountRevision)
        ) {
            m_requestedCountRevision = m_installedCounter.Revision;
            ForgetRefusal();
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
