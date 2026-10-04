using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The sky's field runs and the composite (SdfWorldPackage.Parts.Sky and Parts.Composite), which read the sky interface
// (SdfWorldInterfaces.SkyParameters). The sky runs on the render grid, reading the color views writes with the dispatch box
// it is current inside, in every fragment; the composite runs at the output extent, reading a native view's lit image with
// the visibility records and the box, or a reduced or temporal view's resolved lit image and surface transport. Both bind
// the residency's World set, whose tables hold the sky block and layer table, the volumes and the dynamic transforms, and a
// pass group binding each port at its member, the screens the sky's layers sample (a panorama's, a textured disc's), and
// a filler at every member the pass does not read or write. Their block holds
// the frame's common values, the temporal ones every part of a frame writes alike among them, and ResolvedSurface, which
// only a resolved composite sets; the kernels take the sky's direction without the jitter, so the sky never moves with a
// temporal view's samples. The composite writes the view's color, so it completes the render.
internal sealed class SdfSkyRecorder : IRenderGraphPackageRecorder {
    // The common frame values copy by name from the world layout, whose block the one frame writer lays out.
    private static readonly (int Source, int Destination, int Length)[] FrameCopies = [.. SdfWorldPackage.Values.Select(selector: static member => (
        Source: ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: member.Name)),
        Destination: ((int)SdfWorldInterfaces.SkyParameters.BlockOffsetOf(member: member.Name)),
        Length: checked((int)(member.Type!.Value.SizeBytes() * (member.Length ?? 1)))))];
    private static readonly int ResolvedSurfaceOffset = ((int)SdfWorldInterfaces.SkyParameters.BlockOffsetOf(member: SdfWorldPackage.ResolvedSurface));
    // The interface's image members a port reads through, and those a port writes through.
    private static readonly string[] SampledMembers = [SdfWorldPackage.LitImage, SdfWorldPackage.SkyBaseImage, .. SdfWorldPackage.SkyUpperImages];
    private static readonly string[] StorageMembers = [SdfWorldPackage.SkyBaseWritten, .. SdfWorldPackage.SkyUpperWritten, SdfWorldPackage.Output];
    private static readonly string[] ReadParts = [SdfWorldPackage.Parts.SkyUpper0, SdfWorldPackage.Parts.SkyUpper1, SdfWorldPackage.Parts.SkyUpper2];
    private static readonly string[] BufferMembers = [SdfWorldPackage.VisibilityRecords, SdfWorldPackage.CullBounds, SdfWorldPackage.TransportRead];

    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly RenderGraphFragmentPass m_fragmentPass;
    private readonly SdfWorldPasses m_owner;
    private readonly RenderGraphPackageSets m_sets;
    private readonly RenderGraphPackageWorkCounters m_work;
    private readonly SdfKernel m_kernel;
    private readonly bool m_resolved;
    private readonly bool m_temporal;

    private int m_recordedView;

    // Per frame slot, the tables the slot's pass set was written against.
    private readonly SdfWorldTables?[] m_portTables;
    // Per frame slot, the image each screen element of the pass set binds (zero before its first write), and which
    // screens the residency declares and the frame's sky samples.
    private readonly nint[][] m_screens;

    private readonly bool[] m_declaredScreens = new bool[SdfWorldTables.MaxScreenSurfaces];
    private readonly bool[] m_sampledScreens = new bool[SdfWorldTables.MaxScreenSurfaces];

    private SdfWorldView m_view;
    private bool m_disposed;

    internal SdfSkyRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, SdfWorldPasses owner, SdfWorldView view) {
        m_context = context;
        m_owner = owner;
        m_view = view;
        m_kernel = ((context.Part == SdfWorldPackage.Parts.Composite) ? SdfKernel.Composite : SdfKernel.Sky);

        var fragment = owner.FragmentOf(instance: context.Instance)!;

        m_resolved = fragment.Passes.Any(predicate: static pass => (pass.Name == SdfWorldPackage.Resolve));
        m_temporal = fragment.Resources.Any(predicate: static resource => resource.History);
        m_fragmentPass = fragment.Passes.Single(predicate: pass => string.Equals(a: pass.Name, b: context.Part, comparisonType: StringComparison.Ordinal));
        m_portTables = new SdfWorldTables?[context.InFlightFrames];
        m_screens = new nint[context.InFlightFrames][];
        for (var slot = 0; (slot < m_screens.Length); slot++) {
            m_screens[slot] = new nint[SdfWorldTables.MaxScreenSurfaces];
        }
        DeclareScreens(residency: view.Residency);
        m_sets = new RenderGraphPackageSets(context: context, groups: groups, groupLayoutHandles: view.Residency.Tables!.Pipeline(kernel: m_kernel).GroupLayoutHandles);
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
    // The sky's detail rows: its runs', then every layer label the composition's skies have packed, which only grow.
    public IReadOnlyList<string> WorkDetails(in FrameContext context) => m_view.Residency.SkyDetails.Labels;
    public ulong? Signature(in FrameContext context) => m_owner.SignatureOf(instance: m_context.Instance, part: m_context.Part!, temporal: m_temporal, context: in context);
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        if ((m_owner.ViewOf(instance: m_context.Instance) is { } current) && (current != m_view)) {
            if (!ReferenceEquals(objA: current.Residency, objB: m_view.Residency)) {
                current.Residency.Retain();
                m_owner.Hold(residency: current.Residency);
                m_owner.Unhold(residency: m_view.Residency);
                m_view.Residency.Release();
                DeclareScreens(residency: current.Residency);
            }
            m_view = current;
        }
        var residency = m_view.Residency;

        m_owner.Begin(residency: residency);
        var tables = residency.Submit(context: recording.Context);
        var frame = residency.Frame!;
        var index = Math.Min(val1: m_view.View, val2: (frame.Views.Count - 1));
        Span<byte> frameBlock = stackalloc byte[SdfFrameBlock.SizeBytes];

        frameBlock.Clear();
        SdfFrameBlock.Write(block: frameBlock, tables: tables.PassValues, frame: frame, view: index, width: recording.RenderWidth, height: recording.RenderHeight);
        var temporal = m_owner.SkyTemporalOf(instance: m_context.Instance);

        SdfFrameBlock.WriteTemporal(block: frameBlock, jitter: temporal.Jitter, historyFrames: temporal.Frames, temporal: m_temporal);
        SdfFrameBlock.WritePreviousView(block: frameBlock, view: temporal.PreviousView, valid: temporal.HasPreviousView);
        foreach (var copy in FrameCopies) {
            frameBlock.Slice(length: copy.Length, start: copy.Source).CopyTo(destination: recording.PassBlock.Slice(length: copy.Length, start: copy.Destination));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(destination: recording.PassBlock[ResolvedSurfaceOffset..], value: ((m_resolved && (m_kernel == SdfKernel.Composite)) ? 1u : 0u));
        var set = m_sets.PassSet(slot: recording.Slot);

        m_work.Write(passSet: set, recording: recording);
        BindPorts(recording: in recording, set: set, tables: tables);
        BindScreens(frame: frame, recording: in recording, set: set, tables: tables);
        var pipeline = tables.Pipeline(kernel: m_kernel);

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
        m_recordedView = index;
        return RenderGraphPackageOutcome.Drew;
    }
    public void Submitted() {
        if (m_kernel != SdfKernel.Composite) { return; }
        m_view.Residency.MarkRendered(view: m_recordedView);
        m_owner.MarkRendered(instance: m_context.Instance, view: in m_view);
    }

    // Writes, once per slot and tables, a filler at every member, then each port at the member its version reads or
    // writes it through. A slot's ports stay the same storages for the recorder's life.
    private void BindPorts(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables) {
        var slot = recording.Slot;

        if (ReferenceEquals(objA: m_portTables[slot], objB: tables)) {
            return;
        }

        var bindings = m_context.Services.Bindings;
        var layout = SdfWorldInterfaces.SkyParameters.Layout;

        foreach (var member in SampledMembers) {
            bindings.WriteSampledImage(arrayElement: 0, binding: m_sets.BindingOf(member: member), descriptorSetHandle: set, imageViewHandle: tables.SampledFiller.ImageViewHandle);
        }
        foreach (var member in StorageMembers) {
            bindings.WriteStorageImage(arrayElement: 0, binding: m_sets.BindingOf(member: member), descriptorSetHandle: set, imageViewHandle: tables.StorageFiller.ImageViewHandle);
        }
        foreach (var member in BufferMembers) {
            tables.WriteInterfaceBuffer(buffer: tables.DummyBuffer, layout: layout, member: member, set: set);
        }
        for (var port = 0; (port < m_fragmentPass.Inputs.Count); port++) {
            var member = ReadMemberOf(version: m_fragmentPass.Inputs[port].Name);
            var bound = recording.Inputs[port];

            if (bound.Buffer is { } buffer) {
                tables.WriteInterfaceBuffer(buffer: buffer, layout: layout, member: member, set: set);
            } else {
                bindings.WriteSampledImage(arrayElement: 0, binding: m_sets.BindingOf(member: member), descriptorSetHandle: set, imageViewHandle: bound.Image.ImageViewHandle);
            }
        }
        for (var port = 0; (port < m_fragmentPass.Outputs.Count); port++) {
            bindings.WriteStorageImage(arrayElement: 0, binding: m_sets.BindingOf(member: WrittenMemberOf(version: m_fragmentPass.Outputs[port].Name)), descriptorSetHandle: set, imageViewHandle: recording.Outputs[port].Image.ImageViewHandle);
        }
        Array.Clear(array: m_screens[slot]);
        m_portTables[slot] = tables;
    }
    // Notes the screens a residency declares, which alone may bind a source.
    private void DeclareScreens(SdfWorldResidency residency) {
        Array.Clear(array: m_declaredScreens);

        if (residency.ScreenSources is { } sources) {
            foreach (var screen in sources.Screens) {
                m_declaredScreens[screen] = true;
            }
        }
    }
    // Writes each screen the frame's sky samples into the slot's pass set every frame, since a host's image handle is
    // unique only among live objects, and the filler once at every other screen. The image is the one the views pass
    // binds (SdfWorldResidency.ScreenImage), under the lease it already holds.
    private void BindScreens(SdfFrame frame, in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables) {
        var bound = m_screens[recording.Slot];
        var filler = tables.SampledFiller.ImageViewHandle;
        var residency = m_view.Residency;
        var sky = frame.Sky;

        Array.Clear(array: m_sampledScreens);
        for (var layer = 0; (layer < sky.LayerCount); layer++) {
            var screen = sky.LayerAt(index: layer).Kind switch {
                SdfSkyLayerKind.Panorama => sky.Parameters<SdfSkyPanorama>(index: layer).Screen,
                SdfSkyLayerKind.Disc => sky.Parameters<SdfSkyDisc>(index: layer).Screen,
                SdfSkyLayerKind.View => sky.Parameters<SdfSkyView>(index: layer).Screen,
                _ => -1,
            };

            if ((screen >= 0) && (screen < SdfWorldTables.MaxScreenSurfaces)) {
                m_sampledScreens[screen] = true;
            }
        }
        for (var screen = 0; (screen < SdfWorldTables.MaxScreenSurfaces); screen++) {
            var image = ((m_sampledScreens[screen] && m_declaredScreens[screen])
                ? residency.ScreenImage(
                    leases: recording.Leases,
                    reads: recording.Reads,
                    screen: screen,
                    view: m_view.View
                )
                : 0);

            if (image == 0) {
                if (bound[screen] == filler) {
                    continue;
                }

                image = filler;
            }

            m_context.Services.Bindings.WriteSampledImage(
                arrayElement: ((uint)screen),
                binding: m_sets.BindingOf(member: SdfWorldPackage.ScreenSources),
                descriptorSetHandle: set,
                imageViewHandle: image
            );
            bound[screen] = image;
        }
    }
    // The member a pass reads a fragment version through.
    private static string ReadMemberOf(string version) => version switch {
        SdfWorldPackage.Parts.Lit or SdfWorldPackage.CurrentColor => SdfWorldPackage.LitImage,
        SdfWorldPackage.Parts.CullBounds => SdfWorldPackage.CullBounds,
        SdfWorldPackage.Parts.ShadowVisibility => SdfWorldPackage.VisibilityRecords,
        SdfWorldPackage.Parts.Transport => SdfWorldPackage.TransportRead,
        SdfWorldPackage.Parts.SkyBase => SdfWorldPackage.SkyBaseImage,
        SdfWorldPackage.Parts.SkyUpper0 or SdfWorldPackage.Parts.SkyUpper1 or SdfWorldPackage.Parts.SkyUpper2 => SdfWorldPackage.SkyUpperImages[Array.IndexOf(array: ReadParts, value: version)],
        _ => throw new InvalidOperationException(message: $"The sky interface reads no version '{version}'."),
    };
    // The member a pass writes a fragment version through.
    private static string WrittenMemberOf(string version) => version switch {
        SdfWorldPackage.Parts.SkyBase => SdfWorldPackage.SkyBaseWritten,
        SdfWorldPackage.Parts.SkyUpper0 or SdfWorldPackage.Parts.SkyUpper1 or SdfWorldPackage.Parts.SkyUpper2 => SdfWorldPackage.SkyUpperWritten[Array.IndexOf(array: ReadParts, value: version)],
        SdfWorldPackage.Color => SdfWorldPackage.Output,
        _ => throw new InvalidOperationException(message: $"The sky interface writes no version '{version}'."),
    };
}
