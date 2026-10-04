using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// The installed graph's runtime objects: each frame slot's fence and the command pool its one command list records in,
// each storage's instances, and each pass's compiled objects, sets and regions.
public sealed partial class ShaderPipelineRenderNode {
    private sealed class FrameSlot {
        public IGpuSubmissionFence? Fence;
        // The slot's command list: every pass, the preview, the export copy and the presentation, in order.
        public IGpuCommandPool? Commands;

        // The leases this slot's latest submission sampled, retired after its fence.
        public readonly LeaseRetireList Leases = new();
    }
    // One storage of the plan, shared by its forwarding versions. Owned ordinary/history storage has one instance
    // per flight slot; transient and retained intermediates share one queue-ordered instance.
    private sealed class RuntimeResource {
        public readonly ShaderPipelinePlannedStorage Storage;
        public readonly ShaderPipelineResource Spec;
        public readonly int Count;
        public readonly CadenceVersion[] Cadence;

        // The ring follows successful writes independently of the submission slot.
        public int HistoryLatest;
        public bool HistoryWriting;

        // Per instance: whether it holds contents (cleared, written by a pass, or carried with them), and the unplanned
        // state a host event left it in, if any (see ShaderPipelineRenderNode.Tracker.cs).
        public readonly bool[] Initialized;
        public readonly bool[] HasOverride;
        public readonly ShaderPipelineAccessState[] Override;
        // Per instance: whether its override is a state the plan produced, left standing by a pass that skipped its
        // access, which the next access leaves only as the plan would, rather than a host event's.
        public readonly bool[] OverridePlanned;

        public IGpuBuffer[]? Buffers;
        public bool Borrowed;
        public IGpuImage[]? Images;
        // The image an export copies this storage into, the export that created it, and whether a copy has written it.
        public IGpuExportableImage? Export;
        public IShaderPipelineOutputExport? ExportOwner;
        public bool ExportWritten;
        // The input this output stands for while the package pass writing it draws nothing.
        public PackageAlias Alias;

        public RuntimeResource(ShaderPipelinePlannedStorage storage, int count, bool shared) {
            Storage = storage;
            Spec = storage.Declaration;
            Count = count;
            HistoryLatest = (count - 1);
            // Package-owned buffers keep one queue-ordered content identity, like retained storage. Ordinary
            // per-flight rings have no such identity and must not qualify their outputs to stand across slots.
            Cadence = ((shared || storage.Declaration.Retained || storage.History) ? storage.Versions.Select(selector: static name => new CadenceVersion(name: name)).ToArray() : []);
            Initialized = new bool[count];
            HasOverride = new bool[count];
            Override = new ShaderPipelineAccessState[count];
            OverridePlanned = new bool[count];
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
            OverridePlanned[instance] = false;
        }
        public void Dispose() {
            if (Images is not null) {
                foreach (var image in Images) {
                    image?.Dispose();
                }
            }
            if ((Buffers is not null) && !Borrowed) {
                foreach (var buffer in Buffers) {
                    buffer?.Dispose();
                }
            }
            Export?.Dispose();
        }
    }
    // A package pass has no declaration and no compiled shader; its step's ports, its recorder and their resolved
    // versions stand in for them.
    private sealed class RuntimePass(ShaderPipelinePlannedPass planned, CompiledShader? compiled, int count, (uint Width, uint Height) extent) {
        public readonly string Name = planned.Name;
        // Its position in execution order: its row of the ledger's passes and of the kernel counters.
        public readonly int Index = planned.Index;
        public readonly bool CountsKernelWork = (planned.Package?.CountsKernelWork == true);
        public readonly ShaderPipelinePassKind Kind = planned.Kind;
        public readonly ShaderPipelinePass? Spec = planned.Declaration;
        // Arrays, so the per-frame walks over a pass's bindings and accesses enumerate without allocating.
        public readonly ResourceReference[] Inputs = [.. planned.Inputs];
        public readonly ResourceReference[] Outputs = [.. planned.Outputs];
        public readonly ShaderPipelineAccess[] Accesses = [.. planned.Accesses];
        public readonly CompiledShader? Compiled = compiled;
        public readonly int Count = count;
        public uint Width = extent.Width;
        public uint Height = extent.Height;
        public readonly ShaderPipelineDimensions? Dimensions = planned.Extent;
        public readonly ShaderPipelineParameterLayout ParametersLayout = planned.Parameters;
        public ShaderPipelineParameterValues Parameters = (planned.Parameters.TryBind(
            config: null,
            reason: out _,
            values: out var values
        )
            ? values
            : throw new InvalidDataException(message: $"Invalid parameters for pass {planned.Name}.")
        );

        // A document pass's frame set per slot, null for a package pass, whose recorder binds its own sets; every pass's pass
        // block region; and where each of a document pass's ports binds in its pass group.
        public nint[]? FrameSets;
        public GpuRegion? PassRegion;
        // A document pass's World group: the row region each of its arrays reads, in the order of its Arrays, and each
        // slot's set; both null for a pass that declares no array. The graph's row regions, on the pass that took them;
        // null on every other pass.
        public GpuRegion[]? ArrayRegions;
        public nint[]? WorldSets;
        public RowRegion[]? RowRegions;
        public PortBinding[]? PortBindings;
        // The graph's frame region, on the pass that created it; null on every other pass.
        public GpuRegion? FrameRegion;
        // The regions a package pass's package states, which the node flushes and copies (ShaderPipelineRenderNode.Regions.cs),
        // null for a pass that states none; and the graph's copy pool, reserving every staged region's sets, on its first
        // pass and null on every other.
        public GpuRegion[]? Regions;
        public ulong RegionCpuScratchBytes;
        public GpuRegionCopyPool? RegionCopySets;
        // The counter buffers the graph's kernels count their own work into, on its first pass when a pass counts
        // (ShaderPipelinePlan.CountsKernelWork) and null on every other.
        public GpuKernelCounters? KernelCounters;

        public IReadOnlyList<string> WorkDetails = [];

        public uint WorkDetailRow;

        public bool Grouped => (PortBindings is not null);

        // Why a package pass's outputs cannot stand for its inputs when it draws nothing, or null when they can.
        public string? PackageAliasRefusal;
        // The pass's pipeline, leased from the node's pass-pipeline cache and released last, and the pipeline and render
        // pass it holds; a package pass leases through its own recorder instead.
        public GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? Pipeline;
        public IGpuComputePipeline? Compute;
        public IGpuFramebuffer[]? Framebuffers;
        public IGpuPipeline? Graphics;
        // The graph's one descriptor pool, on the pass that created it; zero on every other pass, whose sets it also
        // holds, so disposing the graph's passes destroys it once.
        public nint DescriptorPool;
        public IGpuRenderPass? RenderPass;
        public nint[]? Samplers;
        public nint[]? Sets;
        public IGpuBuffer? GeometryBuffer;
        public IRenderGraphPackageRecorder? Package;
        public PassCadence? Cadence;
        public bool Recorded;
        public RenderGraphPackageResource[]? PackageInputs;
        public RenderGraphPackageResource[]? PackageOutputs;
        public GpuImageLayout[]? PackageInputLayouts;
        public GpuImageLayout[]? PackageOutputLayouts;
        // The version an indirectly dispatched package pass reads its group counts from, or null.
        public string? PackageArguments;

        public void Dispose(GpuDeviceServices gpu, IGpuDeviceContext device) {
            Package?.Dispose();
            Package = null;
            GeometryBuffer?.Dispose();
            if (Framebuffers is not null) {
                foreach (var framebuffer in Framebuffers) {
                    framebuffer?.Dispose();
                }
            }
            if (Regions is not null) {
                foreach (var region in Regions) {
                    region?.Dispose();
                }

                Regions = null;
            }
            RegionCopySets?.Dispose();
            RegionCopySets = null;
            KernelCounters?.Dispose();
            KernelCounters = null;
            PassRegion?.Dispose();
            PassRegion = null;
            foreach (var region in (RowRegions ?? [])) {
                region.Region.Dispose();
            }

            RowRegions = null;
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
            Compute = null;
            Graphics = null;
            RenderPass = null;
            Pipeline?.Release();
            Pipeline = null;
        }
    }
}
