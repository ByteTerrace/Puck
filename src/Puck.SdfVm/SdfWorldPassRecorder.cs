using System.Buffers.Binary;
using Puck.Hosting;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// One pass of an sdf.world instance (SdfWorldPasses): a part of the package's fragment, recorded into the instance's
// command buffer for the pass. Every part writes its pass block (SdfFrameBlock): the view's camera, the frame's levers and
// environment and the world values. Every compute part binds the residency's World set of the ring slot the frame's upload
// wrote, which holds its tables, and the world interface's pass group: the fragment storages its ports bind and, at every
// member its ports do not, a dummy of the residency's; and the screens, whose host images are rewritten every frame. The mesh part draws the frame's
// mesh draws into its target through the mesh pipeline, with a set of its own per frame slot binding its pass block. A
// recorder records no barrier: the planner's are the instance's.
internal sealed class SdfWorldPassRecorder : IRenderGraphPackageRecorder {
    private const uint WorkgroupEdge = 8;

    // The world interface's scratch buffer members, each bound to the dummy unless a port binds it.
    private static readonly string[] ScratchMembers = [
        SdfWorldPackage.InstanceMasks,
        SdfWorldPackage.InstanceMasksWritten,
        SdfWorldPackage.Tiles,
        SdfWorldPackage.TilesWritten,
        SdfWorldPackage.CullBounds,
        SdfWorldPackage.CullBoundsWritten,
        SdfWorldPackage.ViewsArgsWritten,
        SdfWorldPackage.VisibilityRecords,
        SdfWorldPackage.VisibilityRecordsWritten,
    ];
    private static readonly uint OutputBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.Output);
    private static readonly uint MeshVisibilityBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshVisibility);
    private static readonly uint ScreenSourcesBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.ScreenSources);

    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly RenderGraphFragmentPass m_fragmentPass;
    private readonly SdfWorldPasses m_owner;
    private readonly string m_part;
    private readonly SdfWorldView m_view;

    // Which screen indices the residency binds.
    private readonly bool[] m_declaredScreens = new bool[SdfWorldTables.MaxScreenSurfaces];

    // A compute part's frame and pass sets, one of each per frame slot.
    private readonly RenderGraphPackageSets? m_sets;
    // Per frame slot: the tables the pass set's ports and dummies were written for, and each screen element's last written
    // view.
    private readonly SdfWorldTables?[] m_portTables;
    private readonly nint[][] m_screens;
    // The mesh part's pool, its set per frame slot, and a framebuffer over each instance of its target and depth.
    private readonly nint m_meshPool;

    private readonly nint[] m_meshSets = [];
    private readonly IReadOnlyList<IGpuImage> m_meshTargets = [];
    private readonly IGpuFramebuffer[] m_framebuffers = [];
    private readonly byte[] m_meshPushedIndex = new byte[GpuPipelineLayoutDescription.PushIndexBytes];

    private bool m_disposed;

    public SdfWorldPassRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, SdfWorldPasses owner, SdfWorldView view) {
        m_context = context;
        m_owner = owner;
        m_view = view;
        m_part = (context.Part ?? throw new ArgumentException(message: $"Pass '{context.Pass}' runs no part of '{RenderGraphPackageCatalog.SdfWorld}'.", paramName: nameof(context)));
        m_fragmentPass = SdfWorldPackage.Fragment.Passes.Single(predicate: pass => string.Equals(
            a: pass.Name,
            b: m_part,
            comparisonType: StringComparison.Ordinal
        ));

        if (view.Residency.ScreenSources is { } sources) {
            foreach (var screen in sources.Screens) {
                m_declaredScreens[screen] = true;
            }
        }

        var slots = context.InFlightFrames;
        var tables = (view.Residency.Tables ?? throw new InvalidOperationException(message: $"Residency '{view.Residency.Name}' has no tables for pass '{context.Pass}'."));

        m_portTables = new SdfWorldTables?[slots];
        m_screens = new nint[slots][];

        for (var slot = 0; (slot < slots); slot++) {
            m_screens[slot] = new nint[SdfWorldTables.MaxScreenSurfaces];
        }

        if (!IsMesh) {
            m_sets = new RenderGraphPackageSets(
                context: context,
                groupLayoutHandles: tables.Pipeline(index: SdfWorldTables.BeamPipelineIndex).GroupLayoutHandles,
                groups: groups
            );

            return;
        }

        // The mesh part draws through the mesh interface's layout, whose one pass group holds the pass block the world
        // interface lays out and the mesh region: a set per frame slot from a pool of its own, its block bound to the slot's
        // pass block buffer, and a framebuffer over each instance of its target and depth.
        var bindings = context.Services.Bindings;
        var sizes = default(GpuDescriptorPoolSizes);

        for (var slot = 0; (slot < slots); slot++) {
            sizes += GpuDescriptorPoolSizes.ForGroups(groups: SdfWorldTables.PipelineLayouts.Mesh.Groups);
        }

        m_meshPool = bindings.CreatePool(
            name: new GpuObjectName(
                owner: context.Instance,
                part: context.Pass
            ),
            sizes: sizes
        );

        try {
            m_meshSets = new nint[slots];

            for (var slot = 0; (slot < slots); slot++) {
                m_meshSets[slot] = bindings.AllocateSet(
                    descriptorSetLayoutHandle: tables.MeshPipeline.GroupLayoutHandles[((int)ShaderInterfaceGroup.Pass)],
                    name: new GpuObjectName(
                        detail: "mesh group",
                        index: slot,
                        owner: context.Instance,
                        part: context.Pass
                    ),
                    poolHandle: m_meshPool
                );
                bindings.WriteConstantBuffer(
                    arrayElement: 0,
                    binding: 0,
                    bufferHandle: groups.PassBlocks[slot].BufferHandle,
                    bufferSize: groups.PassBlocks[slot].SizeBytes,
                    descriptorSetHandle: m_meshSets[slot]
                );
            }

            m_meshTargets = groups.OutputImages[0];
            m_framebuffers = new IGpuFramebuffer[m_meshTargets.Count];

            for (var index = 0; (index < m_framebuffers.Length); index++) {
                m_framebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(
                    colors: [m_meshTargets[index]],
                    depth: groups.OutputImages[1][index],
                    renderPass: tables.MeshRenderPass
                );
            }
        } catch {
            foreach (var framebuffer in m_framebuffers) {
                framebuffer?.Dispose();
            }

            bindings.DestroyPool(poolHandle: m_meshPool);

            throw;
        }
    }

    private bool IsMesh => string.Equals(
        a: m_part,
        b: SdfWorldPackage.Parts.Mesh,
        comparisonType: StringComparison.Ordinal
    );

    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        foreach (var framebuffer in m_framebuffers) {
            framebuffer.Dispose();
        }

        if (m_meshPool != 0) {
            m_context.Services.Bindings.DestroyPool(poolHandle: m_meshPool);
        }

        m_owner.Unhold(residency: m_view.Residency);
        m_view.Residency.Release();
    }
    // The mesh part skips every frame that draws no mesh: it records neither its draws nor the barriers of its target and
    // depth, and the hit passes, whose pass block's mesh draws are then zero, read nothing of the target.
    public bool Skips(in FrameContext context) {
        if (!IsMesh) {
            return false;
        }

        var residency = m_view.Residency;

        m_owner.Begin(residency: residency);

        return (residency.Submit(context: in context).MeshDrawCount == 0);
    }
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        var residency = m_view.Residency;

        m_owner.Begin(residency: residency);

        var tables = residency.Submit(context: recording.Context);
        var frame = residency.Frame!;
        var view = Math.Min(
            val1: m_view.View,
            val2: (frame.Views.Count - 1)
        );
        var width = recording.Width;
        var height = recording.Height;

        residency.RequestExtent(
            height: height,
            width: width
        );
        SdfFrameBlock.Write(
            block: recording.PassBlock,
            frame: frame,
            height: height,
            sceneTime: frame.Time,
            tables: tables.PassValues,
            view: view,
            width: width
        );

        if (IsMesh) {
            RecordMesh(
                recording: in recording,
                tables: tables
            );
        } else {
            RecordCompute(
                recording: in recording,
                tables: tables
            );
        }

        if (string.Equals(
            a: m_part,
            b: SdfWorldPackage.Parts.Views,
            comparisonType: StringComparison.Ordinal
        )) {
            residency.MarkRendered(view: view);
        }

        return RenderGraphPackageOutcome.Drew;
    }

    // Binds the pass set and dispatches the part's kernel: the sky over the extent, the masks over the tile grid in
    // groups, the beam one group a tile, the cull arguments once, and the hit passes indirectly over the surviving tiles.
    private void RecordCompute(in RenderGraphPackageRecording recording, SdfWorldTables tables) {
        var slot = recording.Slot;
        var set = m_sets!.PassSet(slot: slot);
        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;
        var pipeline = m_part switch {
            SdfWorldPackage.Parts.Sky => tables.Pipeline(index: SdfWorldTables.SkyPipelineIndex),
            SdfWorldPackage.Parts.Mask => tables.Pipeline(index: SdfWorldTables.InstanceCullPipelineIndex),
            SdfWorldPackage.Parts.Beam => tables.Pipeline(index: SdfWorldTables.BeamPipelineIndex),
            SdfWorldPackage.Parts.CullArgs => tables.Pipeline(index: SdfWorldTables.CullArgsPipelineIndex),
            SdfWorldPackage.Parts.Primary => tables.Pipeline(index: SdfWorldTables.PrimaryPipelineIndex),
            SdfWorldPackage.Parts.Surface => tables.Pipeline(index: SdfWorldTables.SurfacePipelineIndex),
            SdfWorldPackage.Parts.Ambient => tables.Pipeline(index: SdfWorldTables.AmbientPipelineIndex),
            _ => tables.ViewsPipeline,
        };

        BindPorts(
            recording: in recording,
            set: set,
            tables: tables
        );
        BindScreens(
            recording: in recording,
            set: set,
            tables: tables
        );
        recorder.BindPipeline(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            pipelineHandle: pipeline.Handle
        );
        m_sets.Bind(
            bindPoint: GpuBindPoint.Compute,
            commandBuffer: commandBuffer,
            pipelineLayout: pipeline.LayoutHandle,
            recorder: recorder,
            slot: slot
        );
        recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: tables.WorldSet(slot: tables.CurrentSlot),
            group: ((uint)ShaderInterfaceGroup.World),
            pipelineLayoutHandle: pipeline.LayoutHandle
        );

        if (recording.Arguments is { } arguments) {
            recorder.DispatchIndirect(
                argumentBufferHandle: arguments.BufferHandle,
                argumentBufferOffset: 0,
                commandBufferHandle: commandBuffer
            );

            return;
        }

        var tileGridX = ((recording.Width + (SdfWorldPackage.TileSize - 1)) / SdfWorldPackage.TileSize);
        var tileGridY = ((recording.Height + (SdfWorldPackage.TileSize - 1)) / SdfWorldPackage.TileSize);

        var (x, y) = m_part switch {
            SdfWorldPackage.Parts.Sky => (((recording.Width + (WorkgroupEdge - 1)) / WorkgroupEdge), ((recording.Height + (WorkgroupEdge - 1)) / WorkgroupEdge)),
            SdfWorldPackage.Parts.Mask => (((tileGridX + (WorkgroupEdge - 1)) / WorkgroupEdge), ((tileGridY + (WorkgroupEdge - 1)) / WorkgroupEdge)),
            SdfWorldPackage.Parts.Beam => (tileGridX, tileGridY),
            _ => (1u, 1u),
        };

        recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            groupCountX: x,
            groupCountY: y,
            groupCountZ: 1
        );
    }
    // Draws the frame's mesh draws into the instance's target, one draw call a draw, pulling their triangles from the mesh
    // region. A frame with no draws never records (Skips).
    private void RecordMesh(in RenderGraphPackageRecording recording, SdfWorldTables tables) {
        var draws = tables.MeshDraws;
        var count = tables.MeshDrawCount;

        if (
            (count == 0) ||
            (draws is null)
        ) {
            return;
        }

        var slot = recording.Slot;
        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;
        var pipeline = tables.MeshPipeline;
        var set = m_meshSets[slot];
        var target = recording.Outputs[0].Owned!;
        var framebuffer = m_framebuffers[0];

        for (var index = 0; (index < m_meshTargets.Count); index++) {
            if (ReferenceEquals(
                objA: m_meshTargets[index],
                objB: target
            )) {
                framebuffer = m_framebuffers[index];
            }
        }

        tables.WriteMeshTables(
            set: set,
            slot: tables.CurrentSlot
        );
        recorder.BeginRenderPass(
            area: new GpuPixelRect(
                Height: recording.Height,
                Width: recording.Width,
                X: 0,
                Y: 0
            ),
            commandBufferHandle: commandBuffer,
            framebuffer: framebuffer
        );
        recorder.BindPipeline(
            bindPoint: GpuBindPoint.Graphics,
            commandBufferHandle: commandBuffer,
            pipelineHandle: pipeline.Handle
        );
        recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Graphics,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: set,
            group: ((uint)ShaderInterfaceGroup.Pass),
            pipelineLayoutHandle: pipeline.LayoutHandle
        );

        for (var draw = 0u; (draw < count); draw++) {
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: m_meshPushedIndex,
                value: SdfWorldInterfaces.MeshPushedIndex(
                    draw: draw,
                    view: 0u
                )
            );
            recorder.PushConstants(
                bindPoint: GpuBindPoint.Graphics,
                commandBufferHandle: commandBuffer,
                data: m_meshPushedIndex,
                offset: 0,
                pipelineLayoutHandle: pipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Vertex | GpuShaderStage.Fragment
            );
            recorder.Draw(
                commandBufferHandle: commandBuffer,
                parameters: new GpuDrawParameters(
                    instanceCount: 1,
                    vertexCount: ((uint)draws[((int)draw)].Mesh.Indices.Length)
                )
            );
        }

        recorder.EndRenderPass(commandBufferHandle: commandBuffer);
    }
    // Writes, once per slot, every storage the pass's ports bind at the member its access reads or writes it through, and
    // the tables' dummy and fillers at every member no port binds. The storages a slot resolves stay
    // the instance's for the recorder's life.
    private void BindPorts(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables) {
        var slot = recording.Slot;

        if (ReferenceEquals(
            objA: m_portTables[slot],
            objB: tables
        )) {
            return;
        }

        var bindings = tables.Bindings;
        var output = tables.StorageFiller.ImageViewHandle;
        var meshVisibility = tables.SampledFiller.ImageViewHandle;

        foreach (var member in ScratchMembers) {
            tables.WriteWorldBuffer(buffer: tables.DummyBuffer, member: member, set: set);
        }


        for (var port = 0; (port < m_fragmentPass.Inputs.Count); port++) {
            var name = m_fragmentPass.Inputs[port].Name;
            var bound = recording.Inputs[port];

            if (bound.Buffer is { } buffer) {
                if (ReadMemberOf(version: name) is { } member) {
                    tables.WriteWorldBuffer(buffer: buffer, member: member, set: set);
                }
            } else if (string.Equals(
                a: name,
                b: SdfWorldPackage.Parts.MeshTarget,
                comparisonType: StringComparison.Ordinal
            )) {
                meshVisibility = bound.Image.ImageViewHandle;
            }
        }
        for (var port = 0; (port < m_fragmentPass.Outputs.Count); port++) {
            var name = m_fragmentPass.Outputs[port].Name;
            var bound = recording.Outputs[port];

            if (bound.Buffer is { } buffer) {
                if (WrittenMemberOf(version: name) is { } member) {
                    tables.WriteWorldBuffer(buffer: buffer, member: member, set: set);
                }
            } else if (bound.Kind == ShaderPipelineResourceKind.Image) {
                output = bound.Image.ImageViewHandle;
            }
        }

        bindings.WriteStorageImage(
            arrayElement: 0,
            binding: OutputBinding,
            descriptorSetHandle: set,
            imageViewHandle: output
        );
        bindings.WriteSampledImage(
            arrayElement: 0,
            binding: MeshVisibilityBinding,
            descriptorSetHandle: set,
            imageViewHandle: meshVisibility
        );
        Array.Clear(array: m_screens[slot]);
        m_portTables[slot] = tables;
    }
    // Writes each screen's image into the slot's pass set: a host's image every frame, since its handle is unique only among
    // live objects, and the filler once while the screen shows nothing.
    private void BindScreens(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables) {
        var bound = m_screens[recording.Slot];
        var filler = tables.SampledFiller.ImageViewHandle;
        var residency = m_view.Residency;

        for (var screen = 0; (screen < SdfWorldTables.MaxScreenSurfaces); screen++) {
            var image = (m_declaredScreens[screen]
                ? residency.ScreenImage(
                    leases: recording.Leases,
                    reads: recording.Reads,
                    screen: screen
                )
                : 0);

            if (image == 0) {
                if (bound[screen] == filler) {
                    continue;
                }

                image = filler;
            }

            tables.Bindings.WriteSampledImage(
                arrayElement: ((uint)screen),
                binding: ScreenSourcesBinding,
                descriptorSetHandle: set,
                imageViewHandle: image
            );
            bound[screen] = image;
        }
    }
    // The member a pass reads a fragment buffer through, or null for one it reads through no member.
    private static string? ReadMemberOf(string version) => version switch {
        SdfWorldPackage.Parts.InstanceMasks => SdfWorldPackage.InstanceMasks,
        SdfWorldPackage.Parts.Tiles => SdfWorldPackage.Tiles,
        SdfWorldPackage.Parts.CullBounds => SdfWorldPackage.CullBounds,
        SdfWorldPackage.Parts.Visibility or SdfWorldPackage.Parts.SurfaceVisibility or SdfWorldPackage.Parts.AmbientVisibility => SdfWorldPackage.VisibilityRecords,
        _ => null,
    };
    // The member a pass writes a fragment buffer through, or null for one it writes through no member.
    private static string? WrittenMemberOf(string version) => version switch {
        SdfWorldPackage.Parts.InstanceMasks => SdfWorldPackage.InstanceMasksWritten,
        SdfWorldPackage.Parts.Tiles => SdfWorldPackage.TilesWritten,
        SdfWorldPackage.Parts.Arguments => SdfWorldPackage.ViewsArgsWritten,
        SdfWorldPackage.Parts.CullBounds => SdfWorldPackage.CullBoundsWritten,
        SdfWorldPackage.Parts.Visibility or SdfWorldPackage.Parts.SurfaceVisibility or SdfWorldPackage.Parts.AmbientVisibility => SdfWorldPackage.VisibilityRecordsWritten,
        _ => null,
    };
}
