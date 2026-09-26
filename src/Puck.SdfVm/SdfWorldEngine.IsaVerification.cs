using System.Buffers.Binary;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    private ReadOnlyMemory<byte> DispatchIsaReport(IGpuComputePipeline viewsPipeline, IGpuImage reportImage, IGpuImage sampledImage, IGpuSurfaceReadback readback, bool initializeImages) {
        var commandBuffer = m_commandPools[0].CommandBufferHandle;
        var recorder = m_gpu.Recorder;

        recorder.BeginCommandBuffer(
            commandBufferHandle: commandBuffer
        );
        m_bufferHazards.Reset();
        // The report dispatch binds a views set, which binds the mesh visibility target: the construction's handshake is
        // the first submission to reach it.
        InitializeMeshTarget(commandBuffer: commandBuffer);

        if (initializeImages) {
            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuAccess.ShaderRead,
                destinationStageMask: GpuStage.ComputeShader,
                imageHandle: sampledImage.ImageHandle,
                newLayout: GpuImageLayout.ShaderReadOnly,
                oldLayout: GpuImageLayout.Undefined,
                sourceAccessMask: GpuAccess.None,
                sourceStageMask: GpuStage.TopOfPipe
            );
        }

        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderWrite,
            destinationStageMask: GpuStage.ComputeShader,
            imageHandle: reportImage.ImageHandle,
            newLayout: GpuImageLayout.General,
            oldLayout: (initializeImages
            ? GpuImageLayout.Undefined
            : GpuImageLayout.ShaderReadOnly),
            sourceAccessMask: (initializeImages
            ? GpuAccess.None
            : GpuAccess.ShaderRead),
            sourceStageMask: (initializeImages
            ? GpuStage.TopOfPipe
            : GpuStage.ComputeShader)
        );

        if (initializeImages) {
            RecordBufferBarriers(
                commandBuffer: commandBuffer,
                pass: SdfFramePass.Beam
            );
            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                pipelineHandle: m_beamPipeline.Handle
            );
            BindWorldGroups(
                commandBuffer: commandBuffer,
                frameSet: m_frameSets[0],
                pipeline: m_beamPipeline,
                viewsSet: m_viewsSets[0][0]
            );
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                groupCountX: 1,
                groupCountY: 1,
                groupCountZ: 1
            );
        }

        // Every hit kernel reads the beam's ISA word from the cull buffer through the views layout's read-only
        // binding, so the views pass's transitions order the beam's write before the report.
        RecordBufferBarriers(
            commandBuffer: commandBuffer,
            pass: SdfFramePass.Views
        );
        recorder.BindPipeline(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            pipelineHandle: viewsPipeline.Handle
        );
        BindWorldGroups(
            commandBuffer: commandBuffer,
            frameSet: m_frameSets[0],
            pipeline: viewsPipeline,
            viewsSet: m_viewsSets[0][0]
        );
        recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            groupCountX: 1,
            groupCountY: 1,
            groupCountZ: 1
        );
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead,
            destinationStageMask: GpuStage.ComputeShader,
            imageHandle: reportImage.ImageHandle,
            newLayout: GpuImageLayout.ShaderReadOnly,
            oldLayout: GpuImageLayout.General,
            sourceAccessMask: GpuAccess.ShaderWrite,
            sourceStageMask: GpuStage.ComputeShader
        );
        recorder.EndCommandBuffer(
            commandBufferHandle: commandBuffer
        );
        m_gpu.QueueSubmitter.SubmitAndWait(
            commandBufferHandles: [commandBuffer]
        );

        return readback.Read(
            bytesPerPixel: 4,
            format: Format,
            height: 1,
            sourceImageHandle: reportImage.ImageHandle,
            sourceLayout: GpuImageLayout.ShaderReadOnly,
            width: 1
        );
    }
    private void VerifyIsaVersion() {
        using var reportImage = m_gpu.ImageFactory.Create(
            format: Format,
            name: NameOf(part: "isa-report"),
            height: 1,
            usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
            width: 1
        );
        using var sampledImage = m_gpu.ImageFactory.Create(
            format: Format,
            name: NameOf(part: "isa-sampled"),
            height: 1,
            usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
            width: 1
        );
        using var readback = m_gpu.SurfaceTransferFactory.CreateReadback();
        // The report borrows ring slot 0's view-0 set: its output binding takes the report image, rebound to the view's
        // output by the next frame that renders it.
        var viewsSet = m_viewsSets[0][0];

        m_bindings.WriteStorageImage(
            arrayElement: 0,
            binding: OutputBinding,
            descriptorSetHandle: viewsSet,
            imageViewHandle: reportImage.ImageViewHandle
        );
        m_boundOutputViews[0][0] = 0;

        foreach (var binding in ScreenSourceBindings) {
            m_bindings.WriteSampledImage(
                arrayElement: 0,
                binding: binding,
                descriptorSetHandle: viewsSet,
                imageViewHandle: sampledImage.ImageViewHandle
            );
        }

        m_bindings.WriteSampledImage(
            arrayElement: 0,
            binding: GlyphAtlasBinding,
            descriptorSetHandle: viewsSet,
            imageViewHandle: sampledImage.ImageViewHandle
        );
        // The report request rides view 0's world block in ring slot 0, beside a frame block. The regions keep what each
        // slot holds, so the next frame that renders view 0 in that slot sends its own block over this one.
        Span<byte> block = stackalloc byte[m_viewBlocks[0].ByteCount];

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: block[SampleIndexOffset..],
            value: SdfShaderSetVerification.ReportRequest
        );
        _ = m_viewBlocks[0].Write(
            bytes: block,
            offset: 0
        );
        m_viewBlocks[0].Flush(slot: 0);
        WriteFrameBlock(
            frame: 0UL,
            slot: 0
        );

        var report = DispatchIsaReport(
            initializeImages: true,
            readback: readback,
            reportImage: reportImage,
            sampledImage: sampledImage,
            viewsPipeline: m_viewsPipeline
        );

        SdfShaderSetVerification.ValidateReport(
            report: report.Span,
            viewsVariant: "full views"
        );

        report = DispatchIsaReport(
            initializeImages: false,
            readback: readback,
            reportImage: reportImage,
            sampledImage: sampledImage,
            viewsPipeline: m_viewsCorePipeline
        );
        SdfShaderSetVerification.ValidateReport(
            report: report.Span,
            viewsVariant: "core views"
        );
        report = DispatchIsaReport(
            initializeImages: false,
            readback: readback,
            reportImage: reportImage,
            sampledImage: sampledImage,
            viewsPipeline: m_viewsFoldsPipeline
        );
        SdfShaderSetVerification.ValidateReport(
            report: report.Span,
            viewsVariant: "fold views"
        );
        report = DispatchIsaReport(
            initializeImages: false,
            readback: readback,
            reportImage: reportImage,
            sampledImage: sampledImage,
            viewsPipeline: m_primaryPipeline
        );
        SdfShaderSetVerification.ValidateReport(
            report: report.Span,
            viewsVariant: "primary traversal"
        );
        report = DispatchIsaReport(
            initializeImages: false,
            readback: readback,
            reportImage: reportImage,
            sampledImage: sampledImage,
            viewsPipeline: m_surfacePipeline
        );
        SdfShaderSetVerification.ValidateReport(
            report: report.Span,
            viewsVariant: "surface evaluation"
        );
        report = DispatchIsaReport(
            initializeImages: false,
            readback: readback,
            reportImage: reportImage,
            sampledImage: sampledImage,
            viewsPipeline: m_ambientPipeline
        );
        SdfShaderSetVerification.ValidateReport(
            report: report.Span,
            viewsVariant: "ambient occlusion"
        );
    }
}
