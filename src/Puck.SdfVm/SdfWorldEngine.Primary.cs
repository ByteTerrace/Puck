using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    // Primary, surface, ambient and views share live rectangles, masks and the indirect bbox. Every active
    // shaded pixel reads a record written this frame. The pass's buffer transitions order the previous hit pass's
    // record writes before this one. The set is the dispatch set's view's views set.
    private void RecordHitPass(nint commandBuffer, nint viewsSet, IGpuComputePipeline pipeline, SdfFramePass pass, string label, int workPass) {
        var recorder = m_gpu.Recorder;

        m_work.EnterPass(pass: workPass);
        RecordBufferBarriers(
            commandBuffer: commandBuffer,
            pass: pass
        );
        recorder.BeginDebugGroup(
            commandBufferHandle: commandBuffer,
            label: label
        );
        recorder.BindPipeline(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            pipelineHandle: pipeline.Handle
        );
        recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: viewsSet,
            group: 0,
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
        recorder.PushConstants(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            data: m_viewPush,
            offset: 0,
            pipelineLayoutHandle: pipeline.LayoutHandle,
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
}
