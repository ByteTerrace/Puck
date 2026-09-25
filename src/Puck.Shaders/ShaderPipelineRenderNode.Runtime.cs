using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// The installed graph's runtime objects: each frame slot's fence and final command pool, each storage's instances,
// and each pass's compiled objects, sets and regions.
public sealed partial class ShaderPipelineRenderNode {
    private sealed class FrameSlot {
        public IGpuSubmissionFence? Fence;
        public IGpuCommandPool? Final;

        // The leases this slot's latest submission sampled, retired after its fence.
        public readonly LeaseRetireList Leases = new();
    }
    // One storage of the plan, which every version of its forwarding chain names, with one instance per frame slot (one
    // instance only for a host-owned storage).
    private sealed class RuntimeResource {
        public readonly ShaderPipelinePlannedStorage Storage;
        public readonly ShaderPipelineResource Spec;
        public readonly int Count;
        // Per instance: whether it holds contents (cleared, written by a pass, or carried with them), and the unplanned
        // state a host event left it in, if any (see ShaderPipelineRenderNode.Tracker.cs).
        public readonly bool[] Initialized;
        public readonly bool[] HasOverride;
        public readonly ShaderPipelineAccessState[] Override;

        public IGpuBuffer[]? Buffers;
        public IGpuImage[]? Images;
        // The input this output stands for while the package pass writing it draws nothing.
        public PackageAlias Alias;

        public RuntimeResource(ShaderPipelinePlannedStorage storage, int count) {
            Storage = storage;
            Spec = storage.Declaration;
            Count = count;
            Initialized = new bool[count];
            HasOverride = new bool[count];
            Override = new ShaderPipelineAccessState[count];
            if (!Spec.IsExternal) {
                // A new instance holds nothing, and no access has touched it.
                Array.Fill(
                    array: HasOverride,
                    value: true
                );
                Array.Fill(
                    array: Override,
                    value: ShaderPipelineAccessState.Fresh
                );
            }
        }

        public bool History => Storage.History;

        public ShaderPipelineAccessState Prior(int instance) =>
            (HasOverride[instance]
                ? Override[instance]
                : ShaderPipelineAccessState.Fresh);
        public void SetOverride(int instance, ShaderPipelineAccessState state) {
            Override[instance] = state;
            HasOverride[instance] = true;
        }
        public void Dispose() {
            if (Images is not null) {
                foreach (var image in Images) {
                    image?.Dispose();
                }
            }
            if (Buffers is not null) {
                foreach (var buffer in Buffers) {
                    buffer?.Dispose();
                }
            }
        }
    }
    // A package pass has no declaration and no compiled shader; its step's ports, its recorder and their resolved
    // versions stand in for them.
    private sealed class RuntimePass(ShaderPipelinePlannedPass planned, CompiledShader? compiled, int count, (uint Width, uint Height) extent) {
        public readonly string Name = planned.Name;
        public readonly ShaderPipelinePassKind Kind = planned.Kind;
        public readonly ShaderPipelinePass? Spec = planned.Declaration;
        // Arrays, so the per-frame walks over a pass's bindings and accesses enumerate without allocating.
        public readonly ResourceReference[] Inputs = [.. planned.Inputs];
        public readonly ResourceReference[] Outputs = [.. planned.Outputs];
        public readonly ShaderPipelineAccess[] Accesses = [.. planned.Accesses];
        public readonly CompiledShader? Compiled = compiled;
        public readonly int Count = count;
        public readonly uint Width = extent.Width;
        public readonly uint Height = extent.Height;
        public readonly ShaderPipelineParameterLayout ParametersLayout = planned.Parameters;
        public ShaderPipelineParameterValues Parameters = (planned.Parameters.TryBind(
            config: null,
            reason: out _,
            values: out var values
        )
            ? values
            : throw new InvalidDataException(message: $"Invalid parameters for pass {planned.Name}.")
        );
        // A grouped pass's frame set per slot, its pass block's region, and where each of its ports binds in its pass
        // group; null for a pushed or package pass.
        public nint[]? FrameSets;
        public GpuRegion? PassRegion;
        public PortBinding[]? PortBindings;
        // The graph's frame region, on the grouped pass that created it; null on every other pass.
        public GpuRegion? FrameRegion;
        public bool Grouped => (PortBindings is not null);

        // A package pass's per-slot set bindings, which its recorder allocates from the graph's pool.
        public int PackageSetBindings;
        // Why a package pass's outputs cannot stand for its inputs when it draws nothing, or null when they can.
        public string? PackageAliasRefusal;
        public IGpuComputePipeline? Compute;
        public IGpuCommandPool[]? Draw;
        public IGpuFramebuffer[]? Framebuffers;
        public IGpuPipeline? Graphics;
        public IGpuCommandPool[]? Pools;
        // The graph's one descriptor pool, on the pass that created it; zero on every other pass, whose sets it also
        // holds, so disposing the graph's passes destroys it once.
        public nint DescriptorPool;
        public IGpuCommandPool[]? Pre;
        public IGpuShaderModule? Primary;
        public IGpuRenderPass? RenderPass;
        public nint[]? Samplers;
        public IGpuShaderModule? Secondary;
        public nint[]? Sets;
        public IGpuBuffer? GeometryBuffer;
        public IRenderGraphPackageRecorder? Package;
        public RenderGraphPackageResource[]? PackageInputs;
        public RenderGraphPackageResource[]? PackageOutputs;
        public GpuImageLayout[]? PackageInputLayouts;
        public GpuImageLayout[]? PackageOutputLayouts;

        public void Dispose(GpuDeviceServices gpu, IGpuDeviceContext device) {
            Package?.Dispose();
            Package = null;
            Compute?.Dispose();
            GeometryBuffer?.Dispose();
            if (Framebuffers is not null) {
                foreach (var framebuffer in Framebuffers) {
                    framebuffer?.Dispose();
                }
            }
            Graphics?.Dispose();
            RenderPass?.Dispose();
            Primary?.Dispose();
            Secondary?.Dispose();
            if (Draw is not null) {
                foreach (var pool in Draw) {
                    pool?.Dispose();
                }
            }
            if (Pools is not null) {
                foreach (var pool in Pools) {
                    pool?.Dispose();
                }
            }
            if (Pre is not null) {
                foreach (var pool in Pre) {
                    pool?.Dispose();
                }
            }
            PassRegion?.Dispose();
            PassRegion = null;
            FrameRegion?.Dispose();
            FrameRegion = null;
            gpu.Bindings.DestroyPool(poolHandle: DescriptorPool);
            DescriptorPool = 0;
            if (Samplers is not null) {
                foreach (var sampler in Samplers) {
                    if (sampler != 0) {
                        gpu.Bindings.DestroySampler(
                            samplerHandle: sampler
                        );
                    }
                }
            }
        }
    }
}
