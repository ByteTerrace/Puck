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

    /// <summary>States the descriptor pools a node creates for an installed <paramref name="plan"/>: one per pass that
    /// binds a descriptor per in-flight frame, in pass order and slot order within a pass, then the float preview's one
    /// per frame when it has a preview. The node's own pool creation reads the same statement, so an admission computed
    /// from it before anything is allocated is what the node requests.</summary>
    /// <param name="plan">The pipeline plan the node installs.</param>
    /// <param name="inFlight">The node's frames in flight.</param>
    /// <param name="preview">Whether the node presents a float preview.</param>
    /// <returns>Each pool's sizes, in creation order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inFlight"/> is zero.</exception>
    public static IReadOnlyList<GpuDescriptorPoolSizes> DescriptorPools(ShaderPipelinePlan plan, uint inFlight, bool preview) {
        ArgumentNullException.ThrowIfNull(argument: plan);
        ArgumentOutOfRangeException.ThrowIfZero(value: inFlight);

        var specs = VersionSpecs(plan: plan);
        var pools = new List<GpuDescriptorPoolSizes>();

        foreach (var planned in plan.Passes) {
            if (planned.Declaration is not { } declaration) {
                continue;
            }

            var bindings = Descriptors(
                pass: declaration,
                specs: specs
            );

            if (bindings.Count == 0) {
                continue;
            }

            var sizes = PassDescriptorPool(bindings: bindings);

            for (var slot = 0u; (slot < inFlight); slot++) {
                pools.Add(item: sizes);
            }
        }
        if (preview) {
            for (var slot = 0u; (slot < inFlight); slot++) {
                pools.Add(item: PreviewDescriptorPool);
            }
        }

        return pools;
    }

    // One pass's descriptor pool for one in-flight frame: the one set its bindings describe.
    private static GpuDescriptorPoolSizes PassDescriptorPool(IReadOnlyList<GpuComputeBinding> bindings) =>
        GpuDescriptorPoolSizes.ForSets(bindings);
    // Allocates every per-slot object a built pass needs: its descriptor pool, set and sampler, and its command pools
    // (one per slot for a compute pass; the pre-barrier and draw pools for a fullscreen pass). They are allocated on the frame
    // thread when the built candidate installs, so an allocation failure refuses the candidate before the installed graph
    // retires, and a steady-state frame creates nothing. Each object is stored in the pass as soon as it exists, so a failure partway
    // leaves every created object where RuntimePass.Dispose releases it exactly once. A pass that binds no descriptor, a
    // geometry pass with no input among them, has no descriptor set layout, so it gets no pool, set or sampler, and its
    // set stays zero.
    private void AllocateSlotObjects(RuntimePass pass) {
        var bindings = m_gpu.Bindings;

        for (var slot = 0; (slot < m_inFlight); slot++) {
            if (pass.Bindings.Count != 0) {
                pass.PoolsDescriptors![slot] = bindings.CreatePool(sizes: PassDescriptorPool(bindings: pass.Bindings));
                pass.Sets![slot] = bindings.AllocateSet(
                    pass.PoolsDescriptors[slot],
                    ((pass.Kind == ShaderPipelinePassKind.Compute)
                    ? pass.Compute!.DescriptorSetLayoutHandle
                    : pass.Graphics!.DescriptorSetLayoutHandle)
                );
                pass.Samplers![slot] = bindings.CreateSampler();
            }

            if (pass.Kind is ShaderPipelinePassKind.Compute or ShaderPipelinePassKind.Package) {
                pass.Pools![slot] = m_gpu.CommandPoolFactory.Create();
            } else {
                pass.Pre![slot] = m_gpu.CommandPoolFactory.Create();
                pass.Draw![slot] = m_gpu.CommandPoolFactory.Create();
            }
        }
    }
}
