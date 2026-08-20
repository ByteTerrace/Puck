using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    // Creates the TimingPoolCount rotating timestamp pools once. Called at construction in eager mode, or on the first
    // armed frame in live-armed mode. A no-op after the pools exist.
    private void EnsureTimingPools() {
        if (m_timingPools is not null) {
            return;
        }

        var timingPools = new IGpuTimingPool[TimingPoolCount];

        for (var pool = 0; (pool < TimingPoolCount); pool++) {
            timingPools[pool] = m_timingFactory!.CreateTimestampPool(
                deviceContext: m_deviceContext,
                queryCapacity: TimingCapacity
            );
        }

        m_timingPools = timingPools;
    }
    // sky → barrier → mask → barrier → beam → barrier → cull-args → barrier + indirect-args transition → views
    // (INDIRECT) → barrier → composite (INDIRECT), with the output handed off in its consumer layout.
    private void Record(uint viewportCount) {
        var recorder = m_gpu.ComputeRecorder;
        var commandBuffer = m_commandPools[m_currentSlot].CommandBufferHandle;
        // After the first frame the OUTPUT rests in its handoff layout: shader-readable when a same-device consumer
        // sampled it, or the cross-backend External layout when it was exported. The first frame starts undefined.
        // The non-export consumer set spans TWO stages — the presenter's fragment blit AND another engine's COMPUTE
        // sampler (a view engine's output bound as a screen source) — so the resting-stage scope names both; under
        // the frame ring the begin-of-frame re-transition below must order after whichever consumer read it last.
        var restingLayout = (m_exportMode
            ? GpuImageLayout.External
            : GpuImageLayout.ShaderReadOnly
        );
        var restingStage = (m_exportMode
            ? GpuComputeStage.ComputeShader
            : GpuComputeStage.FragmentShader | GpuComputeStage.ComputeShader
        );
        var outputOldLayout = (m_imageInitialized
            ? restingLayout
            : GpuImageLayout.Undefined
        );
        var outputSourceAccess = (m_imageInitialized
            ? GpuComputeAccess.ShaderRead
            : GpuComputeAccess.None
        );
        var outputSourceStage = (m_imageInitialized
            ? restingStage
            : GpuComputeStage.TopOfPipe
        );

        recorder.BeginCommandBuffer(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle
        );
        // The outer debug-marker group scoping this engine's whole recorded frame (see DebugLabel) — a GPU capture
        // shows the per-pass groups below nested inside it. No-op on a backend without debug labels; pixel-neutral.
        recorder.BeginDebugGroup(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
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
                    destinationAccessMask: GpuComputeAccess.ShaderWrite,
                    destinationStageMask: GpuComputeStage.ComputeShader,
                    deviceHandle: m_deviceHandle,
                    imageHandle: source.ImageHandle,
                    newLayout: GpuImageLayout.General,
                    oldLayout: GpuImageLayout.Undefined,
                    sourceAccessMask: GpuComputeAccess.None,
                    sourceStageMask: GpuComputeStage.TopOfPipe
                );
            }

            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: m_deviceHandle,
                imageHandle: m_screenSourceFiller.ImageHandle,
                newLayout: GpuImageLayout.ShaderReadOnly,
                oldLayout: GpuImageLayout.Undefined,
                sourceAccessMask: GpuComputeAccess.None,
                sourceStageMask: GpuComputeStage.TopOfPipe
            );
        }

        // FRAME-RING cross-frame gate: the GPU-written device-local scratch (tile / instance-mask / indirect-args /
        // cull-bounds buffers, the per-view source textures) is SHARED across ring slots, so with FrameRingSize
        // frames in flight this frame's first write must order after the PREVIOUS frame's last read of that scratch —
        // an execution dependency on all prior compute (and the indirect-args fetch), queue-scoped like every Vulkan
        // barrier. This serializes GPU frames against each other (the natural order anyway — the ring overlaps CPU
        // production with GPU execution, not GPU frames); it replaces the host's per-frame whole-device drain.
        recorder.MemoryBarrier(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            sourceAccessMask: GpuComputeAccess.ShaderWrite | GpuComputeAccess.IndirectCommandRead,
            sourceStageMask: GpuComputeStage.ComputeShader | GpuComputeStage.DrawIndirect
        );

        // CARVE-BAKE: prepend this frame's background bake slices BEFORE the frame-timing marks so
        // the render passes' per-pass budget excludes the background bake. Each baking slot advances one ≤ 256K-voxel
        // slice; when a slot's cursor reaches its total, it flips to Ready. A pool-write → pool-read barrier follows so
        // the beam/views marches see the just-written voxels this same frame (and the cross-frame barrier orders any
        // later frame's read after this frame's writes regardless).
        if (RecordBrickBakeSlices(commandBuffer: commandBuffer)) {
            recorder.MemoryBarrier(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: m_deviceHandle,
                sourceAccessMask: GpuComputeAccess.ShaderWrite,
                sourceStageMask: GpuComputeStage.ComputeShader
            );
        }

        // GPU timing: decide ONCE whether this frame is timed (available, and in live-armed mode also
        // GpuTimingControl.Shared.Armed), latch it for the submit paths, and lazily create the pools on the first armed
        // frame. Then this frame's rotating pool is reset and marked frame-start (top of pipe). The marks are
        // pixel-neutral, so the determinism/capture-hash parity gates are unaffected.
        m_frameTimingActive = (m_timingAvailable && (!m_liveArmedTiming || GpuTimingControl.Shared.Armed));

        if (m_frameTimingActive) {
            EnsureTimingPools();
        }

        var timingPool = (m_frameTimingActive
            ? m_timingPools![((int)(m_timingFrame % ((ulong)TimingPoolCount)))].PoolHandle
            : 0
        );

        if (0 != timingPool) {
            m_timingRecorder!.ResetTimestamps(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                firstQuery: 0,
                poolHandle: timingPool,
                queryCount: TimingCapacity
            );
            m_timingRecorder.WriteTimestamp(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                poolHandle: timingPool,
                queryIndex: 0,
                stageFlags: GpuTimingStage.TopOfPipe
            );
        }

        // Cadence gate: when this frame's inputs are byte-identical to the last RENDERED frame's
        // (DecideCadenceSkip proved it), SKIP the four render passes and fall straight through to the composite below —
        // which re-reads the RETAINED (single, ring-shared) views source textures + tile buffer the previous frame wrote
        // and re-composites them into the swapchain-bound output. Pixel-identical to a full re-render of these inputs;
        // the top-of-frame cross-frame barrier already orders this read after that previous frame's writes. Honest
        // timing: the skipped passes' closing marks are written back-to-back (queries 1..4), so each reports ~0 ms.
        if (!m_skipThisFrame) {
            // Sky pre-pass FIRST, before any tile is culled: fills every pixel of every non-child viewport's
            // render-dims source texture with the authored sky. Direct (not indirect) over a fixed
            // (imageExtent.x, imageExtent.y, viewportCapacity) grid — the largest any view's render-dims rect can
            // reach, per-thread bounds-checked against its own view's actual rectDims, matching the beam/instance-cull
            // dispatch style. Reuses Stage 1's own descriptor set (m_viewsSets) and push constant; a beam-culled
            // tile's pixel is otherwise never touched by any later pass, so this is the only writer that reaches it.
            recorder.BeginDebugGroup(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                label: "sky"
            );
            recorder.BindComputePipeline(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                pipelineHandle: m_skyPipeline.Handle
            );
            recorder.BindComputeDescriptorSet(
                commandBufferHandle: commandBuffer,
                descriptorSetHandle: m_viewsSets[m_currentSlot],
                deviceHandle: m_deviceHandle,
                pipelineLayoutHandle: m_skyPipeline.LayoutHandle
            );
            recorder.PushConstants(
                commandBufferHandle: commandBuffer,
                data: m_pushConstant,
                deviceHandle: m_deviceHandle,
                offset: 0,
                pipelineLayoutHandle: m_skyPipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Compute
            );
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                groupCountX: ((m_width + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountY: ((m_height + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountZ: viewportCount
            );
            recorder.EndDebugGroup(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle
            );

            WriteTimingMark(
                commandBuffer: commandBuffer,
                queryIndex: 1,
                timingPool: timingPool
            ); // close: sky pre-pass

            // Make the sky pass's source-texture writes visible to Stage 1's later read (the shadow-history alpha
            // lane) and write of the same images.
            recorder.MemoryBarrier(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: m_deviceHandle,
                sourceAccessMask: GpuComputeAccess.ShaderWrite,
                sourceStageMask: GpuComputeStage.ComputeShader
            );

            // Instance-cull pass (mask-first): one invocation per (tile, viewport) — bins the program's instances
            // against each tile's cone into the per-tile mask (the uniform-grid walk, or the flat loop when the program
            // packs no grid). Its OWN kernel so its register footprint never taxes the cone march's occupancy.
            recorder.BeginDebugGroup(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                label: "mask"
            );
            recorder.BindComputePipeline(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                pipelineHandle: m_instanceCullPipeline.Handle
            );
            recorder.BindComputeDescriptorSet(
                commandBufferHandle: commandBuffer,
                descriptorSetHandle: m_instanceCullSets[m_currentSlot],
                deviceHandle: m_deviceHandle,
                pipelineLayoutHandle: m_instanceCullPipeline.LayoutHandle
            );
            recorder.PushConstants(
                commandBufferHandle: commandBuffer,
                data: m_pushConstant,
                deviceHandle: m_deviceHandle,
                offset: 0,
                pipelineLayoutHandle: m_instanceCullPipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Compute
            );
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                groupCountX: ((m_tileGridX + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountY: ((m_tileGridY + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountZ: viewportCount
            );
            recorder.EndDebugGroup(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle
            );

            WriteTimingMark(
                commandBuffer: commandBuffer,
                queryIndex: 2,
                timingPool: timingPool
            ); // close: instance-mask cull

            // Make the instance-mask writes visible to the beam's cone march (it evaluates the tile-masked field).
            recorder.MemoryBarrier(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: m_deviceHandle,
                sourceAccessMask: GpuComputeAccess.ShaderWrite,
                sourceStageMask: GpuComputeStage.ComputeShader
            );

            // Tile-cull prepass: one invocation per (tile, viewport), cone-marching the tile-MASKED field.
            recorder.BeginDebugGroup(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                label: "beam"
            );
            recorder.BindComputePipeline(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                pipelineHandle: m_beamPipeline.Handle
            );
            recorder.BindComputeDescriptorSet(
                commandBufferHandle: commandBuffer,
                descriptorSetHandle: m_beamSets[m_currentSlot],
                deviceHandle: m_deviceHandle,
                pipelineLayoutHandle: m_beamPipeline.LayoutHandle
            );
            recorder.PushConstants(
                commandBufferHandle: commandBuffer,
                data: m_pushConstant,
                deviceHandle: m_deviceHandle,
                offset: 0,
                pipelineLayoutHandle: m_beamPipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Compute
            );
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                groupCountX: m_tileGridX,
                groupCountY: m_tileGridY,
                groupCountZ: viewportCount
            );
            recorder.EndDebugGroup(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle
            );

            WriteTimingMark(
                commandBuffer: commandBuffer,
                queryIndex: 3,
                timingPool: timingPool
            ); // close: beam prepass

            // Make the beam's tile writes visible to the cull-args reduction's (and Stage 1's) reads — a global memory
            // barrier (the mask writes are already visible from the first barrier; a second global one costs nothing more).
            recorder.MemoryBarrier(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: m_deviceHandle,
                sourceAccessMask: GpuComputeAccess.ShaderWrite,
                sourceStageMask: GpuComputeStage.ComputeShader
            );

            // Cull-args reduction (a single invocation): reduce the cull buffer to the surviving-tile bbox, writing Stage
            // 1's INDIRECT dispatch group counts + the bbox group origin — so the GPU, not the CPU, sizes the views grid.
            recorder.BeginDebugGroup(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                label: "cull-args"
            );
            recorder.BindComputePipeline(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                pipelineHandle: m_cullArgsPipeline.Handle
            );
            recorder.BindComputeDescriptorSet(
                commandBufferHandle: commandBuffer,
                descriptorSetHandle: m_cullArgsSet,
                deviceHandle: m_deviceHandle,
                pipelineLayoutHandle: m_cullArgsPipeline.LayoutHandle
            );
            recorder.PushConstants(
                commandBufferHandle: commandBuffer,
                data: m_pushConstant,
                deviceHandle: m_deviceHandle,
                offset: 0,
                pipelineLayoutHandle: m_cullArgsPipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Compute
            );
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                groupCountX: 1,
                groupCountY: 1,
                groupCountZ: 1
            );
            recorder.EndDebugGroup(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle
            );

            // Order the cull-args writes before Stage 1. The bbox ORIGIN (cullBounds) is an ordinary compute-shader read,
            // so a global memory barrier suffices.
            recorder.MemoryBarrier(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: m_deviceHandle,
                sourceAccessMask: GpuComputeAccess.ShaderWrite,
                sourceStageMask: GpuComputeStage.ComputeShader
            );

            WriteTimingMark(
                commandBuffer: commandBuffer,
                queryIndex: 4,
                timingPool: timingPool
            ); // close: cull-args reduction

            // The INDIRECT ARGS need a PER-RESOURCE transition into the indirect-argument state — a global barrier does not
            // prepare a specific buffer for ExecuteIndirect on Direct3D 12 (on Vulkan this is a memory barrier all the same).
            recorder.TransitionBuffer(
                bufferHandle: m_viewsArgsBuffer.BufferHandle,
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.IndirectCommandRead,
                destinationStageMask: GpuComputeStage.DrawIndirect,
                deviceHandle: m_deviceHandle,
                sourceAccessMask: GpuComputeAccess.ShaderWrite,
                sourceStageMask: GpuComputeStage.ComputeShader
            );

            // Stage 1: render each viewport's SDF camera into its own source texture — dispatched INDIRECTLY from the
            // GPU-computed surviving-tile bbox; the all-empty margins are never dispatched; the kernel offsets each
            // invocation by the bbox origin (binding 8). The pipeline is the variant UploadProgram selected for the LIVE
            // program (full ISA vs core-ops — the stripped cases are unreachable under core, so the field is the same;
            // see SdfViewsKernelVariant); the per-slot views set binds against either (identically defined layouts, same
            // bindings array).
            var viewsPipeline = (m_useCoreViews
                ? m_viewsCorePipeline
                : m_viewsPipeline
            );

            recorder.BeginDebugGroup(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                label: "views"
            );
            recorder.BindComputePipeline(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                pipelineHandle: viewsPipeline.Handle
            );
            recorder.BindComputeDescriptorSet(
                commandBufferHandle: commandBuffer,
                descriptorSetHandle: m_viewsSets[m_currentSlot],
                deviceHandle: m_deviceHandle,
                pipelineLayoutHandle: viewsPipeline.LayoutHandle
            );
            recorder.PushConstants(
                commandBufferHandle: commandBuffer,
                data: m_pushConstant,
                deviceHandle: m_deviceHandle,
                offset: 0,
                pipelineLayoutHandle: viewsPipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Compute
            );
            recorder.DispatchIndirect(
                argumentBufferHandle: m_viewsArgsBuffer.BufferHandle,
                argumentBufferOffset: 0,
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle
            );
            recorder.EndDebugGroup(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle
            );

            WriteTimingMark(
                commandBuffer: commandBuffer,
                queryIndex: 5,
                timingPool: timingPool
            ); // close: Stage 1 views

            // Make Stage 1's source writes visible to Stage 2's reads.
            recorder.MemoryBarrier(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: m_deviceHandle,
                sourceAccessMask: GpuComputeAccess.ShaderWrite,
                sourceStageMask: GpuComputeStage.ComputeShader
            );
        } else {
            // SKIPPED FRAME: no render passes ran, so close their five timing marks (queries 1..5) back-to-back — each
            // reports ~0 ms, the honest cost of a skipped pass — and fall through to the composite. The retained tile
            // buffer + source textures (single, ring-shared, left in General by the previous rendered frame) are ordered
            // for this frame's composite reads by the top-of-frame cross-frame barrier, so no extra barrier is needed.
            // The sky pre-pass is skipped too: its only inputs (viewports, sdfScreenLights) are already covered by the
            // signature that proved this frame identical to the last rendered one, so its retained output is still correct.
            WriteTimingMark(
                commandBuffer: commandBuffer,
                queryIndex: 1,
                timingPool: timingPool
            ); // close: sky pre-pass (skipped)
            WriteTimingMark(
                commandBuffer: commandBuffer,
                queryIndex: 2,
                timingPool: timingPool
            ); // close: instance-mask cull (skipped)
            WriteTimingMark(
                commandBuffer: commandBuffer,
                queryIndex: 3,
                timingPool: timingPool
            ); // close: beam prepass (skipped)
            WriteTimingMark(
                commandBuffer: commandBuffer,
                queryIndex: 4,
                timingPool: timingPool
            ); // close: cull-args reduction (skipped)
            WriteTimingMark(
                commandBuffer: commandBuffer,
                queryIndex: 5,
                timingPool: timingPool
            ); // close: Stage 1 views (skipped)
        }

        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderWrite,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            imageHandle: m_storageImage.ImageHandle,
            newLayout: GpuImageLayout.General,
            oldLayout: outputOldLayout,
            sourceAccessMask: outputSourceAccess,
            sourceStageMask: outputSourceStage
        );

        // Stage 2: composite each source into its screen region (indirect, from the host-written constant grid).
        recorder.BeginDebugGroup(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            label: "composite"
        );
        recorder.BindComputePipeline(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            pipelineHandle: m_compositePipeline.Handle
        );
        recorder.BindComputeDescriptorSet(
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: m_compositeSets[m_currentSlot],
            deviceHandle: m_deviceHandle,
            pipelineLayoutHandle: m_compositePipeline.LayoutHandle
        );
        recorder.PushConstants(
            commandBufferHandle: commandBuffer,
            data: m_compositePush,
            deviceHandle: m_deviceHandle,
            offset: 0,
            pipelineLayoutHandle: m_compositePipeline.LayoutHandle,
            stageFlags: GpuShaderStage.Compute
        );
        recorder.DispatchIndirect(
            argumentBufferHandle: m_compositeArgsBuffer.BufferHandle,
            argumentBufferOffset: 0,
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle
        );
        recorder.EndDebugGroup(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle
        );

        WriteTimingMark(
            commandBuffer: commandBuffer,
            queryIndex: 6,
            timingPool: timingPool
        ); // close: Stage 2 composite

        // Hand the output off in its consumer layout: shader-readable for a same-device consumer (compositor or
        // readback), or the cross-backend External handoff layout. Routing this through the recorder keeps its
        // per-resource state tracking the single source of truth.
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead,
            destinationStageMask: restingStage,
            deviceHandle: m_deviceHandle,
            imageHandle: m_storageImage.ImageHandle,
            newLayout: restingLayout,
            oldLayout: GpuImageLayout.General,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
        );

        // Copy the marks into the pool's readback storage (a no-op on Vulkan; the D3D12 ResolveQueryData) so they
        // submit and drain atomically with the frame.
        if (0 != timingPool) {
            m_timingRecorder!.ResolveTimestamps(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                firstQuery: 0,
                poolHandle: timingPool,
                queryCount: TimingMarkCount
            );
        }

        recorder.EndDebugGroup(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle
        ); // close the outer per-engine group
        recorder.EndCommandBuffer(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle
        );

        m_imageInitialized = true;
    }
    // Records this frame's carve-bake slices: for each Baking brick slot, one voxel slice of ≤ MaxBrickBakeVoxelsPerSlice,
    // advancing the slot's CPU cursor and flipping it to Ready once its whole brick is written. Returns whether ANY
    // slice was recorded (so Record inserts the pool-visibility barrier). A no-op when the pool is disabled or nothing
    // is baking — the bare room never pays it. Each slice is a plain direct dispatch of the standalone baker pipeline;
    // the bake writes are made visible to the render marches by the barrier Record adds after this returns true.
    private bool RecordBrickBakeSlices(nint commandBuffer) {
        if (m_brickBakePipeline is null) {
            return false;
        }

        var recorder = m_gpu.ComputeRecorder;
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
                recorder.BeginDebugGroup(
                    commandBufferHandle: commandBuffer,
                    deviceHandle: m_deviceHandle,
                    label: "brick-bake"
                );
            }

            push[0] = ((uint)m_brickVoxelCursor[slot]); push[1] = ((uint)sliceCount); push[2] = 0u; push[3] = 0u;

            recorder.BindComputePipeline(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                pipelineHandle: m_brickBakePipeline.Handle
            );
            recorder.BindComputeDescriptorSet(
                commandBufferHandle: commandBuffer,
                descriptorSetHandle: m_brickBakeSets[slot],
                deviceHandle: m_deviceHandle,
                pipelineLayoutHandle: m_brickBakePipeline.LayoutHandle
            );
            recorder.PushConstants(
                commandBufferHandle: commandBuffer,
                data: m_brickBakePush,
                deviceHandle: m_deviceHandle,
                offset: 0,
                pipelineLayoutHandle: m_brickBakePipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Compute
            );
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
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
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle
            );
        }

        return recorded;
    }
    // Writes a bottom-of-pipe closing timestamp for a pass, when timing is on.
    private void WriteTimingMark(nint timingPool, nint commandBuffer, uint queryIndex) {
        if (0 != timingPool) {
            m_timingRecorder!.WriteTimestamp(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                poolHandle: timingPool,
                queryIndex: queryIndex,
                stageFlags: GpuTimingStage.BottomOfPipe
            );
        }
    }
}
