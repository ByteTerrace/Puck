using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Maths;
using Puck.Shaders;

namespace Puck.SdfVm;

// The glass tiles' upkeep (SdfKernel.GlassTiles, passes/sdf-glass-tiles.comp.hlsl) on the glass interface, for two parts:
// - the glass clear (SdfWorldPackage.Parts.GlassClear) zeroes the sums of the tiles the preceding frame listed, then the
//   tiles' header and both masks, so the cost follows the tiles the glass covered, not the image;
// - the glass tiles pass (SdfWorldPackage.Parts.GlassTiles) commits the instance's glass plan
//   (SdfWorldPasses.CommitShadePlan with glass), lists the tiles the raster marked in tile order and writes the glass
//   shading pass's indirect arguments for the plan from their count.
// Both skip a frame whose glass passes do not run (SdfWorldPasses.GlassSeen), with the raster.
// The clear's invariant: every glass sums word outside the tiles the last executed tiles pass listed is zero, and the
// tiles buffer's count and list name those tiles (frame/sdf-glass-tiles.hlsli). The GPU keeps it from one frame to the
// next (the clear zeroes the listed tiles, the raster marks every tile it writes in, sdfGlassMarkTile, and the tiles pass,
// which records whenever the raster does, lists every marked tile); the host knows it holds
// only for the pair of buffers whose whole clear a submitted recording made (m_kept), so any other pair, a new
// allocation or one a graph replacement brought, is cleared whole before its first raster. The clear and the raster stand
// or record together (one signature, one input), so the raster never accumulates onto a clear it did not see.
internal sealed class SdfGlassTilesRecorder : SdfViewRecorder, IRenderGraphPackageRecorder {
    private static readonly int GlassJobsOffset = ((int)SdfWorldInterfaces.GlassParameters.BlockOffsetOf(member: SdfWorldPackage.GlassJobs));
    // The steps of the kernel, its pushed index (passes/sdf-glass-tiles.comp.hlsl).
    private const uint ClearSumsStep = 0u;
    private const uint ClearMasksStep = 1u;
    private const uint ListStep = 2u;
    // The invocations of a group, SdfGlassTilesGroupSize in the kernel.
    private const uint GroupSize = 256u;
    // The most groups the clear's first step dispatches, each striding over the listed tiles a tile at a time: enough to
    // keep the device's write bandwidth busy over a screen of tiles, few enough that a frame listing a handful launches
    // almost nothing.
    private const uint ClearGroups = 1024u;
    // The members only the glass shading pass reads or writes, which this pass binds fillers at.
    private static readonly string[] ShadingBuffers = [
        SdfWorldPackage.VisibilityRecords,
        SdfWorldPackage.GlassSums,
        SdfWorldPackage.ReactivityWritten,
        SdfWorldPackage.GlassProbeHistory,
        SdfWorldPackage.GlassProbeHistoryWritten,
    ];

    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly SdfWorldPasses m_owner;
    private readonly RenderGraphPackageSets m_sets;
    private readonly RenderGraphPackageWorkCounters m_work;
    private readonly bool m_clears;
    // Per frame slot, the tables and the two buffers the slot's pass set was written with: the sums and the tiles for the
    // clear, the tiles and the arguments for the tiles pass.
    private readonly (SdfWorldTables? Tables, IGpuBuffer? First, IGpuBuffer? Second)[] m_ports;
    // The pair of glass sums and tiles buffers the clear keeps zero outside the listed tiles, and the pair a recording
    // cleared whole, which becomes the kept pair once that recording's submission reaches the queue.
    private (IGpuBuffer Sums, IGpuBuffer Tiles)? m_kept;
    private (IGpuBuffer Sums, IGpuBuffer Tiles)? m_pending;
    private bool m_disposed;

    internal SdfGlassTilesRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, SdfWorldPasses owner, SdfWorldView view) : base(owner: owner, instance: context.Instance, view: view) {
        m_context = context;
        m_owner = owner;
        m_clears = (context.Part == SdfWorldPackage.Parts.GlassClear);
        m_ports = new (SdfWorldTables?, IGpuBuffer?, IGpuBuffer?)[context.InFlightFrames];
        m_sets = new RenderGraphPackageSets(context: context, groups: groups, groupLayoutHandles: view.Residency.Tables!.Pipeline(kernel: SdfKernel.GlassTiles).GroupLayoutHandles);
        m_work = new RenderGraphPackageWorkCounters(context: context, sets: m_sets);
    }

    // Whether a fragment version is one of the glass tiles' (SdfWorldPackage.GlassTiles): a compiled port may carry the name
    // of any version of the clear's storage.
    internal static bool IsTilesVersion(string name) =>
        (name is SdfWorldPackage.Parts.GlassTilesCleared or SdfWorldPackage.Parts.GlassTilesMarked or SdfWorldPackage.Parts.GlassTilesListed or SdfWorldPackage.Parts.GlassTilesShaded);

    public void Dispose() {
        if (m_disposed) {
            return;
        }
        m_disposed = true;
        LeaveView();
    }
    public bool Skips(in FrameContext context) => !m_owner.GlassSeen(instance: m_context.Instance, view: m_view, context: in context);
    public ulong? Signature(in FrameContext context, RenderGraphExternalReads? reads) =>
        m_owner.SignatureOf(instance: m_context.Instance, part: m_context.Part!, temporal: m_temporal, context: in context, reads: reads);
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        var residency = m_view.Residency;
        var tables = residency.Staged(context: recording.Context);
        var set = m_sets.PassSet(slot: recording.Slot);

        m_pending = null;
        m_work.Write(passSet: set, recording: recording);
        if (m_clears) {
            RecordClear(recording: in recording, set: set, tables: tables);
        } else {
            RecordArguments(recording: in recording, set: set, tables: tables);
        }
        return RenderGraphPackageOutcome.Drew;
    }
    public void Submitted() {
        if (m_pending is { } cleared) {
            m_kept = cleared;
            m_pending = null;
        }
    }

    // Zeroes the sums of the tiles the preceding raster listed, then the tiles' header and mask; or, for a pair the host
    // does not know is kept, both buffers whole.
    private void RecordClear(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables) {
        var sums = recording.Outputs[0].Buffer!;
        var tiles = recording.Outputs[1].Buffer!;
        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;

        if (m_kept is not { } kept || !ReferenceEquals(objA: kept.Sums, objB: sums) || !ReferenceEquals(objA: kept.Tiles, objB: tiles)) {
            foreach (var buffer in ((ReadOnlySpan<IGpuBuffer>)[sums, tiles])) {
                recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: buffer.BufferHandle,
                    sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                    destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
                recorder.ClearStorageBuffer(commandBuffer, buffer.BufferHandle, buffer.SizeBytes);
                recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: buffer.BufferHandle,
                    sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
                    destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            }
            m_pending = (sums, tiles);
            return;
        }

        var capacity = SdfWorldPackage.GlassTileCapacity(bytes: tiles.SizeBytes);
        var groups = (uint)Math.Clamp(value: capacity, min: 1UL, max: ClearGroups);
        var maskGroups = (uint)Math.Max(val1: 1UL, val2: SdfWorldPackage.GlassTileClearWords(capacity: capacity).CeilingDivide(divisor: GroupSize));

        WriteJobs(block: recording.PassBlock, width: 0u, count: groups);
        BindPorts(recording: in recording, set: set, tables: tables, sums: sums, tiles: tiles, arguments: tables.DummyBuffer);
        var pipeline = Bind(recording: in recording, tables: tables);

        Dispatch(recording: in recording, pipeline: pipeline, step: ClearSumsStep, groups: groups);
        // The second step zeroes the count and the list's marks the first read.
        recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: tiles.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
        Dispatch(recording: in recording, pipeline: pipeline, step: ClearMasksStep, groups: maskGroups);
    }
    // Commits the instance's glass plan, lists the marked tiles in tile order and writes a record of the shading pass's
    // indirect arguments for each of the plan's jobs.
    private void RecordArguments(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables) {
        var residency = m_view.Residency;
        var frame = residency.Frame!;
        var index = Math.Min(val1: m_view.View, val2: (frame.Views.Count - 1));
        var plan = m_owner.CommitShadePlan(instance: m_context.Instance, frame: frame, width: recording.RenderWidth, height: recording.RenderHeight,
            tableLights: tables.Lights.Count, lightMask: tables.GlassLightMaskOf(frame: frame, view: index), glass: true);

        WriteJobs(block: recording.PassBlock, width: plan.JobWidth, count: (uint)plan.Jobs.Count);
        BindPorts(recording: in recording, set: set, tables: tables, sums: tables.DummyBuffer, tiles: recording.Outputs[0].Buffer!, arguments: recording.Outputs[1].Buffer!);
        Dispatch(recording: in recording, pipeline: Bind(recording: in recording, tables: tables), step: ListStep, groups: 1u);
    }

    private static void WriteJobs(Span<byte> block, uint width, uint count) {
        BinaryPrimitives.WriteUInt32LittleEndian(destination: block[GlassJobsOffset..], value: width);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: block[(GlassJobsOffset + sizeof(uint))..], value: count);
    }
    // Binds the kernel with the slot's pass set and the residency's World set, which the glass layout holds.
    private IGpuComputePipeline Bind(in RenderGraphPackageRecording recording, SdfWorldTables tables) {
        var pipeline = tables.Pipeline(kernel: SdfKernel.GlassTiles);

        recording.Recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: recording.CommandBuffer, pipelineHandle: pipeline.Handle);
        m_sets.Bind(bindPoint: GpuBindPoint.Compute, commandBuffer: recording.CommandBuffer, pipelineLayout: pipeline.LayoutHandle, recorder: recording.Recorder, slot: recording.Slot);
        recording.Recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: recording.CommandBuffer, descriptorSetHandle: tables.WorldSet(slot: tables.CurrentSlot),
            group: ((uint)ShaderInterfaceGroup.World), pipelineLayoutHandle: pipeline.LayoutHandle);
        return pipeline;
    }
    private static void Dispatch(in RenderGraphPackageRecording recording, IGpuComputePipeline pipeline, uint step, uint groups) {
        recording.Recorder.PushIndex(bindPoint: GpuBindPoint.Compute, commandBufferHandle: recording.CommandBuffer, index: step,
            pipelineLayoutHandle: pipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recording.Recorder.Dispatch(commandBufferHandle: recording.CommandBuffer, groupCountX: groups, groupCountY: 1, groupCountZ: 1);
    }
    // Writes, once per slot, tables and pair of buffers, a filler at every member the kernel does not read, then the
    // members it reads and writes.
    private void BindPorts(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables, IGpuBuffer sums, IGpuBuffer tiles, IGpuBuffer arguments) {
        var first = (m_clears ? sums : arguments);
        var second = tiles;
        var bound = m_ports[recording.Slot];

        if (ReferenceEquals(objA: bound.Tables, objB: tables) && ReferenceEquals(objA: bound.First, objB: first) && ReferenceEquals(objA: bound.Second, objB: second)) {
            return;
        }

        var bindings = m_context.Services.Bindings;
        var layout = SdfWorldInterfaces.GlassParameters.Layout;

        bindings.WriteSampledImage(arrayElement: 0, binding: m_sets.BindingOf(member: SdfWorldPackage.LitImage), descriptorSetHandle: set, imageViewHandle: tables.SampledFiller.ImageViewHandle);
        bindings.WriteSampledImage(arrayElement: 0, binding: m_sets.BindingOf(member: SdfWorldPackage.GlassVisibility), descriptorSetHandle: set, imageViewHandle: tables.SampledFiller.ImageViewHandle);
        bindings.WriteStorageImage(arrayElement: 0, binding: m_sets.BindingOf(member: SdfWorldPackage.Output), descriptorSetHandle: set, imageViewHandle: tables.StorageFiller.ImageViewHandle);
        foreach (var member in ShadingBuffers) {
            tables.WriteInterfaceBuffer(buffer: tables.DummyBuffer, layout: layout, member: member, set: set);
        }
        tables.WriteInterfaceBuffer(buffer: sums, layout: layout, member: SdfWorldPackage.GlassSumsClearWritten, set: set);
        tables.WriteInterfaceBuffer(buffer: tiles, layout: layout, member: SdfWorldPackage.GlassTilesWritten, set: set);
        tables.WriteInterfaceBuffer(buffer: arguments, layout: layout, member: SdfWorldPackage.GlassArgumentsWritten, set: set);
        m_ports[recording.Slot] = (tables, first, second);
    }
}
