using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// Reconstruction uses the residency's optional reloadable pipeline: native views allocate none of its GPU resources.
// The pass binds the residency's World set, whose tables sdfReprojection reads, and its pass group: the render-grid
// color, the visibility records and the dispatch box it reads the grid through, the lit image and the surface transport it
// writes, and, in the temporal fragment, the reactivity and the history of the preceding frame and of this one. A spatial
// resolve binds the tables' fillers at the temporal members. The sky and the composite follow it, so it leaves the render
// to the composite to complete.
internal sealed class SdfResolveRecorder : IRenderGraphPackageRecorder {
    // Resolve-only values may shift common members. Compile the name-based copies once; native pass blocks retain
    // their established layout, and the one frame writer remains authoritative for common values.
    private static readonly (int Source, int Destination, int Length)[] FrameCopies = [.. SdfWorldPackage.Values.Select(selector: static member => (
        Source: ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: member.Name)),
        Destination: ((int)SdfWorldInterfaces.ResolveParameters.BlockOffsetOf(member: member.Name)),
        Length: checked((int)(member.Type!.Value.SizeBytes() * (member.Length ?? 1)))))];
    // The resolve's buffer inputs after the render-grid color in either mode, by the member each binds at, in port order.
    private static readonly string[] Inputs = [
        SdfWorldPackage.VisibilityRecords,
        SdfWorldPackage.CullBounds,
    ];
    // The temporal fragment's resolve inputs after those, by the member each binds at, in port order.
    private static readonly string[] TemporalInputs = [
        SdfWorldPackage.Reactivity,
        SdfWorldPackage.HistoryColor,
        SdfWorldPackage.HistorySurface,
    ];
    // The temporal fragment's resolve outputs after the lit image and the surface transport, by the member each binds at,
    // in port order.
    private static readonly string[] TemporalOutputs = [
        SdfWorldPackage.HistoryColorWritten,
        SdfWorldPackage.HistorySurfaceWritten,
    ];

    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly SdfWorldPasses m_owner;
    private readonly RenderGraphPackageSets m_sets;
    private readonly RenderGraphPackageWorkCounters m_work;
    private readonly bool m_temporal;
    // Per frame slot, the tables the slot's pass set was written against.
    private readonly SdfWorldTables?[] m_portTables;

    private SdfWorldView m_view;
    private bool m_disposed;

    internal SdfResolveRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, SdfWorldPasses owner, SdfWorldView view) {
        m_context = context;
        m_owner = owner;
        m_view = view;
        m_temporal = ReferenceEquals(objA: owner.FragmentOf(instance: context.Instance), objB: SdfWorldPackage.TemporalFragment);
        m_portTables = new SdfWorldTables?[context.InFlightFrames];
        m_sets = new RenderGraphPackageSets(context: context, groups: groups, groupLayoutHandles: view.Residency.Tables!.Pipeline(kernel: SdfKernel.Resolve).GroupLayoutHandles);
        m_work = new RenderGraphPackageWorkCounters(context: context, sets: m_sets);
    }

    public void Dispose() {
        if (m_disposed) {
            return;
        }
        m_disposed = true;
        m_owner.Unhold(residency: m_view.Residency);
        m_view.Residency.Release();
    }
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        if ((m_owner.ViewOf(instance: m_context.Instance) is { } current) && (current != m_view)) {
            if (!ReferenceEquals(objA: current.Residency, objB: m_view.Residency)) {
                current.Residency.Retain();
                m_owner.Hold(residency: current.Residency);
                m_owner.Unhold(residency: m_view.Residency);
                m_view.Residency.Release();
            }
            m_view = current;
        }
        var residency = m_view.Residency;

        m_owner.Begin(residency: residency);
        var tables = residency.Submit(context: recording.Context);
        var frame = residency.Frame!;
        var index = Math.Min(val1: m_view.View, val2: (frame.Views.Count - 1));
        var render = (Width: recording.RenderWidth, Height: recording.RenderHeight);
        Span<byte> frameBlock = stackalloc byte[SdfFrameBlock.SizeBytes];

        frameBlock.Clear();
        SdfFrameBlock.Write(block: frameBlock, tables: tables.PassValues, frame: frame, view: index, width: render.Width, height: render.Height);
        var temporal = m_owner.TemporalOf(instance: m_context.Instance, view: m_view, width: recording.FrameWidth, height: recording.FrameHeight, debug: tables.PassValues.DebugMode, temporal: m_temporal, renderWidth: render.Width, renderHeight: render.Height);

        SdfFrameBlock.WriteTemporal(block: frameBlock, jitter: temporal.Jitter, historyFrames: temporal.Frames, temporal: m_temporal);
        SdfFrameBlock.WritePreviousView(block: frameBlock, view: temporal.PreviousView, valid: temporal.HasPreviousView);
        foreach (var copy in FrameCopies) {
            frameBlock.Slice(length: copy.Length, start: copy.Source).CopyTo(destination: recording.PassBlock.Slice(length: copy.Length, start: copy.Destination));
        }
        BinaryPrimitives.WriteSingleLittleEndian(destination: recording.PassBlock[((int)SdfWorldInterfaces.ResolveParameters.BlockOffsetOf(member: SdfWorldPackage.UpscaleSharpness))..], value: frame.Views[index].UpscaleSharpness);
        var set = m_sets.PassSet(slot: recording.Slot);

        m_work.Write(passSet: set, recording: recording);
        BindPorts(recording: in recording, set: set, tables: tables);
        var pipeline = tables.Pipeline(kernel: SdfKernel.Resolve);

        recording.Recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: recording.CommandBuffer, pipelineHandle: pipeline.Handle);
        m_sets.Bind(bindPoint: GpuBindPoint.Compute, commandBuffer: recording.CommandBuffer, pipelineLayout: pipeline.LayoutHandle, recorder: recording.Recorder, slot: recording.Slot);
        recording.Recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: recording.CommandBuffer,
            descriptorSetHandle: tables.WorldSet(slot: tables.CurrentSlot),
            group: ((uint)ShaderInterfaceGroup.World),
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
        recording.Recorder.Dispatch(commandBufferHandle: recording.CommandBuffer, groupCountX: ((recording.Width + 7) / 8), groupCountY: ((recording.Height + 7) / 8), groupCountZ: 1);
        return RenderGraphPackageOutcome.Drew;
    }

    // Writes, once per slot and tables, every port at its member, and the tables' fillers at each temporal member a
    // spatial resolve leaves unbound. A slot's ports, the history of the slot before it included, stay the same storages
    // for the recorder's life.
    private void BindPorts(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables) {
        var slot = recording.Slot;

        if (ReferenceEquals(objA: m_portTables[slot], objB: tables)) {
            return;
        }

        var bindings = m_context.Services.Bindings;

        bindings.WriteSampledImage(arrayElement: 0, binding: m_sets.BindingOf(member: SdfWorldPackage.CurrentColor), descriptorSetHandle: set, imageViewHandle: recording.Inputs[0].Image.ImageViewHandle);
        bindings.WriteStorageImage(arrayElement: 0, binding: m_sets.BindingOf(member: SdfWorldPackage.Output), descriptorSetHandle: set, imageViewHandle: recording.Outputs[0].Image.ImageViewHandle);
        tables.WriteInterfaceBuffer(buffer: recording.Outputs[1].Buffer!, layout: SdfWorldInterfaces.ResolveParameters.Layout, member: SdfWorldPackage.TransportWritten, set: set);
        for (var port = 0; (port < Inputs.Length); port++) {
            tables.WriteInterfaceBuffer(buffer: recording.Inputs[(port + 1)].Buffer!, layout: SdfWorldInterfaces.ResolveParameters.Layout, member: Inputs[port], set: set);
        }
        for (var port = 0; (port < TemporalInputs.Length); port++) {
            var member = TemporalInputs[port];
            var bound = (m_temporal ? recording.Inputs[((port + 1) + Inputs.Length)] : default);

            if (member == SdfWorldPackage.HistoryColor) {
                bindings.WriteSampledImage(arrayElement: 0, binding: m_sets.BindingOf(member: member), descriptorSetHandle: set,
                    imageViewHandle: (m_temporal ? bound.Image.ImageViewHandle : tables.SampledFiller.ImageViewHandle));
            } else {
                tables.WriteInterfaceBuffer(buffer: ((m_temporal ? bound.Buffer : null) ?? tables.DummyBuffer), layout: SdfWorldInterfaces.ResolveParameters.Layout, member: member, set: set);
            }
        }
        for (var port = 0; (port < TemporalOutputs.Length); port++) {
            var member = TemporalOutputs[port];
            var bound = (m_temporal ? recording.Outputs[(port + 2)] : default);

            if (member == SdfWorldPackage.HistoryColorWritten) {
                bindings.WriteStorageImage(arrayElement: 0, binding: m_sets.BindingOf(member: member), descriptorSetHandle: set,
                    imageViewHandle: (m_temporal ? bound.Image.ImageViewHandle : tables.StorageFiller.ImageViewHandle));
            } else {
                tables.WriteInterfaceBuffer(buffer: ((m_temporal ? bound.Buffer : null) ?? tables.DummyBuffer), layout: SdfWorldInterfaces.ResolveParameters.Layout, member: member, set: set);
            }
        }
        m_portTables[slot] = tables;
    }
}
