using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Maths;

namespace Puck.SdfVm;

// Reconstruction uses the residency's optional reloadable pipeline: native views allocate none of its GPU resources.
// The pass binds the residency's World set, whose tables sdfReprojection reads, and its pass group: the render-grid
// color, the visibility records it reads the grid through, the lit image and the surface transport it
// writes, and, in the temporal fragment, the reactivity, the history of the preceding frame and of this one, and the
// stationary window it updates in place. A spatial
// resolve binds the tables' fillers at the temporal members. The sky and the composite follow it, so it leaves the render
// to the composite to complete.
internal sealed class SdfResolveRecorder : SdfViewRecorder, IRenderGraphPackageRecorder {
    // Resolve-only values may shift common members. Compile the name-based copies once; native pass blocks retain
    // their established layout, and the one frame writer remains authoritative for common values.
    private static readonly SdfFrameBlock.Copy[] FrameCopies = SdfFrameBlock.CopiesInto(layout: SdfWorldInterfaces.ResolveParameters);
    // The resolve's buffer inputs after the render-grid color in either mode, by the member each binds at, in port order.
    private static readonly string[] Inputs = [
        SdfWorldPackage.VisibilityRecords,
        SdfWorldPackage.SampleAir,
    ];
    // The temporal fragment's resolve inputs after those, by the member each binds at, in port order. The stationary
    // window is an input the resolve updates in place, bound read-write.
    private static readonly string[] TemporalInputs = [
        SdfWorldPackage.Reactivity,
        SdfWorldPackage.HistoryColor,
        SdfWorldPackage.HistorySurface,
        SdfWorldPackage.HistoryWindowWritten,
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
    // Per frame slot, the tables the slot's pass set was written against.
    private readonly SdfWorldTables?[] m_portTables;
    private readonly (nint Lit, nint ColorRead, nint SurfaceRead, nint ColorWrite, nint SurfaceWrite, nint Window)[] m_portHistory;

    private bool m_disposed;

    internal SdfResolveRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, SdfWorldPasses owner, SdfWorldView view) : base(owner: owner, instance: context.Instance, view: view) {
        m_context = context;
        m_owner = owner;
        m_portTables = new SdfWorldTables?[context.InFlightFrames];
        m_portHistory = new (nint, nint, nint, nint, nint, nint)[context.InFlightFrames];
        m_sets = new RenderGraphPackageSets(context: context, groups: groups, groupLayoutHandles: view.Residency.Tables!.Pipeline(kernel: SdfKernel.Resolve).GroupLayoutHandles);
        m_work = new RenderGraphPackageWorkCounters(context: context, sets: m_sets);
    }

    public void Dispose() {
        if (m_disposed) {
            return;
        }
        m_disposed = true;
        LeaveView();
    }
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        var residency = m_view.Residency;
        var tables = residency.Staged(context: recording.Context);
        var frame = residency.Frame!;
        var index = Math.Min(val1: m_view.View, val2: (frame.Views.Count - 1));
        var render = (Width: recording.RenderWidth, Height: recording.RenderHeight);

        m_owner.RecordHits(instance: m_context.Instance, part: SdfWorldPackage.Resolve, tables: tables);
        var temporal = m_owner.TemporalOf(instance: m_context.Instance, view: m_view, width: recording.FrameWidth, height: recording.FrameHeight, debug: tables.PassValues.DebugMode, temporal: m_temporal, unread: recording.UnreadFrames, renderWidth: render.Width, renderHeight: render.Height);

        SdfFrameBlock.WriteInto(passBlock: recording.PassBlock, copies: FrameCopies, tables: tables.PassValues, frame: frame, view: index,
            width: render.Width, height: render.Height, temporal: temporal, temporalEnabled: m_temporal, residency: residency,
            lightMask: tables.LightMaskOf(frame: frame, view: index));
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
        recording.Recorder.Dispatch(commandBufferHandle: recording.CommandBuffer, groupCountX: recording.Width.CeilingDivide(divisor: 8u), groupCountY: recording.Height.CeilingDivide(divisor: 8u), groupCountZ: 1);
        return RenderGraphPackageOutcome.Drew;
    }
    public ulong? Signature(in Puck.Hosting.FrameContext context, Puck.Hosting.RenderGraphExternalReads? reads) => m_owner.SignatureOf(instance: m_context.Instance, part: SdfWorldPackage.Resolve, temporal: m_temporal, context: in context, reads: reads);
    public void Submitted() => m_owner.MarkSampleRendered(instance: m_context.Instance);

    // Ordinary ports follow the slot. History follows its writer, so refresh cached bindings whenever the resolved
    // history handles change, even on a slot whose tables stay the same. The color input follows the glass shading pass's
    // output, which stands for the opaque image while no glass is in view, so its handle is part of the key too.
    private void BindPorts(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables) {
        var slot = recording.Slot;
        var lit = recording.Inputs[0].ReadImage(latch: recording.Latch).ImageViewHandle;
        var history = (m_temporal
            ? (lit, recording.Inputs[(Inputs.Length + 2)].ReadImage(latch: recording.Latch).ImageViewHandle, recording.Inputs[(Inputs.Length + 3)].Buffer!.BufferHandle,
                recording.Outputs[2].ReadImage(latch: recording.Latch).ImageViewHandle, recording.Outputs[3].Buffer!.BufferHandle, recording.Inputs[(Inputs.Length + 4)].Buffer!.BufferHandle)
            : (lit, (nint)0, (nint)0, (nint)0, (nint)0, (nint)0));

        if (ReferenceEquals(objA: m_portTables[slot], objB: tables) && (m_portHistory[slot] == history)) {
            return;
        }

        var bindings = m_context.Services.Bindings;

        bindings.WriteSampledImage(arrayElement: 0, binding: m_sets.BindingOf(member: SdfWorldPackage.CurrentColor), descriptorSetHandle: set, imageViewHandle: lit);
        bindings.WriteStorageImage(arrayElement: 0, binding: m_sets.BindingOf(member: SdfWorldPackage.Output), descriptorSetHandle: set, imageViewHandle: recording.Outputs[0].ReadImage(latch: recording.Latch).ImageViewHandle);
        tables.WriteInterfaceBuffer(buffer: recording.Outputs[1].Buffer!, layout: SdfWorldInterfaces.ResolveParameters.Layout, member: SdfWorldPackage.TransportWritten, set: set);
        for (var port = 0; (port < Inputs.Length); port++) {
            tables.WriteInterfaceBuffer(buffer: recording.Inputs[(port + 1)].Buffer!, layout: SdfWorldInterfaces.ResolveParameters.Layout, member: Inputs[port], set: set);
        }
        for (var port = 0; (port < TemporalInputs.Length); port++) {
            var member = TemporalInputs[port];
            var bound = (m_temporal ? recording.Inputs[((port + 1) + Inputs.Length)] : default);

            if (member == SdfWorldPackage.HistoryColor) {
                bindings.WriteSampledImage(arrayElement: 0, binding: m_sets.BindingOf(member: member), descriptorSetHandle: set,
                    imageViewHandle: (m_temporal ? bound.ReadImage(latch: recording.Latch).ImageViewHandle : tables.SampledFiller.ImageViewHandle));
            } else {
                tables.WriteInterfaceBuffer(buffer: ((m_temporal ? bound.Buffer : null) ?? tables.DummyBuffer), layout: SdfWorldInterfaces.ResolveParameters.Layout, member: member, set: set);
            }
        }
        for (var port = 0; (port < TemporalOutputs.Length); port++) {
            var member = TemporalOutputs[port];
            var bound = (m_temporal ? recording.Outputs[(port + 2)] : default);

            if (member == SdfWorldPackage.HistoryColorWritten) {
                bindings.WriteStorageImage(arrayElement: 0, binding: m_sets.BindingOf(member: member), descriptorSetHandle: set,
                    imageViewHandle: (m_temporal ? bound.ReadImage(latch: recording.Latch).ImageViewHandle : tables.StorageFiller.ImageViewHandle));
            } else {
                tables.WriteInterfaceBuffer(buffer: ((m_temporal ? bound.Buffer : null) ?? tables.DummyBuffer), layout: SdfWorldInterfaces.ResolveParameters.Layout, member: member, set: set);
            }
        }
        m_portTables[slot] = tables;
        m_portHistory[slot] = history;
    }
}
