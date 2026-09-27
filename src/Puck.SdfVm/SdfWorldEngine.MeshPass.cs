using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// The mesh pass (SdfMeshRasterPass): per view, between cull-args and primary, the frame's mesh draws (SdfFrame.MeshDraws)
// rasterize into the mesh visibility target at the engine extent, one draw call a draw, pulling their triangles from the
// mesh region. Primary reads the target to bound its march and records a mesh record where the mesh is nearer or equal;
// surface reads it for the mesh normal. The target and its reversed-Z depth attachment are created at the engine extent by
// the first frame that draws a mesh, and kept for the engine's life; the target rests shader-readable between passes. An
// engine whose frames draw no mesh holds neither (20 bytes a pixel of its extent): a frame with no mesh draws records no
// draw, the world block's meshDraws is zero and no kernel reads the target, so until one exists each views set binds the
// filler in its place, and the first frame of each ring slot after its creation rebinds the target.
public sealed partial class SdfWorldEngine {
    // Where the world block holds the frame's mesh draws, and the mesh interface's bindings.
    private static readonly int MeshDrawsOffset = WorldOffset(member: SdfWorldPackage.MeshDraws);
    private static readonly uint MeshVisibilityBinding = WorldBinding(member: SdfWorldPackage.MeshVisibility);

    private readonly IGpuPipeline m_meshPipeline;
    private readonly IGpuRenderPass m_meshRenderPass;

    // The attachments, or null before a frame draws a mesh.
    private IGpuImage? m_meshTarget;
    private IGpuImage? m_meshDepth;
    private IGpuFramebuffer? m_meshFramebuffer;
    // The bytes the attachments hold, zero before they exist; written on the frame thread, read by world.budget.
    private ulong m_meshAttachmentBytes;

    // One mesh set per ring slot: that slot's viewport table and mesh region.
    private readonly nint[] m_meshSets = new nint[FrameRingSize];
    // The index a draw call pushes: the view and the draw (SdfWorldInterfaces.MeshPushedIndex).
    private readonly byte[] m_meshPushedIndex = new byte[GpuPipelineLayoutDescription.PushIndexBytes];

    private bool m_meshTargetInitialized;
    // The draws the frame being recorded rasterizes, from its staged draw list.
    private uint m_meshDrawCount;

    /// <summary>Gets the bytes the mesh pass's target and depth attachment hold: zero until a frame draws a mesh, then
    /// <see cref="SdfMeshRasterPass.BytesPerPixel"/> a pixel of the engine's extent.</summary>
    public ulong MeshAttachmentBytes => Volatile.Read(location: ref m_meshAttachmentBytes);
    /// <summary>Gets the draws the latest frame's mesh pass rasterized in each view it rendered.</summary>
    public uint MeshDrawCount => m_meshDrawCount;

    /// <summary>Returns the bytes the mesh pass's target and depth attachment hold at an engine extent.</summary>
    /// <param name="width">The engine's extent width in pixels.</param>
    /// <param name="height">The engine's extent height in pixels.</param>
    /// <returns>The bytes.</returns>
    public static ulong MeshAttachmentBytesOf(uint width, uint height) =>
        ((((ulong)width) * height) * SdfMeshRasterPass.BytesPerPixel);

    // Creates the mesh pass's target and depth attachment at the engine extent and the framebuffer binding them for the
    // pass's render pass, the first time a frame draws a mesh; a creation that throws releases what it created.
    private void EnsureMeshAttachments() {
        if (
            (m_meshDrawCount == 0) ||
            (m_meshTarget is not null)
        ) {
            return;
        }

        using var scope = new GpuCreationScope();
        var target = scope.Own(created: m_gpu.ImageFactory.Create(
            format: SdfWorldPackage.MeshTargetFormat,
            height: m_height,
            name: NameOf(part: "mesh-visibility"),
            usage: GpuImageUsage.Sampled | GpuImageUsage.ColorAttachment,
            width: m_width
        ));
        var depth = scope.Own(created: m_gpu.ImageFactory.CreateDepth(
            attachment: SdfWorldPackage.MeshDepthAttachment,
            height: m_height,
            name: NameOf(part: "mesh-depth"),
            width: m_width
        ));
        var framebuffer = scope.Own(created: m_gpu.RenderPassFactory.CreateFramebuffer(
            colors: [target],
            depth: depth,
            renderPass: m_meshRenderPass
        ));

        scope.Complete();
        m_meshTarget = target;
        m_meshDepth = depth;
        m_meshFramebuffer = framebuffer;
        Volatile.Write(
            location: ref m_meshAttachmentBytes,
            value: MeshAttachmentBytesOf(
                height: m_height,
                width: m_width
            )
        );
    }
    // Binds the mesh visibility target, or the filler before one exists, into each views set of the frame's ring slot that
    // does not bind it already. Both are engine-owned, so the change-detected skip is sound (BindScreenSources' rule).
    private void BindMeshVisibility(uint viewportCount) {
        for (var view = 0; (view < ((int)viewportCount)); view++) {
            BindMeshVisibility(
                boundIndex: ((m_currentSlot * m_viewOutputs.Length) + view),
                viewsSet: m_viewsSets[m_currentSlot][view]
            );
        }
    }
    private void BindMeshVisibility(int boundIndex, nint viewsSet) {
        var view = (m_meshTarget?.ImageViewHandle ?? m_screenSourceFiller.ImageViewHandle);

        if (view == m_boundMeshVisibilityViews[boundIndex]) {
            return;
        }

        m_bindings.WriteSampledImage(
            arrayElement: 0,
            binding: MeshVisibilityBinding,
            descriptorSetHandle: viewsSet,
            imageViewHandle: view
        );
        m_boundMeshVisibilityViews[boundIndex] = view;
    }
    // Moves the filler from its first, undefined layout to the shader-readable one every sampled binding needs, once,
    // before any dispatch can reach a views set that binds it: in the construction's ISA handshake, and in the first frame
    // for an engine whose handshake did not run.
    private void InitializeFiller(nint commandBuffer) {
        if (m_fillerInitialized) {
            return;
        }

        m_gpu.Recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead,
            destinationStageMask: GpuStage.ComputeShader,
            imageHandle: m_screenSourceFiller.ImageHandle,
            newLayout: GpuImageLayout.ShaderReadOnly,
            oldLayout: GpuImageLayout.Undefined,
            sourceAccessMask: GpuAccess.None,
            sourceStageMask: GpuStage.TopOfPipe
        );
        m_fillerInitialized = true;
    }
    // Allocates a ring slot's mesh set against the mesh pipeline's pass group.
    private nint AllocateMeshSet(int slot) =>
        m_bindings.AllocateSet(
            descriptorSetLayoutHandle: m_meshPipeline.GroupLayoutHandles[((int)PassGroup)],
            name: NameOf(index: slot, part: "mesh"),
            poolHandle: m_pool
        );
    // Binds a ring slot's viewport table and mesh region into its mesh set, and the mesh region into its views sets.
    private void BindMeshRegion(int slot) {
        var region = m_meshRegion.Buffer(slot: slot);

        foreach (var views in m_viewsSets[slot]) {
            WriteWorldBuffer(buffer: region, member: SdfWorldPackage.MeshRegion, set: views);
        }

        WriteBuffer(buffer: m_viewportRegion.Buffer(slot: slot), layout: SdfWorldInterfaces.MeshLayout, member: SdfWorldPackage.Viewports, set: m_meshSets[slot]);
        WriteBuffer(buffer: region, layout: SdfWorldInterfaces.MeshLayout, member: SdfWorldPackage.MeshRegion, set: m_meshSets[slot]);
    }
    // Moves the target from its first, undefined layout to the shader-readable one it rests in between passes, once, in
    // the frame that created it, before any dispatch can reach the views sets that bind it.
    private void InitializeMeshTarget(nint commandBuffer) {
        if (
            m_meshTargetInitialized ||
            (m_meshTarget is null)
        ) {
            return;
        }

        m_gpu.Recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead,
            destinationStageMask: GpuStage.ComputeShader,
            imageHandle: m_meshTarget.ImageHandle,
            newLayout: GpuImageLayout.ShaderReadOnly,
            oldLayout: GpuImageLayout.Undefined,
            sourceAccessMask: GpuAccess.None,
            sourceStageMask: GpuStage.TopOfPipe
        );
        m_meshTargetInitialized = true;
    }
    // Rasterizes the frame's mesh draws for one view over its render extent: the target and depth attachment cleared,
    // one draw call a draw, then the target handed back to the hit passes' compute reads. A frame with no draws records
    // nothing, and the target keeps the layout it rests in.
    private void RecordMeshPass(nint commandBuffer, uint view) {
        if (m_meshDrawCount == 0) {
            return;
        }

        var recorder = m_gpu.Recorder;
        var output = m_viewOutputs[view]!;
        var target = m_meshTarget!;

        recorder.BeginDebugGroup(
            commandBufferHandle: commandBuffer,
            label: "mesh"
        );
        // The previous view's primary and surface read the target, and the previous pass's depth tests wrote the depth
        // attachment; both are cleared, so their contents are discarded.
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ColorAttachmentWrite,
            destinationStageMask: GpuStage.ColorAttachmentOutput,
            imageHandle: target.ImageHandle,
            newLayout: GpuImageLayout.RenderTarget,
            oldLayout: GpuImageLayout.ShaderReadOnly,
            sourceAccessMask: GpuAccess.ShaderRead,
            sourceStageMask: GpuStage.ComputeShader
        );
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.DepthAttachmentRead | GpuAccess.DepthAttachmentWrite,
            destinationStageMask: GpuStage.FragmentTests,
            imageHandle: m_meshDepth!.ImageHandle,
            newLayout: GpuImageLayout.DepthAttachment,
            oldLayout: GpuImageLayout.Undefined,
            sourceAccessMask: GpuAccess.DepthAttachmentWrite,
            sourceStageMask: GpuStage.FragmentTests
        );
        recorder.BeginRenderPass(
            area: new GpuPixelRect(
                Height: output.Height,
                Width: output.Width,
                X: 0,
                Y: 0
            ),
            commandBufferHandle: commandBuffer,
            framebuffer: m_meshFramebuffer!
        );
        recorder.BindPipeline(
            bindPoint: GpuBindPoint.Graphics,
            commandBufferHandle: commandBuffer,
            pipelineHandle: m_meshPipeline.Handle
        );
        recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Graphics,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: m_meshSets[m_currentSlot],
            group: PassGroup,
            pipelineLayoutHandle: m_meshPipeline.LayoutHandle
        );

        for (var draw = 0u; (draw < m_meshDrawCount); draw++) {
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: m_meshPushedIndex,
                value: SdfWorldInterfaces.MeshPushedIndex(
                    draw: draw,
                    view: view
                )
            );
            recorder.PushConstants(
                bindPoint: GpuBindPoint.Graphics,
                commandBufferHandle: commandBuffer,
                data: m_meshPushedIndex,
                offset: 0,
                pipelineLayoutHandle: m_meshPipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Vertex | GpuShaderStage.Fragment
            );
            recorder.Draw(
                commandBufferHandle: commandBuffer,
                parameters: new GpuDrawParameters(
                    instanceCount: 1,
                    vertexCount: ((uint)m_meshDraws![((int)draw)].Mesh.Indices.Length)
                )
            );
        }

        recorder.EndRenderPass(commandBufferHandle: commandBuffer);
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead,
            destinationStageMask: GpuStage.ComputeShader,
            imageHandle: target.ImageHandle,
            newLayout: GpuImageLayout.ShaderReadOnly,
            oldLayout: GpuImageLayout.RenderTarget,
            sourceAccessMask: GpuAccess.ColorAttachmentWrite,
            sourceStageMask: GpuStage.ColorAttachmentOutput
        );
        recorder.EndDebugGroup(commandBufferHandle: commandBuffer);
    }
}
