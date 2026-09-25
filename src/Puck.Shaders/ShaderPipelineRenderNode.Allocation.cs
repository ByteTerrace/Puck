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

    /// <summary>States the descriptor pools a node creates for an installed <paramref name="plan"/>: one pool holding a
    /// set per in-flight frame for every pass that binds a descriptor, when any does, then the float preview's one pool
    /// (<see cref="PreviewDescriptorPool"/>) when it has a preview. A package pass's sets are the ones its factory
    /// states (<see cref="IRenderGraphPackageFactory.SetBindings"/>), which its recorder allocates from the same pool.
    /// The node's own pool creation reads the same statement, so an admission computed from it before anything is
    /// allocated is what the node requests.</summary>
    /// <param name="plan">The pipeline plan the node installs.</param>
    /// <param name="inFlight">The node's frames in flight.</param>
    /// <param name="preview">Whether the node presents a float preview.</param>
    /// <param name="packages">The recorders the node's package passes run through.</param>
    /// <returns>Each pool's sizes, in creation order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> or <paramref name="packages"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inFlight"/> is zero.</exception>
    /// <exception cref="InvalidDataException">A package pass names a package no recorder serves.</exception>
    public static IReadOnlyList<GpuDescriptorPoolSizes> DescriptorPools(ShaderPipelinePlan plan, uint inFlight, bool preview, RenderGraphPackageRecorders packages) {
        ArgumentNullException.ThrowIfNull(argument: plan);
        ArgumentNullException.ThrowIfNull(argument: packages);
        ArgumentOutOfRangeException.ThrowIfZero(value: inFlight);

        var pools = new List<GpuDescriptorPoolSizes>(capacity: 2);

        if (GraphDescriptorPool(
            inFlight: inFlight,
            packages: packages,
            plan: plan
        ) is { } graph) {
            pools.Add(item: graph);
        }
        if (preview) {
            pools.Add(item: PreviewDescriptorPool(inFlight: inFlight));
        }

        return pools;
    }

    // The graph's one descriptor pool: a set per in-flight frame for each pass that binds a descriptor, or none when no
    // pass does.
    private static GpuDescriptorPoolSizes? GraphDescriptorPool(ShaderPipelinePlan plan, uint inFlight, RenderGraphPackageRecorders packages) {
        var specs = VersionSpecs(plan: plan);
        var sets = new List<IReadOnlyList<GpuComputeBinding>>();

        foreach (var planned in plan.Passes) {
            // A package pass has no declaration; its recorder allocates the sets its factory states.
            var bindings = ((planned.Declaration is { } declaration)
                ? Descriptors(
                    pass: declaration,
                    specs: specs
                )
                : packages.FactoryFor(
                    instance: plan.Definition.Name,
                    package: planned.Package!.Package,
                    pass: planned.Name
                ).SetBindings);

            if (bindings.Count == 0) {
                continue;
            }
            for (var slot = 0u; (slot < inFlight); slot++) {
                sets.Add(item: bindings);
            }
        }

        return ((sets.Count == 0)
            ? null
            : GpuDescriptorPoolSizes.ForSets([.. sets])
        );
    }
    // Allocates every per-slot object a built pass needs: its descriptor set and sampler, and its command pools (one per
    // slot for a compute pass; the pre-barrier and draw pools for a fullscreen pass). They are allocated on the frame
    // thread when the built candidate installs, so an allocation failure refuses the candidate before the installed graph
    // retires, and a steady-state frame creates nothing. The graph holds one descriptor pool, owned by the pass that binds
    // a descriptor ahead of every other, and each pass allocates its sets from it. Each object is stored in
    // the pass as soon as it exists, so a failure partway leaves every created object where RuntimePass.Dispose releases
    // it exactly once. A pass that binds no descriptor, a geometry pass with no input among them, has no descriptor set
    // layout, so it gets no set or sampler, and its set stays zero.
    private void AllocateSlotObjects(RuntimePass pass, GpuDescriptorPoolSizes? graphPool, ref nint descriptorPool) {
        var bindings = m_gpu.Bindings;

        if (
            ((pass.Bindings.Count != 0) || (pass.PackageSetBindings != 0)) &&
            (descriptorPool == 0)
        ) {
            descriptorPool = bindings.CreatePool(sizes: (graphPool ?? throw new InvalidOperationException(message: "The plan states no descriptor pool for a pass that binds descriptors.")));
            pass.DescriptorPool = descriptorPool;
        }
        for (var slot = 0; (slot < m_inFlight); slot++) {
            if (pass.Bindings.Count != 0) {
                pass.Sets![slot] = bindings.AllocateSet(
                    descriptorPool,
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
