using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

// The glass shading pass (SdfWorldPackage.Parts.GlassShade), which reads the glass interface
// (SdfWorldInterfaces.GlassParameters): one invocation a pixel of the render grid, writing the lit image views wrote over
// again with each glass pixel shaded over the opaque image behind it. It binds the residency's World set, whose tables
// hold the material table, the lights and the sky's environment map, and a pass group binding the lit image and the glass
// target as sampled images, the visibility records and the glass sums as buffers, the reactivity a temporal view's
// resolve reads (the tables' dummy in any other view) and the image it writes. Its block holds the frame's common
// values, copied by name from the world layout, the temporal ones every part of a frame writes alike among them.
internal sealed class SdfGlassRecorder : IRenderGraphPackageRecorder, IRenderGraphPackageReadback {
    private static readonly SdfFrameBlock.Copy[] FrameCopies = SdfFrameBlock.CopiesInto(layout: SdfWorldInterfaces.GlassParameters);
    // The pass's inputs by position: the lit image, the visibility records, the glass target and the glass sums, then
    // the environment's buffers the graph orders the pass after, which the World set already binds.
    private const int LitInput = 0;
    private const int VisibilityInput = 1;
    private const int GlassTargetInput = 2;
    private const int GlassSumsInput = 3;

    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly SdfWorldPasses m_owner;
    private readonly RenderGraphPackageSets m_sets;
    private readonly RenderGraphPackageWorkCounters m_work;
    private readonly bool m_temporal;
    // Per frame slot, the tables the slot's pass set was written against.
    private readonly SdfWorldTables?[] m_portTables;

    private SdfWorldView m_view;
    private bool m_disposed;
    private SdfShadeDispatch? m_shadePlan;
    private readonly SdfShadeSlots m_shade;

    internal SdfGlassRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, SdfWorldPasses owner, SdfWorldView view) {
        m_context = context;
        m_shade = new SdfShadeSlots(slots: context.InFlightFrames);
        m_owner = owner;
        m_view = view;
        m_temporal = owner.FragmentOf(instance: context.Instance)!.Resources.Any(predicate: static resource => resource.History);
        m_portTables = new SdfWorldTables?[context.InFlightFrames];
        m_sets = new RenderGraphPackageSets(context: context, groups: groups, groupLayoutHandles: view.Residency.Tables!.Pipeline(kernel: SdfKernel.Glass).GroupLayoutHandles);
        m_work = new RenderGraphPackageWorkCounters(context: context, sets: m_sets);
    }

    public void Dispose() {
        if (m_disposed) {
            return;
        }
        m_disposed = true;
        m_shade.Release(releases: m_owner.ShadeReleases);
        m_owner.Leave(view: m_view);
    }
    public ulong? Signature(in FrameContext context, RenderGraphExternalReads? reads) =>
        m_owner.SignatureOf(instance: m_context.Instance, part: SdfWorldPackage.Parts.GlassShade, temporal: m_temporal, context: in context);
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        _ = m_owner.Follow(instance: m_context.Instance, view: ref m_view);
        var residency = m_view.Residency;

        m_owner.Begin(residency: residency);
        var tables = residency.Staged(context: recording.Context);
        var frame = residency.Frame!;
        var index = Math.Min(val1: m_view.View, val2: (frame.Views.Count - 1));

        SdfFrameBlock.WriteInto(passBlock: recording.PassBlock, copies: FrameCopies, tables: tables.PassValues, frame: frame, view: index,
            width: recording.RenderWidth, height: recording.RenderHeight, temporal: m_owner.SkyTemporalOf(instance: m_context.Instance), temporalEnabled: m_temporal);
        var set = m_sets.PassSet(slot: recording.Slot);

        m_work.Write(passSet: set, recording: recording);
        BindPorts(recording: in recording, set: set, tables: tables);
        var pipeline = tables.Pipeline(kernel: SdfKernel.Glass);

        recording.Recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: recording.CommandBuffer, pipelineHandle: pipeline.Handle);
        m_sets.Bind(bindPoint: GpuBindPoint.Compute, commandBuffer: recording.CommandBuffer, pipelineLayout: pipeline.LayoutHandle, recorder: recording.Recorder, slot: recording.Slot);
        recording.Recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: recording.CommandBuffer,
            descriptorSetHandle: tables.WorldSet(slot: tables.CurrentSlot),
            group: ((uint)ShaderInterfaceGroup.World),
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
        m_shadePlan = SdfShadeDispatch.For(m_shadePlan, frame, recording.Width, recording.Height,
            tables.PassValues.ScreenCount, glass: true);
        m_shadePlan.Record(in recording, pipeline);
        m_shade.Recorded(slot: recording.Slot, residency: residency, plan: m_shadePlan);
        return RenderGraphPackageOutcome.Drew;
    }
    public void Submitted() {
    }
    public ulong ReadbackBytes => 0;
    public bool TryReadback(int slot, int index, out RenderGraphBufferReadback readback) { readback = default; return false; }
    public void Submitted(int slot) => m_shade.Submitted(slot: slot);
    public void Completed(int slot) => m_shade.Completed(slot: slot);

    // Writes, once per slot and tables, each port at the member its version reads or writes it through, and the tables'
    // dummy at the reactivity of a view that resolves nothing over time. A slot's ports stay the same storages for the
    // recorder's life.
    private void BindPorts(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables) {
        var slot = recording.Slot;

        if (ReferenceEquals(objA: m_portTables[slot], objB: tables)) {
            return;
        }

        var bindings = m_context.Services.Bindings;
        var layout = SdfWorldInterfaces.GlassParameters.Layout;

        bindings.WriteSampledImage(arrayElement: 0, binding: m_sets.BindingOf(member: SdfWorldPackage.LitImage), descriptorSetHandle: set, imageViewHandle: recording.Inputs[LitInput].Image.ImageViewHandle);
        tables.WriteInterfaceBuffer(buffer: recording.Inputs[VisibilityInput].Buffer!, layout: layout, member: SdfWorldPackage.VisibilityRecords, set: set);
        bindings.WriteSampledImage(arrayElement: 0, binding: m_sets.BindingOf(member: SdfWorldPackage.GlassVisibility), descriptorSetHandle: set, imageViewHandle: recording.Inputs[GlassTargetInput].Image.ImageViewHandle);
        tables.WriteInterfaceBuffer(buffer: recording.Inputs[GlassSumsInput].Buffer!, layout: layout, member: SdfWorldPackage.GlassSums, set: set);
        bindings.WriteStorageImage(arrayElement: 0, binding: m_sets.BindingOf(member: SdfWorldPackage.Output), descriptorSetHandle: set, imageViewHandle: recording.Outputs[0].Image.ImageViewHandle);
        tables.WriteInterfaceBuffer(buffer: ((m_temporal ? recording.Outputs[1].Buffer : null) ?? tables.DummyBuffer), layout: layout, member: SdfWorldPackage.ReactivityWritten, set: set);
        m_portTables[slot] = tables;
    }
}
