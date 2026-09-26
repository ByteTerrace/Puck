using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    // upload → (per view: sky → mask → beam → cull-args → primary → surface → ambient → views) → composite.
    // The hit passes share the indirect bbox and have barriers between consumers; output uses its consumer layout.
    private void Record(uint viewportCount) {
        var recorder = m_gpu.Recorder;
        var commandBuffer = m_commandPools[m_currentSlot].CommandBufferHandle;
        // After the first frame the OUTPUT rests in its handoff layout: shader-readable when a same-device consumer
        // sampled it, or the cross-backend External layout when it was exported. The first frame starts undefined.
        // The non-export consumer set spans TWO stages — the presenter's fragment blit AND another engine's COMPUTE
        // sampler (a view engine's output bound as a screen source) — so the resting-stage scope names both; under
        // the frame ring the begin-of-frame re-transition below must order after whichever consumer read it last.
        var restingLayout = OutputLayout;
        var restingStage = (m_exportMode
            ? GpuStage.ComputeShader
            : GpuStage.FragmentShader | GpuStage.ComputeShader
        );
        var outputOldLayout = (m_imageInitialized
            ? restingLayout
            : GpuImageLayout.Undefined
        );
        var outputSourceAccess = (m_imageInitialized
            ? GpuAccess.ShaderRead
            : GpuAccess.None
        );
        var outputSourceStage = (m_imageInitialized
            ? restingStage
            : GpuStage.TopOfPipe
        );

        recorder.BeginCommandBuffer(
            commandBufferHandle: commandBuffer
        );
        m_bufferHazards.Reset();
        // The outer debug-marker group scoping this engine's whole recorded frame (see DebugLabel) — a GPU capture
        // shows the per-pass groups below nested inside it. No-op on a backend without debug labels; pixel-neutral.
        recorder.BeginDebugGroup(
            commandBufferHandle: commandBuffer,
            label: DebugLabel
        );

        // Every descriptor-reachable image must have a defined layout before the first dispatch. In particular, the
        // sky pre-pass writes the per-view sources before Stage 1, while screen content may sample the filler there.
        if (!m_imageInitialized) {
            foreach (var source in m_sourceTextures) {
                if (source is null) {
                    continue;
                }

                recorder.TransitionImageLayout(
                    commandBufferHandle: commandBuffer,
                    destinationAccessMask: GpuAccess.ShaderWrite,
                    destinationStageMask: GpuStage.ComputeShader,
                    imageHandle: source.ImageHandle,
                    newLayout: GpuImageLayout.General,
                    oldLayout: GpuImageLayout.Undefined,
                    sourceAccessMask: GpuAccess.None,
                    sourceStageMask: GpuStage.TopOfPipe
                );
            }

            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuAccess.ShaderRead,
                destinationStageMask: GpuStage.ComputeShader,
                imageHandle: m_screenSourceFiller.ImageHandle,
                newLayout: GpuImageLayout.ShaderReadOnly,
                oldLayout: GpuImageLayout.Undefined,
                sourceAccessMask: GpuAccess.None,
                sourceStageMask: GpuStage.TopOfPipe
            );
        }

        // FRAME-RING cross-frame gate: the GPU-written device-local scratch (tile / instance-mask / indirect-args /
        // cull-bounds / primary-hit buffers, the per-view source textures) is SHARED across ring slots, so with FrameRingSize
        // frames in flight this frame's first write must order after the PREVIOUS frame's last read of that scratch —
        // an execution dependency on all prior compute (and the indirect-args fetch), queue-scoped like every Vulkan
        // barrier. This serializes GPU frames against each other (the natural order anyway — the ring overlaps CPU
        // production with GPU execution, not GPU frames); it replaces the host's per-frame whole-device drain.
        recorder.MemoryBarrier(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite,
            destinationStageMask: GpuStage.ComputeShader,
            sourceAccessMask: GpuAccess.ShaderWrite | GpuAccess.IndirectCommandRead,
            sourceStageMask: GpuStage.ComputeShader | GpuStage.DrawIndirect
        );

        // CARVE-BAKE: prepend this frame's background bake slices before the render passes below. Each baking slot
        // advances one ≤ 256K-voxel slice; when a slot's cursor reaches its total, it flips to Ready. The beam's
        // buffer transitions make the pool writes visible to its march this same frame.
        RecordBrickUpload(commandBuffer: commandBuffer);
        RecordBrickBakeSlices(commandBuffer: commandBuffer);

        // The work counted before UploadPass (the brick upload and bake slices, the begin-of-frame transitions and
        // barrier) or after CompositePass is counted outside every pass.
        m_work.EnterPass(pass: UploadPass);
        // The region copies run on every frame, skipped ones included (the tables are this frame's inputs whatever the
        // passes do with them), copying only the words the staged regions owe, then transitioning each copied buffer
        // for the passes that read it.
        RecordRegionCopies(commandBuffer: commandBuffer);
        m_work.LeavePass();

        // Cadence gate: when this frame's inputs are byte-identical to the last RENDERED frame's
        // (DecideCadenceSkip proved it), SKIP sky through views and fall straight through to the composite below —
        // which re-reads the RETAINED (single, ring-shared) views source textures the previous frame wrote and
        // re-composites them into the swapchain-bound output. Pixel-identical to a full re-render of these inputs;
        // the top-of-frame cross-frame barrier already orders this read after that previous frame's writes.
        m_pushConstant.AsSpan().CopyTo(destination: m_viewPush);

        var viewPushWords = MemoryMarshal.Cast<byte, uint>(span: m_viewPush.AsSpan());

        // Each view renders through its own dispatch set, sky through views, one deep in Z: the set's push names its view
        // (CompositeParams.viewBase), and the buffer hazards between one set's reads and the next set's writes are the
        // frame buffer plan's, recorded by RecordBufferBarriers as for any pass order.
        for (var view = 0u; ((view < viewportCount) && !m_skipThisFrame); view++) {
            viewPushWords[ViewBaseWord] = view;
            m_work.EnterPass(pass: SkyPass);

            // Sky pre-pass FIRST, before any tile is culled: fills every pixel of the set's view's render-dims source
            // texture with the authored sky. Direct (not indirect) over a fixed (imageExtent.x, imageExtent.y, 1) grid
            // — the largest the view's render-dims rect can reach, per-thread bounds-checked against its actual
            // rectDims, matching the beam/instance-cull dispatch style. Reuses Stage 1's own descriptor set (m_viewsSets) and push constant; a beam-culled
            // tile's pixel is otherwise never touched by any later pass, so this is the only writer that reaches it.
            RecordBufferBarriers(
                commandBuffer: commandBuffer,
                pass: SdfFramePass.Sky
            );
            recorder.BeginDebugGroup(
                commandBufferHandle: commandBuffer,
                label: "sky"
            );
            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                pipelineHandle: m_skyPipeline.Handle
            );
            recorder.BindDescriptorSet(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                descriptorSetHandle: m_viewsSets[m_currentSlot],
                group: 0,
                pipelineLayoutHandle: m_skyPipeline.LayoutHandle
            );
            recorder.PushConstants(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                data: m_viewPush,
                offset: 0,
                pipelineLayoutHandle: m_skyPipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Compute
            );
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                groupCountX: ((m_width + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountY: ((m_height + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountZ: 1
            );
            recorder.EndDebugGroup(
                commandBufferHandle: commandBuffer
            );

            m_work.LeavePass();
            m_work.EnterPass(pass: MaskPass);

            // Order the sky pass's source-texture writes before the views pass overwrites the same images.
            recorder.MemoryBarrier(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite,
                destinationStageMask: GpuStage.ComputeShader,
                sourceAccessMask: GpuAccess.ShaderWrite,
                sourceStageMask: GpuStage.ComputeShader
            );

            // Instance-cull pass (mask-first): one invocation per tile of the set's view — bins the program's instances
            // against each tile's cone into the per-tile mask (the uniform-grid walk, or the flat loop when the program
            // packs no grid). Its OWN kernel so its register footprint never taxes the cone march's occupancy.
            RecordBufferBarriers(
                commandBuffer: commandBuffer,
                pass: SdfFramePass.Mask
            );
            recorder.BeginDebugGroup(
                commandBufferHandle: commandBuffer,
                label: "mask"
            );
            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                pipelineHandle: m_instanceCullPipeline.Handle
            );
            recorder.BindDescriptorSet(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                descriptorSetHandle: m_instanceCullSets[m_currentSlot],
                group: 0,
                pipelineLayoutHandle: m_instanceCullPipeline.LayoutHandle
            );
            recorder.PushConstants(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                data: m_viewPush,
                offset: 0,
                pipelineLayoutHandle: m_instanceCullPipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Compute
            );
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                groupCountX: ((m_tileGridX + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountY: ((m_tileGridY + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountZ: 1
            );
            recorder.EndDebugGroup(
                commandBufferHandle: commandBuffer
            );

            m_work.LeavePass();
            m_work.EnterPass(pass: BeamPass);

            // Tile-cull prepass: one invocation per tile of the set's view, cone-marching the tile-MASKED field.
            RecordBufferBarriers(
                commandBuffer: commandBuffer,
                pass: SdfFramePass.Beam
            );
            recorder.BeginDebugGroup(
                commandBufferHandle: commandBuffer,
                label: "beam"
            );
            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                pipelineHandle: m_beamPipeline.Handle
            );
            recorder.BindDescriptorSet(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                descriptorSetHandle: m_beamSets[m_currentSlot],
                group: 0,
                pipelineLayoutHandle: m_beamPipeline.LayoutHandle
            );
            recorder.PushConstants(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                data: m_viewPush,
                offset: 0,
                pipelineLayoutHandle: m_beamPipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Compute
            );
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                groupCountX: m_tileGridX,
                groupCountY: m_tileGridY,
                groupCountZ: 1
            );
            recorder.EndDebugGroup(
                commandBufferHandle: commandBuffer
            );

            m_work.LeavePass();
            m_work.EnterPass(pass: CullArgsPass);

            // Cull-args reduction (a single invocation): reduce the cull buffer to the surviving-tile bbox, writing Stage
            // 1's INDIRECT dispatch group counts + the dispatch box (group origin and exclusive end) — so the GPU, not
            // the CPU, sizes the views grid.
            RecordBufferBarriers(
                commandBuffer: commandBuffer,
                pass: SdfFramePass.CullArgs
            );
            recorder.BeginDebugGroup(
                commandBufferHandle: commandBuffer,
                label: "cull-args"
            );
            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                pipelineHandle: m_cullArgsPipeline.Handle
            );
            recorder.BindDescriptorSet(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                descriptorSetHandle: m_cullArgsSet,
                group: 0,
                pipelineLayoutHandle: m_cullArgsPipeline.LayoutHandle
            );
            recorder.PushConstants(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                data: m_viewPush,
                offset: 0,
                pipelineLayoutHandle: m_cullArgsPipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Compute
            );
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                groupCountX: 1,
                groupCountY: 1,
                groupCountZ: 1
            );
            recorder.EndDebugGroup(
                commandBufferHandle: commandBuffer
            );

            m_work.LeavePass();

            // Primary's buffer transitions carry the indirect args into the indirect-argument state (Direct3D 12's
            // ExecuteIndirect needs that per-resource state) and the dispatch box into its read state. The cull buffer and the
            // upload twins are already in their read states from the passes before.
            RecordHitPass(
                commandBuffer: commandBuffer,
                label: "primary",
                pass: SdfFramePass.Primary,
                pipeline: m_primaryPipeline,
                workPass: PrimaryPass
            );
            RecordHitPass(
                commandBuffer: commandBuffer,
                label: "surface",
                pass: SdfFramePass.Surface,
                pipeline: m_surfacePipeline,
                workPass: SurfacePass
            );
            RecordHitPass(
                commandBuffer: commandBuffer,
                label: "ambient",
                pass: SdfFramePass.Ambient,
                pipeline: m_ambientPipeline,
                workPass: AmbientPass
            );

            // Stage 1: shade each viewport's primary hits into its own source texture — dispatched INDIRECTLY from the
            // GPU-computed surviving-tile bbox; the all-empty margins are never dispatched; the kernel offsets each
            // invocation by the bbox origin (binding 8). The pipeline is the variant UploadProgram selected for the LIVE
            // program (full ISA vs core-ops — the stripped cases are unreachable under core, so the field is the same;
            // see SdfViewsKernelVariant); the per-slot views set binds against either (identically defined layouts, same
            // bindings array).
            m_work.EnterPass(pass: ViewsPass);

            var viewsPipeline = (m_viewsVariant switch {
                SdfViewsKernelVariant.CoreOps => m_viewsCorePipeline,
                SdfViewsKernelVariant.Folds => m_viewsFoldsPipeline,
                _ => m_viewsPipeline,
            });

            RecordBufferBarriers(
                commandBuffer: commandBuffer,
                pass: SdfFramePass.Views
            );
            recorder.BeginDebugGroup(
                commandBufferHandle: commandBuffer,
                label: "views"
            );
            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                pipelineHandle: viewsPipeline.Handle
            );
            recorder.BindDescriptorSet(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                descriptorSetHandle: m_viewsSets[m_currentSlot],
                group: 0,
                pipelineLayoutHandle: viewsPipeline.LayoutHandle
            );
            recorder.PushConstants(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                data: m_viewPush,
                offset: 0,
                pipelineLayoutHandle: viewsPipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Compute
            );
            recorder.DispatchIndirect(
                argumentBufferHandle: m_viewsArgsBuffer.BufferHandle,
                argumentBufferOffset: 0,
                commandBufferHandle: commandBuffer
            );
            recorder.EndDebugGroup(
                commandBufferHandle: commandBuffer
            );

            m_work.LeavePass();
        }

        if (!m_skipThisFrame) {
            m_work.EnterPass(pass: CompositePass);

            // Make Stage 1's source-texture writes visible to Stage 2's reads.
            recorder.MemoryBarrier(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuAccess.ShaderRead,
                destinationStageMask: GpuStage.ComputeShader,
                sourceAccessMask: GpuAccess.ShaderWrite,
                sourceStageMask: GpuStage.ComputeShader
            );
        } else {
            // SKIPPED FRAME: no render passes ran; fall through to the composite. The retained tile buffer + source
            // textures (single, ring-shared, left in General by the previous rendered frame) are ordered for this
            // frame's composite reads by the top-of-frame cross-frame barrier, so no extra barrier is needed. The sky
            // pre-pass is skipped too: its only inputs (viewports, sdfScreenLights) are already covered by the
            // signature that proved this frame identical to the last rendered one, so its retained output is still correct.
            for (var pass = SkyPass; (pass <= ViewsPass); pass++) {
                m_work.SkipPass(pass: pass);
            }

            m_work.EnterPass(pass: CompositePass);
        }

        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderWrite,
            destinationStageMask: GpuStage.ComputeShader,
            imageHandle: m_storageImage.ImageHandle,
            newLayout: GpuImageLayout.General,
            oldLayout: outputOldLayout,
            sourceAccessMask: outputSourceAccess,
            sourceStageMask: outputSourceStage
        );

        // Stage 2: composite each source into its screen region (indirect, from the host-written constant grid).
        RecordBufferBarriers(
            commandBuffer: commandBuffer,
            pass: SdfFramePass.Composite
        );
        recorder.BeginDebugGroup(
            commandBufferHandle: commandBuffer,
            label: "composite"
        );
        recorder.BindPipeline(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            pipelineHandle: m_compositePipeline.Handle
        );
        recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: m_compositeSets[m_currentSlot],
            group: 0,
            pipelineLayoutHandle: m_compositePipeline.LayoutHandle
        );
        recorder.PushConstants(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            data: m_compositePush,
            offset: 0,
            pipelineLayoutHandle: m_compositePipeline.LayoutHandle,
            stageFlags: GpuShaderStage.Compute
        );
        recorder.DispatchIndirect(
            argumentBufferHandle: m_compositeArgsBuffer.BufferHandle,
            argumentBufferOffset: 0,
            commandBufferHandle: commandBuffer
        );
        recorder.EndDebugGroup(
            commandBufferHandle: commandBuffer
        );

        m_work.LeavePass();

        // Hand the output off in its consumer layout: shader-readable for a same-device consumer (compositor or
        // readback), or the cross-backend External handoff layout. Routing this through the recorder keeps its
        // per-resource state tracking the single source of truth.
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead,
            destinationStageMask: restingStage,
            imageHandle: m_storageImage.ImageHandle,
            newLayout: restingLayout,
            oldLayout: GpuImageLayout.General,
            sourceAccessMask: GpuAccess.ShaderWrite,
            sourceStageMask: GpuStage.ComputeShader
        );

        recorder.EndDebugGroup(
            commandBufferHandle: commandBuffer
        ); // close the outer per-engine group
        recorder.EndCommandBuffer(
            commandBufferHandle: commandBuffer
        );

        m_imageInitialized = true;
    }
    // One queued host-baked brick per produced frame: the brick staging region, retargeted at the brick's slot in the
    // pool, owes every voxel written and copies them there through the device's region-copy pipeline. The pool's
    // hazards are the frame buffer plan's, as for the bake that writes it.
    private void RecordBrickUpload(nint commandBuffer) {
        if (
            (m_brickRegion is not { } region) ||
            (m_brickUploads.Count == 0)
        ) {
            return;
        }

        var (slot, count, voxels) = m_brickUploads.Dequeue();
        var recorder = m_gpu.Recorder;

        region.Target(destinationWord: SdfBrickPoolLayout.SlotWordOffset(slot: slot));
        _ = region.Write(
            bytes: MemoryMarshal.AsBytes(span: voxels.AsSpan(
                length: count,
                start: 0
            )),
            offset: 0
        );
        region.Flush(slot: m_currentSlot);
        RecordBufferBarriers(
            commandBuffer: commandBuffer,
            pass: SdfFramePass.BrickUpload
        );
        recorder.BeginDebugGroup(
            commandBufferHandle: commandBuffer,
            label: "brick-upload"
        );
        region.RecordCopy(
            commandBuffer: commandBuffer,
            slot: m_currentSlot
        );
        recorder.EndDebugGroup(
            commandBufferHandle: commandBuffer
        );

        m_brickStates[slot] = BrickBakeState.Ready;
        m_brickSerials[slot]++;
    }
    // Records this frame's carve-bake slices: for each Baking brick slot, one voxel slice of ≤ MaxBrickBakeVoxelsPerSlice,
    // advancing the slot's CPU cursor and flipping it to Ready once its whole brick is written. A no-op when the pool is
    // disabled or nothing is baking — the bare room never pays it. Each slice is a plain direct dispatch of the
    // standalone baker pipeline; the slots' voxel ranges are disjoint, so the slices need no barrier between them.
    private void RecordBrickBakeSlices(nint commandBuffer) {
        if (m_brickBakePipeline is null) {
            return;
        }

        var recorder = m_gpu.Recorder;
        var push = MemoryMarshal.Cast<byte, uint>(span: m_brickBakePush.AsSpan());
        var recorded = false;

        for (var slot = 0; (slot < SdfBrickPoolLayout.MaxBricks); slot++) {
            if (m_brickStates[slot] != BrickBakeState.Baking) {
                continue;
            }

            var remaining = (m_brickTotalVoxels[slot] - m_brickVoxelCursor[slot]);

            if (remaining <= 0) {
                m_brickStates[slot] = BrickBakeState.Ready;

                continue;
            }

            var sliceCount = Math.Min(
                val1: remaining,
                val2: MaxBrickBakeVoxelsPerSlice
            );

            if (!recorded) {
                RecordBufferBarriers(
                    commandBuffer: commandBuffer,
                    pass: SdfFramePass.BrickBake
                );
                recorder.BeginDebugGroup(
                    commandBufferHandle: commandBuffer,
                    label: "brick-bake"
                );
            }

            push[0] = ((uint)m_brickVoxelCursor[slot]); push[1] = ((uint)sliceCount); push[2] = 0u; push[3] = 0u;

            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                pipelineHandle: m_brickBakePipeline.Handle
            );
            recorder.BindDescriptorSet(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                descriptorSetHandle: m_brickBakeSets[slot],
                group: 0,
                pipelineLayoutHandle: m_brickBakePipeline.LayoutHandle
            );
            recorder.PushConstants(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                data: m_brickBakePush,
                offset: 0,
                pipelineLayoutHandle: m_brickBakePipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Compute
            );
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                groupCountX: ((((uint)sliceCount) + (BrickBakeWorkgroupSize - 1)) / BrickBakeWorkgroupSize),
                groupCountY: 1,
                groupCountZ: 1
            );

            m_brickVoxelCursor[slot] += sliceCount;

            if (m_brickVoxelCursor[slot] >= m_brickTotalVoxels[slot]) {
                m_brickStates[slot] = BrickBakeState.Ready;
            }

            recorded = true;
        }

        if (recorded) {
            recorder.EndDebugGroup(
                commandBufferHandle: commandBuffer
            );
        }
    }
}
