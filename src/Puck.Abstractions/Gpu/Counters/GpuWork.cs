using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// The kinds of GPU work the counting wrappers of <see cref="GpuWorkCounting"/> record. Submission kinds are counted
/// per submission and per pass into a <see cref="GpuWorkLedger"/>, and their position in
/// <see cref="SubmissionKinds"/> is the column a <see cref="GpuWorkSample"/> reads them by. Lifetime kinds count
/// objects created over the ledger's whole life and are read through its <see cref="IWorkCounterSource"/>.
/// <para>
/// Every count but the kernel kinds (<see cref="KernelKinds"/>) is of calls the node makes through a neutral interface,
/// so it is the same on every backend for the same inputs. A barrier is counted as the node requests it; what a backend
/// does to satisfy it is not counted. The kernel kinds are counted by a pass's own kernels on the GPU, into the node's
/// counter buffers (<see cref="GpuKernelCounters"/>), and reach the pass's row once its submission completes.
/// </para>
/// </summary>
public static partial class GpuWork {
    internal const int BufferBarriersColumn = 7;
    internal const int BufferCopyBytesColumn = 17;
    internal const int BuffersCreatedIndex = 3;
    internal const int ClearsColumn = 13;
    internal const int CommandBuffersColumn = 4;
    internal const int CopiesColumn = 14;
    internal const int DescriptorPoolsCreatedIndex = 4;
    internal const int DescriptorSetBindsColumn = 9;
    internal const int DescriptorSetsCreatedIndex = 5;
    internal const int DescriptorWritesColumn = 11;
    internal const int DispatchesColumn = 0;
    internal const int DrawsColumn = 2;
    internal const int HostVisibleUploadBytesColumn = 12;
    internal const int ImageBarriersColumn = 5;
    internal const int ImagesCreatedIndex = 2;
    internal const int IndirectDispatchesColumn = 1;
    internal const int LifetimeKindCount = 7;
    internal const int MarchStepsColumn = 15;
    internal const int MemoryBarriersColumn = 6;
    internal const int PipelineBindsColumn = 8;
    internal const int PipelinesCreatedIndex = 0;
    internal const int PushConstantBytesColumn = 10;
    internal const int RenderPassesColumn = 3;
    internal const int ShaderModulesCreatedIndex = 1;
    internal const int SkyEvaluationsColumn = 18;
    internal const int SkyHashesColumn = 19;
    internal const int SkyTextureLoadsColumn = 20;
    internal const int EnvironmentProjectionsColumn = (ShadowPixelsColumn + 1);
    internal const int EnvironmentProjectionTexelsColumn = (EnvironmentProjectionsColumn + 1);
    internal const int EnvironmentSkippedColumn = (EnvironmentProjectionsColumn + 2);
    internal const int SubmissionColumnCount = (EnvironmentProjectionsColumn + 3);
    internal const int TexelsWrittenColumn = 16;
    internal const int TimestampPoolsCreatedIndex = 6;

    /// <summary>Gets the number of CPU sky projections used to decide environment refreshes.</summary>
    public static WorkKind EnvironmentProjections { get; } = new(name: "gpu.environment.projections", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the texels evaluated by CPU sky projections.</summary>
    public static WorkKind EnvironmentProjectionTexels { get; } = new(name: "gpu.environment.projection-texels", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the changed candidates whose environment re-render was skipped below one display code.</summary>
    public static WorkKind EnvironmentSkipped { get; } = new(name: "gpu.environment.skipped", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting compute dispatches whose group counts the CPU supplies.</summary>
    public static WorkKind Dispatches { get; } = new(name: "gpu.dispatches", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting compute dispatches whose group counts the GPU reads from a buffer.</summary>
    public static WorkKind IndirectDispatches { get; } = new(name: "gpu.dispatches.indirect", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting draws, indexed or not.</summary>
    public static WorkKind Draws { get; } = new(name: "gpu.draws", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting render pass instances begun.</summary>
    public static WorkKind RenderPasses { get; } = new(name: "gpu.render-passes", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting command buffers begun for recording.</summary>
    public static WorkKind CommandBuffers { get; } = new(name: "gpu.command-buffers", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting image layout barriers requested.</summary>
    public static WorkKind ImageBarriers { get; } = new(name: "gpu.barriers.image", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting global memory barriers requested.</summary>
    public static WorkKind MemoryBarriers { get; } = new(name: "gpu.barriers.memory", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting single-buffer barriers requested.</summary>
    public static WorkKind BufferBarriers { get; } = new(name: "gpu.barriers.buffer", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting compute and graphics pipeline binds.</summary>
    public static WorkKind PipelineBinds { get; } = new(name: "gpu.binds.pipeline", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting descriptor-set binds.</summary>
    public static WorkKind DescriptorSetBinds { get; } = new(name: "gpu.binds.descriptor-set", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting push-constant payload bytes recorded.</summary>
    public static WorkKind PushConstantBytes { get; } = new(name: "gpu.push-constants", unit: "bytes", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting descriptors written into sets.</summary>
    public static WorkKind DescriptorWrites { get; } = new(name: "gpu.descriptor-writes", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting bytes the CPU writes into host-visible storage buffers.</summary>
    public static WorkKind HostVisibleUploadBytes { get; } = new(name: "gpu.uploads.host-visible", unit: "bytes", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting whole-resource clears of storage images and buffers.</summary>
    public static WorkKind Clears { get; } = new(name: "gpu.clears", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting whole-image copies and buffer-range copies.</summary>
    public static WorkKind Copies { get; } = new(name: "gpu.copies", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the bytes each buffer-range copy transfers, including readback and
    /// device-local history copies. Image copies have no buffer-byte count; host writes have their own kind.</summary>
    public static WorkKind BufferCopyBytes { get; } = new(name: "gpu.copies.buffer-bytes", unit: "bytes", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the field evaluations a pass's kernels make: each sample of a march (the beam's,
    /// primary's, the ambient occlusion's and the soft shadow's) and each query (a normal's taps). The kernels count it on
    /// the GPU, so it is per-backend-deterministic: the marches run in floats, and an indirect pass runs only the tiles
    /// culling leaves it.</summary>
    public static WorkKind MarchSteps { get; } = new(name: "gpu.march.steps", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting the pixels a pass's kernels write an output for: an image texel, or a pixel's
    /// visibility record. The kernels count it on the GPU, so it is per-backend-deterministic, as
    /// <see cref="MarchSteps"/> is.</summary>
    public static WorkKind TexelsWritten { get; } = new(name: "gpu.texels.written", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting sky layer evaluations, including analytic point layers, field fallbacks and
    /// the gradient used by surface fog. Each evaluated layer counts once per invocation. Coverage comes from the
    /// march, so it is per-backend-deterministic, as <see cref="MarchSteps"/> is.</summary>
    public static WorkKind SkyEvaluations { get; } = new(name: "gpu.sky.evaluations", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting procedural hashes evaluated by sky layers, including noise lattice hashes.</summary>
    public static WorkKind SkyHashes { get; } = new(name: "gpu.sky.hashes", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting texture loads of sky field runs, including invalid base taps.</summary>
    public static WorkKind SkyTextureLoads { get; } = new(name: "gpu.sky.texture-loads", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting compute and graphics pipelines created. A node's ledger counts the pipelines that
    /// node created; a backend's <see cref="GpuPipelineCacheWork"/> counts every pipeline its devices created.</summary>
    public static WorkKind PipelinesCreated { get; } = new(name: "gpu.created.pipelines", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting pipeline creations the backend's persistent pipeline cache answered: the driver
    /// reused native code it had cached instead of compiling it. Pacing: it depends on what the cache already holds.</summary>
    public static WorkKind PipelineCacheHits { get; } = new(name: "gpu.pipeline-cache.hits", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting pipeline creations the backend's persistent pipeline cache could not answer, and
    /// those whose driver does not report how the cache answered. Pacing, like the hits.</summary>
    public static WorkKind PipelineCacheMisses { get; } = new(name: "gpu.pipeline-cache.misses", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting pipeline-cache files the backend deleted when it opened a device's cache file: the
    /// least recently written beyond <see cref="GpuPipelineCacheFile.RetainedFiles"/> across all its devices. Pacing: it
    /// depends on what earlier processes left on disk.</summary>
    public static WorkKind PipelineCachePruned { get; } = new(name: "gpu.pipeline-cache.pruned", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting shader modules created.</summary>
    public static WorkKind ShaderModulesCreated { get; } = new(name: "gpu.created.shader-modules", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting images created, whatever their usages.</summary>
    public static WorkKind ImagesCreated { get; } = new(name: "gpu.created.images", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting storage buffers created, host-visible and device-local alike.</summary>
    public static WorkKind BuffersCreated { get; } = new(name: "gpu.created.buffers", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting descriptor pools created.</summary>
    public static WorkKind DescriptorPoolsCreated { get; } = new(name: "gpu.created.descriptor-pools", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting descriptor sets allocated.</summary>
    public static WorkKind DescriptorSetsCreated { get; } = new(name: "gpu.created.descriptor-sets", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting timestamp query pools created on demand.</summary>
    public static WorkKind TimestampPoolsCreated { get; } = new(name: "gpu.created.timestamp-pools", unit: "count", workClass: WorkClass.PerBackendDeterministic);

    /// <summary>Gets the kinds a pass's kernels count on the GPU, in the order a counter row holds them
    /// (<see cref="GpuKernelCounters"/>): <see cref="MarchSteps"/>, <see cref="TexelsWritten"/>, then
    /// <see cref="SkyEvaluations"/>, <see cref="SkyHashes"/> and <see cref="SkyTextureLoads"/>, then <see cref="ShadowSteps"/> in slot order.</summary>
    public static ReadOnlySpan<WorkKind> KernelKinds =>
        Order.Kernel;
    /// <summary>Gets the lifetime kinds, in the order a report lists them.</summary>
    public static ReadOnlySpan<WorkKind> LifetimeKinds =>
        Order.Lifetime;
    /// <summary>Gets the per-submission kinds in their fixed column order; a kind's index here is the column
    /// <see cref="GpuWorkSample"/> reads it by.</summary>
    public static ReadOnlySpan<WorkKind> SubmissionKinds =>
        Order.Submission;

    // A nested holder initializes after every kind above, whatever order the members are declared in. Each array is
    // filled through the column constants, so a kind's index is its column by construction.
    private static class Order {
        internal static readonly WorkKind[] Kernel = [MarchSteps, TexelsWritten, SkyEvaluations, SkyHashes, SkyTextureLoads, .. ShadowSteps, ShadowPixels];
        internal static readonly WorkKind[] Lifetime = CreateLifetime();
        internal static readonly WorkKind[] Submission = CreateSubmission();

        private static WorkKind[] CreateLifetime() {
            var kinds = new WorkKind[LifetimeKindCount];

            kinds[PipelinesCreatedIndex] = PipelinesCreated;
            kinds[ShaderModulesCreatedIndex] = ShaderModulesCreated;
            kinds[ImagesCreatedIndex] = ImagesCreated;
            kinds[BuffersCreatedIndex] = BuffersCreated;
            kinds[DescriptorPoolsCreatedIndex] = DescriptorPoolsCreated;
            kinds[DescriptorSetsCreatedIndex] = DescriptorSetsCreated;
            kinds[TimestampPoolsCreatedIndex] = TimestampPoolsCreated;

            return kinds;
        }
        private static WorkKind[] CreateSubmission() {
            var kinds = new WorkKind[SubmissionColumnCount];

            kinds[EnvironmentProjectionsColumn] = EnvironmentProjections;
            kinds[EnvironmentProjectionTexelsColumn] = EnvironmentProjectionTexels;
            kinds[EnvironmentSkippedColumn] = EnvironmentSkipped;
            kinds[ShadowPixelsColumn] = ShadowPixels;
            kinds[DispatchesColumn] = Dispatches;
            kinds[IndirectDispatchesColumn] = IndirectDispatches;
            kinds[DrawsColumn] = Draws;
            kinds[RenderPassesColumn] = RenderPasses;
            kinds[CommandBuffersColumn] = CommandBuffers;
            kinds[ImageBarriersColumn] = ImageBarriers;
            kinds[MemoryBarriersColumn] = MemoryBarriers;
            kinds[BufferBarriersColumn] = BufferBarriers;
            kinds[PipelineBindsColumn] = PipelineBinds;
            kinds[DescriptorSetBindsColumn] = DescriptorSetBinds;
            kinds[PushConstantBytesColumn] = PushConstantBytes;
            kinds[DescriptorWritesColumn] = DescriptorWrites;
            kinds[HostVisibleUploadBytesColumn] = HostVisibleUploadBytes;
            kinds[ClearsColumn] = Clears;
            kinds[CopiesColumn] = Copies;
            kinds[MarchStepsColumn] = MarchSteps;
            kinds[TexelsWrittenColumn] = TexelsWritten;
            kinds[BufferCopyBytesColumn] = BufferCopyBytes;
            kinds[SkyEvaluationsColumn] = SkyEvaluations;
            kinds[SkyHashesColumn] = SkyHashes;
            kinds[SkyTextureLoadsColumn] = SkyTextureLoads;
            for (var slot = 0; (slot < ShadowSlotCount); slot++) {
                kinds[(ShadowStepsFirstColumn + slot)] = ShadowSteps[slot];
            }

            return kinds;
        }
    }
}
