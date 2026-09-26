using System.Buffers.Binary;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh pass (SdfMeshRasterPass): per view, between cull-args and primary, the frame's mesh draws (SdfFrame.MeshDraws)
// rasterize into the mesh visibility target at the engine extent, one draw call a draw, pulling their triangles from the
// mesh region. Primary reads the target to bound its march and records a mesh record where the mesh is nearer or equal;
// surface reads it for the mesh normal. The target and its reversed-Z depth attachment are created with the engine and
// sized to its extent, and the target rests shader-readable between passes, so the views sets bind it once. A frame with
// no mesh draws records no draw: the world block's meshDraws is zero and no kernel reads the target.
public sealed partial class SdfWorldEngine {
    // Where the world block holds the frame's mesh draws, and the mesh interface's bindings.
    private static readonly int MeshDrawsOffset = WorldOffset(member: SdfWorldInterfaces.MeshDraws);
    private static readonly uint MeshVisibilityBinding = WorldBinding(member: SdfWorldInterfaces.MeshVisibility);

    private readonly IGpuPipeline m_meshPipeline;
    private readonly IGpuRenderPass m_meshRenderPass;
    private readonly IGpuImage m_meshTarget;
    private readonly IGpuImage m_meshDepth;
    private readonly IGpuFramebuffer m_meshFramebuffer;
    // One mesh set per ring slot: that slot's viewport table and mesh region.
    private readonly nint[] m_meshSets = new nint[FrameRingSize];
    // The index a draw call pushes: the view and the draw (SdfWorldInterfaces.MeshPushedIndex).
    private readonly byte[] m_meshPushedIndex = new byte[GpuPipelineLayoutDescription.PushIndexBytes];
    private bool m_meshTargetInitialized;
    // The draws the frame being recorded rasterizes, from its staged draw list.
    private uint m_meshDrawCount;

    /// <summary>Gets the bytes the mesh pass's target and depth attachment hold, which the engine allocates at its
    /// extent: <see cref="SdfMeshRasterPass.BytesPerPixel"/> a pixel.</summary>
    public ulong MeshAttachmentBytes => MeshAttachmentBytesOf(
        height: m_height,
        width: m_width
    );
    /// <summary>Gets the draws the latest frame's mesh pass rasterized in each view it rendered.</summary>
    public uint MeshDrawCount => m_meshDrawCount;

    /// <summary>Returns the bytes the mesh pass's target and depth attachment hold at an engine extent.</summary>
    /// <param name="width">The engine's extent width in pixels.</param>
    /// <param name="height">The engine's extent height in pixels.</param>
    /// <returns>The bytes.</returns>
    public static ulong MeshAttachmentBytesOf(uint width, uint height) =>
        ((((ulong)width) * height) * SdfMeshRasterPass.BytesPerPixel);

    // Creates the mesh pass's target and depth attachment at the engine extent and the framebuffer binding them for the
    // pass's render pass, each joining the construction's scope.
    private (IGpuImage Target, IGpuImage Depth, IGpuFramebuffer Framebuffer) CreateMeshAttachments(GpuCreationScope scope, IGpuRenderPass renderPass) {
        var target = scope.Own(created: m_gpu.ImageFactory.Create(
            format: SdfMeshRasterPass.TargetFormat,
            height: m_height,
            name: NameOf(part: "mesh-visibility"),
            usage: GpuImageUsage.Sampled | GpuImageUsage.ColorAttachment,
            width: m_width
        ));
        var depth = scope.Own(created: m_gpu.ImageFactory.Create(
            clearDepth: SdfMeshRasterPass.ClearDepth,
            format: SdfMeshRasterPass.DepthFormat,
            height: m_height,
            name: NameOf(part: "mesh-depth"),
            usage: GpuImageUsage.DepthAttachment,
            width: m_width
        ));
        var framebuffer = scope.Own(created: m_gpu.RenderPassFactory.CreateFramebuffer(
            colors: [target],
            depth: depth,
            renderPass: renderPass
        ));

        return (target, depth, framebuffer);
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
            WriteWorldBuffer(buffer: region, member: SdfWorldInterfaces.MeshRegion, set: views);
        }

        WriteBuffer(buffer: m_viewportRegion.Buffer(slot: slot), layout: SdfWorldInterfaces.MeshLayout, member: SdfWorldInterfaces.Viewports, set: m_meshSets[slot]);
        WriteBuffer(buffer: region, layout: SdfWorldInterfaces.MeshLayout, member: SdfWorldInterfaces.MeshRegion, set: m_meshSets[slot]);
    }
    // Moves the target from its first, undefined layout to the shader-readable one it rests in between passes, once,
    // before any dispatch can reach the views sets that bind it: in the construction's ISA handshake, and in the first
    // frame for an engine whose handshake did not run.
    private void InitializeMeshTarget(nint commandBuffer) {
        if (m_meshTargetInitialized) {
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
            imageHandle: m_meshTarget.ImageHandle,
            newLayout: GpuImageLayout.RenderTarget,
            oldLayout: GpuImageLayout.ShaderReadOnly,
            sourceAccessMask: GpuAccess.ShaderRead,
            sourceStageMask: GpuStage.ComputeShader
        );
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.DepthAttachmentRead | GpuAccess.DepthAttachmentWrite,
            destinationStageMask: GpuStage.FragmentTests,
            imageHandle: m_meshDepth.ImageHandle,
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
            framebuffer: m_meshFramebuffer
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
            imageHandle: m_meshTarget.ImageHandle,
            newLayout: GpuImageLayout.ShaderReadOnly,
            oldLayout: GpuImageLayout.RenderTarget,
            sourceAccessMask: GpuAccess.ColorAttachmentWrite,
            sourceStageMask: GpuStage.ColorAttachmentOutput
        );
        recorder.EndDebugGroup(commandBufferHandle: commandBuffer);
    }}
