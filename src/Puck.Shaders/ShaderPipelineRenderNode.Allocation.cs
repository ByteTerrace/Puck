using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

// Graph allocation that completes before a graph is installed, and the allocation-free helpers the per-frame path uses.
public sealed partial class ShaderPipelineRenderNode {
    private Action<string>? m_captureWriter;

    // Whether any device object exists: every submission, preview and readback follows the first installed graph,
    // which creates the frame slots' fences, so a node with no fence has allocated and submitted nothing.
    private bool HoldsDeviceObjects {
        get {
            foreach (var slot in m_slots) {
                if (
                    (slot.Fence is not null) ||
                    (slot.Final is not null)
                ) {
                    return true;
                }
            }

            return (
                (m_preview is not null) ||
                (m_readback is not null) ||
                (m_resources.Length != 0) ||
                (m_retired.Count != 0) ||
                (m_held.Count != 0)
            );
        }
    }

    /// <summary>States the descriptor pools a node creates for an installed <paramref name="plan"/>: one pool holding
    /// every pass's frame group and pass group sets once per in-flight frame, then the float preview's one pool
    /// (<see cref="PreviewDescriptorPool"/>) when it has a preview. A package pass's sets are laid out by the plan as a
    /// document pass's are, and its recorder allocates them from the same pool (<see cref="RenderGraphPackageSets"/>).
    /// The node's own pool creation reads the same statement, so an admission computed from it before anything is
    /// allocated is what the node requests.</summary>
    /// <param name="plan">The pipeline plan the node installs.</param>
    /// <param name="inFlight">The node's frames in flight.</param>
    /// <param name="preview">Whether the node presents a float preview.</param>
    /// <returns>Each pool's sizes, in creation order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inFlight"/> is zero.</exception>
    public static IReadOnlyList<GpuDescriptorPoolSizes> DescriptorPools(ShaderPipelinePlan plan, uint inFlight, bool preview) {
        ArgumentNullException.ThrowIfNull(argument: plan);
        ArgumentOutOfRangeException.ThrowIfZero(value: inFlight);

        var pools = new List<GpuDescriptorPoolSizes>(capacity: 2);

        if (GraphDescriptorPool(
            inFlight: inFlight,
            plan: plan
        ) is { } graph) {
            pools.Add(item: graph);
        }
        if (preview) {
            pools.Add(item: PreviewDescriptorPool(inFlight: inFlight));
        }

        return pools;
    }

    // The graph's one descriptor pool: a frame set and a pass set per in-flight frame for each pass; none for a graph with
    // no pass.
    private static GpuDescriptorPoolSizes? GraphDescriptorPool(ShaderPipelinePlan plan, uint inFlight) {
        var groups = default(GpuDescriptorPoolSizes);

        foreach (var planned in plan.Passes) {
            groups += GroupPoolSizes(
                inFlight: inFlight,
                planned: planned
            );
        }

        return ((groups.MaxSets == 0)
            ? null
            : groups);
    }
    // Allocates every per-slot object a built pass needs: its pass region, its sets and sampler, and its command pools
    // (one per slot for a compute pass; the pre-barrier and draw pools for a fullscreen pass). They are allocated on the frame
    // thread when the built candidate installs, so an allocation failure refuses the candidate before the installed graph
    // retires, and a steady-state frame creates nothing. The graph holds one descriptor pool, owned by the pass that binds
    // a descriptor ahead of every other, and each pass allocates its sets from it. Each object is stored in
    // the pass as soon as it exists, so a failure partway leaves every created object where RuntimePass.Dispose releases
    // it exactly once. A document pass allocates its frame group and pass group sets (AllocateGroupSets); a package pass
    // allocates its own sets through its recorder.
    private void AllocateSlotObjects(RuntimePass pass, GpuDescriptorPoolSizes? graphPool, ref nint descriptorPool) {
        var bindings = m_gpu.Bindings;

        if (descriptorPool == 0) {
            descriptorPool = bindings.CreatePool(
                name: new GpuObjectName(
                    owner: m_descriptor.Name,
                    part: "descriptors"
                ),
                sizes: (graphPool ?? throw new InvalidOperationException(message: "The plan states no descriptor pool for a pass that binds descriptors."))
            );
            pass.DescriptorPool = descriptorPool;
        }
        CreatePassRegion(pass: pass);
        if (pass.Grouped) {
            AllocateGroupSets(
                descriptorPool: descriptorPool,
                pass: pass
            );
        }
        for (var slot = 0; (slot < m_inFlight); slot++) {
            if (pass.Kind is ShaderPipelinePassKind.Compute or ShaderPipelinePassKind.Package) {
                pass.Pools![slot] = m_gpu.CommandPoolFactory.Create(name: new GpuObjectName(
                    index: slot,
                    owner: m_descriptor.Name,
                    part: pass.Name
                ));
            } else {
                pass.Pre![slot] = m_gpu.CommandPoolFactory.Create(name: new GpuObjectName(
                    detail: "barriers",
                    index: slot,
                    owner: m_descriptor.Name,
                    part: pass.Name
                ));
                pass.Draw![slot] = m_gpu.CommandPoolFactory.Create(name: new GpuObjectName(
                    detail: "draw",
                    index: slot,
                    owner: m_descriptor.Name,
                    part: pass.Name
                ));
            }
        }
    }
}
