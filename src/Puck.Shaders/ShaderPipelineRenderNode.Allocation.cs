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
                pass.PoolsDescriptors![slot] = bindings.CreatePool(sizes: GpuDescriptorPoolSizes.ForSets(pass.Bindings));
                pass.Sets![slot] = bindings.AllocateSet(
                    pass.PoolsDescriptors[slot],
                    ((pass.Spec.Kind == ShaderPipelineDocumentPassKind.Compute)
                    ? pass.Compute!.DescriptorSetLayoutHandle
                    : pass.Graphics!.DescriptorSetLayoutHandle)
                );
                pass.Samplers![slot] = bindings.CreateSampler();
            }

            if (pass.Spec.Kind == ShaderPipelineDocumentPassKind.Compute) {
                pass.Pools![slot] = m_gpu.CommandPoolFactory.Create();
            } else {
                pass.Pre![slot] = m_gpu.CommandPoolFactory.Create();
                pass.Draw![slot] = m_gpu.CommandPoolFactory.Create();
            }
        }
    }
}
