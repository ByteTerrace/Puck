namespace Puck.Shaders;

/// <summary>What a node resolves its counted buffers against (<see cref="ShaderPipelineResource.Count"/>): the counts of
/// every basis at a frame extent, and a revision that moves whenever a count that is not the extent's own changes, such
/// as a program growing its instances, or retained package storage must be replaced even at the same size.
/// A package states it for the instances running it
/// (<see cref="IRenderGraphPackageFactory.CounterOf"/>).</summary>
public interface IShaderPipelineStorageCounter {
    /// <summary>Gets the storage revision, which moves whenever <see cref="CountsAt"/> would return another value
    /// at an unchanged extent or the installed recorders must bind replacement package storage.</summary>
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
    private ShaderPipelineStorageCounts CountsAt(ShaderPipelinePlan plan, (uint Width, uint Height) extent) {
        var render = (RenderExtentOf(plan: plan)?.CeilingAt(height: extent.Height, width: extent.Width) ?? extent);

        // Buffer exports have no output pixel grid: their requested extent is only a scheduler placeholder.
        // Any exported image retains the output bound; an all-buffer package owns its positive allocation ceiling.
        var imageOutput = false;
        foreach (var output in plan.Outputs) {
            if (plan.FindResource(name: output)!.Declaration.Kind != ShaderPipelineResourceKind.Buffer) {
                imageOutput = true;
                break;
            }
        }
        ValidateRenderExtent(ceiling: imageOutput ? extent : render, render: render);
        var counts = (CounterOf(plan: plan)?.CountsAt(height: render.Height, width: render.Width) ?? new ShaderPipelineStorageCounts(Height: extent.Height, Width: extent.Width));

        return counts with {
            Width = extent.Width,
            Height = extent.Height,
            RenderWidth = ((render.Width == extent.Width) ? 0u : render.Width),
            RenderHeight = ((render.Height == extent.Height) ? 0u : render.Height),
        };
    }

    // A changed counter can mean larger scratch or a replaced residency. Its old recorders cannot render current data.
    private bool CountsChanged => (
        ((m_installedCounter is { } counter) && (counter.Revision != m_installedCountRevision)) ||
        ((m_installedRenderExtent is { } extent) && (extent.Revision != m_installedRenderRevision))
    );

    // Marks the installed graph for a rebuild when its counter has moved since it was allocated.
    private void CheckCounts() {
        if (
            m_ready &&
            (m_pipeline is not null) &&
            CountsChanged &&
            (((m_installedCounter?.Revision ?? 0L) != m_requestedCountRevision) ||
             ((m_installedRenderExtent?.Revision ?? 0L) != m_requestedRenderRevision))
        ) {
            m_requestedCountRevision = (m_installedCounter?.Revision ?? 0L);
            m_requestedRenderRevision = (m_installedRenderExtent?.Revision ?? 0L);
            ForgetRefusal();
            m_recountPending = true;
        }
    }
    // A transient or retained intermediate has one queue-ordered allocation shared by every frame slot. Other owned
    // storages have one allocation per frame slot.
    private static int InstancesOf(ShaderPipelinePlannedStorage storage, uint inFlight) => ((storage.Declaration.Transient || storage.Declaration.Retained)
        ? 1
        : ((int)inFlight));
    // The instance a slot's index reaches in a storage: its own, or the one instance of a storage that has one.
    private static int InstanceAt(RuntimeResource resource, int index) => ((resource.Count == 1)
        ? 0
        : index);
}
