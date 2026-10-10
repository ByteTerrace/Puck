using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The upload: one fenced submission a frame, recorded by the frame's earliest pass, ahead of every view's submission. It takes the next ring slot, waits the previous upload's fence, which signals once every submission queued
// before it has finished, the views that read this slot two uploads earlier among them, flushes the slot's share of every
// region, and records the fillers' first transitions, a queued host-baked brick, this frame's carve-bake slices, the
// regions' copies, each handing what it wrote to every shader stage that reads it in a later submission. The graph's
// environment producer runs afterward, once its panorama images have been acquired.
public sealed partial class SdfWorldTables {
    /// <summary>Gets the ring slot the latest upload wrote, whose region buffers the frame's passes bind, or -1 before the
    /// first upload.</summary>
    public int CurrentSlot => m_currentSlot;

    /// <summary>Submits the frame's upload: waits the previous upload, flushes and copies what every region owes into
    /// the next ring slot, and records a queued brick upload and the carve bake's slices, then submits it fenced. Nothing
    /// here waits for the upload itself: the views' submissions that follow it on the queue read what it wrote.</summary>
    /// <exception cref="ObjectDisposedException">The tables are disposed.</exception>
    public void SubmitUpload() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        // Publishes the newest upload whose fence has already signaled.
        m_work.Poll();

        var slot = ((int)(m_uploads % FrameRingSize));

        // Waiting an upload completes it in the ledger before another upload reuses its ring slot.
        if (m_uploads > 0UL) {
            m_frameFences[((int)((m_uploads - 1UL) % FrameRingSize))].Wait();
        }

        var previousSlot = m_currentSlot;

        m_currentSlot = slot;
        m_uploads++;

        var recorder = m_gpu.Recorder;
        var commandBuffer = m_commandPools[slot].CommandBufferHandle;

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer);
        // The outer debug-marker group scoping this residency's upload (see DebugLabel); pixel-neutral.
        recorder.BeginDebugGroup(
            commandBufferHandle: commandBuffer,
            label: DebugLabel
        );
        if (m_fillersInitialized) {
            m_work.SkipPass(pass: FillersPass);
        } else {
            m_work.EnterPass(pass: FillersPass);
            InitializeFillers(commandBuffer: commandBuffer);
            m_work.LeavePass();
        }
        if (WritesBricks()) {
            m_work.EnterPass(pass: BricksPass);
            RecordBrickWrites(commandBuffer: commandBuffer);
            m_work.LeavePass();
        } else {
            m_work.SkipPass(pass: BricksPass);
        }
        m_work.EnterPass(pass: UploadPass);
        RecordIndirectClear(commandBuffer: commandBuffer);
        RecordPreviousTables(commandBuffer: commandBuffer, previousSlot: previousSlot);
        RecordRegionCopies();
        CompletePreviousTables(commandBuffer: commandBuffer);
        m_work.LeavePass();
        RecordSkyEnvironmentDecision();
        recorder.EndDebugGroup(commandBufferHandle: commandBuffer);
        recorder.EndCommandBuffer(commandBufferHandle: commandBuffer);
        m_gpu.QueueSubmitter.Submit(
            commandBufferHandles: [commandBuffer],
            fence: m_frameFences[slot]
        );
    }

    // Waits until every upload in flight has retired: what a rewrite of the only table no view reads, a bake request,
    // pays.
    private void WaitForUploads() {
        foreach (var fence in m_frameFences) {
            fence.Wait();
        }
    }
    // Moves the fillers from their first, undefined layout to the ones a pass binds them in, once, before any pass can
    // bind them: the sampled filler shader-readable and the storage filler General, both cleared, so a screen whose source
    // is missing from a frame samples black. The incoming storage filler moves to General uncleared: a pass binding it
    // reads a zero fade count (SdfFrameBlock.WriteWithoutFades), so no kernel reads or writes it.
    private void InitializeFillers(nint commandBuffer) {
        var recorder = m_gpu.Recorder;

        foreach (var filler in ((ReadOnlySpan<IGpuImage>)[m_sampledFiller, m_storageFiller])) {
            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuAccess.TransferWrite,
                destinationStageMask: GpuStage.Transfer,
                imageHandle: filler.ImageHandle,
                newLayout: GpuImageLayout.General,
                oldLayout: GpuImageLayout.Undefined,
                sourceAccessMask: GpuAccess.None,
                sourceStageMask: GpuStage.TopOfPipe
            );
            recorder.ClearStorageImage(
                commandBufferHandle: commandBuffer,
                format: Format,
                imageHandle: filler.ImageHandle
            );
        }

        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead,
            destinationStageMask: GpuStage.ComputeShader,
            imageHandle: m_sampledFiller.ImageHandle,
            newLayout: GpuImageLayout.ShaderReadOnly,
            oldLayout: GpuImageLayout.General,
            sourceAccessMask: GpuAccess.TransferWrite,
            sourceStageMask: GpuStage.Transfer
        );
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite,
            destinationStageMask: GpuStage.ComputeShader,
            imageHandle: m_storageFiller.ImageHandle,
            newLayout: GpuImageLayout.General,
            oldLayout: GpuImageLayout.General,
            sourceAccessMask: GpuAccess.TransferWrite,
            sourceStageMask: GpuStage.Transfer
        );
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite,
            destinationStageMask: GpuStage.ComputeShader,
            imageHandle: m_incomingStorageFiller.ImageHandle,
            newLayout: GpuImageLayout.General,
            oldLayout: GpuImageLayout.Undefined,
            sourceAccessMask: GpuAccess.None,
            sourceStageMask: GpuStage.TopOfPipe
        );
        m_fillersInitialized = true;
    }
    // Whether this upload writes the brick pool: a queued host-baked brick, or a carve bake in progress.
    private bool WritesBricks() => (
        ((m_brickRegion is not null) && (m_brickUploads.Count > 0)) ||
        AnyBrickBaking()
    );
    // The brick pool's writes this upload, bracketed by its barriers: every earlier view's read of the pool before the
    // writes, and the writes before every later view's read.
    private void RecordBrickWrites(nint commandBuffer) {
        var recorder = m_gpu.Recorder;

        recorder.TransitionBuffer(
            bufferHandle: m_brickPoolBuffer.BufferHandle,
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderWrite,
            destinationStageMask: GpuStage.ComputeShader,
            sourceAccessMask: GpuAccess.ShaderRead,
            sourceStageMask: GpuStage.ComputeShader
        );
        RecordBrickUpload(commandBuffer: commandBuffer);
        RecordBrickBakeSlices(commandBuffer: commandBuffer);
        recorder.TransitionBuffer(
            bufferHandle: m_brickPoolBuffer.BufferHandle,
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead,
            destinationStageMask: GpuStage.ComputeShader,
            sourceAccessMask: GpuAccess.ShaderWrite,
            sourceStageMask: GpuStage.ComputeShader
        );
    }
    // One queued host-baked brick per upload: the brick staging region, retargeted at the brick's slot in the pool, owes
    // every voxel written and copies them there through the device's region-copy pipeline.
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
    // Records this upload's carve-bake slices: for each Baking brick slot, one voxel slice of ≤ MaxBrickBakeVoxelsPerSlice,
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
                recorder.BeginDebugGroup(
                    commandBufferHandle: commandBuffer,
                    label: "brick-bake"
                );
            }

            // The slice ordinal: every slice but a brick's last is whole, so the cursor is a whole number of slices, and the
            // kernel derives the slice's start and count from the ordinal, its block's extent and the request's total.
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
                descriptorSetHandle: m_brickBakeFrameSet,
                group: FrameGroup,
                pipelineLayoutHandle: m_brickBakePipeline.LayoutHandle
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
