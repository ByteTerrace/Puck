using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    // upload → (per view: sky → mask → beam → cull-args → primary → surface → ambient → views). The hit passes share the
    // indirect bbox and have barriers between consumers; each view's output ends in its consumer layout.
    private void Record(uint viewportCount) {
        var recorder = m_gpu.Recorder;
        var commandBuffer = m_commandPools[m_currentSlot].CommandBufferHandle;
        // Between frames each view's output rests in its handoff layout: shader-readable for a same-device consumer, or
        // the cross-backend External layout when it is exported. The consumers span TWO stages — a graph pass's
        // fragment or compute read, and another engine's COMPUTE sampler (a view engine's output bound as a screen
        // source) — so the resting-stage scope names both; under the frame ring the re-transition before a view's set
        // must order after whichever consumer read it last. A new output starts undefined.
        var restingLayout = OutputLayout;
        var restingStage = (m_exportMode
            ? GpuStage.ComputeShader
            : GpuStage.FragmentShader | GpuStage.ComputeShader
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

        // Every descriptor-reachable image must have a defined layout before the first dispatch: screen content may
        // sample the filler in the first frame's views.
        if (!m_fillerInitialized) {
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
            m_fillerInitialized = true;
        }

        // FRAME-RING cross-frame gate: the GPU-written device-local scratch (tile / instance-mask / indirect-args /
        // cull-bounds / primary-hit buffers) is SHARED across ring slots, so with FrameRingSize
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
        // barrier) is counted outside every pass, and so are each view's output transitions.
        m_work.EnterPass(pass: UploadPass);
        // The region copies run on every frame, skipped ones included (the tables are this frame's inputs whatever the
        // passes do with them), copying only the words the staged regions owe, then transitioning each copied buffer
        // for the passes that read it.
        RecordRegionCopies();
        m_work.LeavePass();

        // Cadence gate: when this frame's inputs are byte-identical to the last RENDERED frame's (DecideCadenceSkip
        // proved it), every view's set is skipped and its retained output, untouched in its resting layout, stands:
        // pixel-identical to a full re-render of these inputs.
        //
        // Each view renders through its own dispatch set, sky through views, one deep in Z, into its own output: every
        // dispatch binds the ring slot's frame set and the view's views set, whose block names the view, and the buffer hazards between one set's reads and the next set's
        // writes are the frame buffer plan's, recorded by RecordBufferBarriers as for any pass order.
        for (var view = 0u; ((view < viewportCount) && !m_skipThisFrame); view++) {
            var output = m_viewOutputs[view]!;
            var viewsSet = m_viewsSets[m_currentSlot][view];

            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuAccess.ShaderWrite,
                destinationStageMask: GpuStage.ComputeShader,
                imageHandle: output.Image.ImageHandle,
                newLayout: GpuImageLayout.General,
                oldLayout: (output.Initialized
                    ? restingLayout
                    : GpuImageLayout.Undefined),
                sourceAccessMask: (output.Initialized
                    ? GpuAccess.ShaderRead
                    : GpuAccess.None),
                sourceStageMask: (output.Initialized
                    ? restingStage
                    : GpuStage.TopOfPipe)
            );
            m_work.EnterPass(pass: SkyPass);

            // Sky pre-pass FIRST, before any tile is culled: fills every pixel of the set's view's output with the
            // authored sky. Direct (not indirect) over a fixed (imageExtent.x, imageExtent.y, 1) grid — the largest a
            // view's render extent can reach, per-thread bounds-checked against its actual extent, matching the
            // beam/instance-cull dispatch style. Reuses Stage 1's own descriptor set and push constant; a beam-culled
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
            BindWorldGroups(
                commandBuffer: commandBuffer,
                frameSet: m_frameSets[m_currentSlot],
                pipeline: m_skyPipeline,
                viewsSet: viewsSet
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

            // Order the sky pass's output writes before the views pass overwrites the same image.
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
            BindWorldGroups(
                commandBuffer: commandBuffer,
                frameSet: m_frameSets[m_currentSlot],
                pipeline: m_instanceCullPipeline,
                viewsSet: viewsSet
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
            BindWorldGroups(
                commandBuffer: commandBuffer,
                frameSet: m_frameSets[m_currentSlot],
                pipeline: m_beamPipeline,
                viewsSet: viewsSet
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
            BindWorldGroups(
                commandBuffer: commandBuffer,
                frameSet: m_frameSets[m_currentSlot],
                pipeline: m_cullArgsPipeline,
                viewsSet: viewsSet
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
                viewsSet: viewsSet,
                workPass: PrimaryPass
            );
            RecordHitPass(
                commandBuffer: commandBuffer,
                label: "surface",
                pass: SdfFramePass.Surface,
                pipeline: m_surfacePipeline,
                viewsSet: viewsSet,
                workPass: SurfacePass
            );
            RecordHitPass(
                commandBuffer: commandBuffer,
                label: "ambient",
                pass: SdfFramePass.Ambient,
                pipeline: m_ambientPipeline,
                viewsSet: viewsSet,
                workPass: AmbientPass
            );

            // Stage 1: shade the view's primary hits into its output — dispatched INDIRECTLY from the
            // GPU-computed surviving-tile bbox; the all-empty margins are never dispatched; the kernel offsets each
            // invocation by the bbox origin (binding 8). The pipeline is the variant UploadProgram selected for the LIVE
            // program (full ISA vs core-ops — the stripped cases are unreachable under core, so the field is the same;
            // see SdfViewsKernelVariant); the view's views set binds against either (identically defined layouts, same
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
            BindWorldGroups(
                commandBuffer: commandBuffer,
                frameSet: m_frameSets[m_currentSlot],
                pipeline: viewsPipeline,
                viewsSet: viewsSet
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

            // Hand the output off in its consumer layout: shader-readable for a same-device consumer (a graph pass or
            // readback), or the cross-backend External handoff layout. Routing this through the recorder keeps its
            // per-resource state tracking the single source of truth.
            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuAccess.ShaderRead,
                destinationStageMask: restingStage,
                imageHandle: output.Image.ImageHandle,
                newLayout: restingLayout,
                oldLayout: GpuImageLayout.General,
                sourceAccessMask: GpuAccess.ShaderWrite,
                sourceStageMask: GpuStage.ComputeShader
            );
            output.Initialized = true;
            output.Rendered = true;
        }

        // A skipped frame runs no view's set: each view's output keeps the frame it last rendered.
        if (m_skipThisFrame) {
            for (var pass = SkyPass; (pass <= ViewsPass); pass++) {
                m_work.SkipPass(pass: pass);
            }
        }

        recorder.EndDebugGroup(
            commandBufferHandle: commandBuffer
        ); // close the outer per-engine group
        recorder.EndCommandBuffer(
            commandBufferHandle: commandBuffer
        );
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

            // The slice ordinal: every slice but a brick's last is whole, so the cursor is a whole number of slices, and the
            // kernel derives the slice's start and count from the ordinal, its block's slice size and the request's total.
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: m_brickBakeIndex,
                value: ((uint)(m_brickVoxelCursor[slot] / MaxBrickBakeVoxelsPerSlice))
            );

            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                pipelineHandle: m_brickBakePipeline.Handle
            );
            recorder.BindDescriptorSet(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                descriptorSetHandle: m_brickBakeSets[slot],
                group: PassGroup,
                pipelineLayoutHandle: m_brickBakePipeline.LayoutHandle
            );
            recorder.PushConstants(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                data: m_brickBakeIndex,
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
