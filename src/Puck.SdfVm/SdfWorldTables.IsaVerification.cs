using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The ISA handshake: every per-view kernel of the loaded set, dispatched once in report mode against a pass set of the
// tables' own pool, writes the ISA version it was built against into a one-pixel report the host reads back and checks
// (SdfShaderSetVerification). The beam writes its word into a scratch cull buffer the hit kernels read, so the report
// runs the beam first. The handshake binds the tables' World set of ring slot 0 and a pass set of scratch sized for one
// tile of one view.
public sealed partial class SdfWorldTables {
    // The handshake's frame and pass sets, allocated from the tables' pool once and rewritten by each handshake.
    private readonly nint m_isaFrameSet;
    private readonly nint m_isaPassSet;

    private ReadOnlyMemory<byte> DispatchIsaReport(IGpuComputePipeline pipeline, IGpuImage reportImage, IGpuImage sampledImage, IGpuSurfaceReadback readback, IGpuBuffer tiles, bool initializeImages) {
        var commandBuffer = m_commandPools[0].CommandBufferHandle;
        var recorder = m_gpu.Recorder;

        // Every earlier upload recorded into the pool is complete before the handshake reuses it.
        WaitForUploads();
        recorder.BeginCommandBuffer(
            commandBufferHandle: commandBuffer
        );
        // The World set binds the sampled filler at every mesh atlas until a frame draws a textured mesh, so the fillers
        // reach their resting layouts before the handshake's first dispatch rather than with the first upload.
        InitializeFillers(commandBuffer: commandBuffer);

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
            var beam = m_pipelines.Pipeline(index: BeamPipelineIndex);

            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: commandBuffer,
                pipelineHandle: beam.Handle
            );
            BindIsaSets(
                commandBuffer: commandBuffer,
                pipeline: beam
            );
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                groupCountX: 1,
                groupCountY: 1,
                groupCountZ: 1
            );
            // Every hit kernel reads the beam's ISA word from the cull buffer.
            recorder.TransitionBuffer(
                bufferHandle: tiles.BufferHandle,
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuAccess.ShaderRead,
                destinationStageMask: GpuStage.ComputeShader,
                sourceAccessMask: GpuAccess.ShaderWrite,
                sourceStageMask: GpuStage.ComputeShader
            );
        }

        recorder.BindPipeline(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            pipelineHandle: pipeline.Handle
        );
        BindIsaSets(
            commandBuffer: commandBuffer,
            pipeline: pipeline
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
    private void BindIsaSets(nint commandBuffer, IGpuComputePipeline pipeline) {
        var recorder = m_gpu.Recorder;

        recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: m_isaFrameSet,
            group: FrameGroup,
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
        recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: WorldSet(slot: 0),
            group: WorldGroup,
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
        recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: m_isaPassSet,
            group: PassGroup,
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
    }
    private void VerifyIsaVersion() {
        using var scope = new GpuCreationScope();
        var reportImage = scope.Own(created: m_gpu.ImageFactory.Create(
            format: Format,
            name: NameOf(part: "isa-report"),
            height: 1,
            usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
            width: 1
        ));
        var sampledImage = scope.Own(created: m_gpu.ImageFactory.Create(
            format: Format,
            name: NameOf(part: "isa-sampled"),
            height: 1,
            usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
            width: 1
        ));
        var readback = scope.Own(created: m_gpu.SurfaceTransferFactory.CreateReadback());
        var parameters = SdfWorldInterfaces.WorldParameters;
        var frameBlock = scope.Own(created: m_gpu.BufferFactory.CreateHostVisible(
            name: NameOf(detail: "frame block", part: "isa"),
            sizeBytes: ((ulong)UniformBytes(blockBytes: parameters.FrameBlockSizeBytes)),
            usage: GpuBufferUsage.Uniform
        ));
        var passBlock = scope.Own(created: m_gpu.BufferFactory.CreateHostVisible(
            name: NameOf(detail: "pass block", part: "isa"),
            sizeBytes: ((ulong)UniformBytes(blockBytes: parameters.SizeBytes)),
            usage: GpuBufferUsage.Uniform
        ));
        // Scratch for one tile of one view: the cull buffer's tile planes and part bounds, the masks, the dispatch box,
        // the indirect arguments and one visibility record.
        var tiles = Scratch(bytes: ((SdfWorldPackage.TilePlaneCount + (SdfWorldPackage.PartBoundFloatCount * ((ulong)m_instanceCapacity))) * sizeof(float)), part: "isa-tiles");
        var masks = Scratch(bytes: (((ulong)SdfProgram.InstanceMaskStorageWordCountFor(instanceCount: m_instanceCapacity)) * sizeof(uint)), part: "isa-masks");
        var cullBounds = Scratch(bytes: SdfWorldPackage.CullBoundsByteLength, part: "isa-cull-bounds");
        var arguments = Scratch(bytes: ShaderPipelineDispatch.ArgumentBytes, part: "isa-arguments");
        var visibility = Scratch(bytes: SdfWorldPackage.VisibilityRecordByteLength, part: "isa-visibility");

        IGpuBuffer Scratch(ulong bytes, string part) => scope.Own(created: m_gpu.BufferFactory.CreateDeviceLocal(
            name: NameOf(part: part),
            sizeBytes: bytes,
            usage: GpuBufferUsage.Storage | GpuBufferUsage.Indirect
        ));

        // The report request rides the pass block beside a frame block of frame zero.
        var frameBytes = new byte[parameters.FrameBlockSizeBytes];
        var passBytes = new byte[parameters.SizeBytes];

        parameters.WriteFrame(
            block: frameBytes,
            frame: 0UL,
            values: default
        );
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: passBytes.AsSpan(start: ((int)parameters.BlockOffsetOf(member: SdfWorldPackage.SampleIndex))),
            value: SdfShaderSetVerification.ReportRequest
        );
        frameBlock.Write<byte>(data: frameBytes);
        passBlock.Write<byte>(data: passBytes);
        m_bindings.WriteConstantBuffer(
            arrayElement: 0,
            binding: 0,
            bufferHandle: frameBlock.BufferHandle,
            bufferSize: frameBlock.SizeBytes,
            descriptorSetHandle: m_isaFrameSet
        );
        m_bindings.WriteConstantBuffer(
            arrayElement: 0,
            binding: 0,
            bufferHandle: passBlock.BufferHandle,
            bufferSize: passBlock.SizeBytes,
            descriptorSetHandle: m_isaPassSet
        );
        WriteWorldBuffer(buffer: tiles, member: SdfWorldPackage.Tiles, set: m_isaPassSet);
        WriteWorldBuffer(buffer: tiles, member: SdfWorldPackage.TilesWritten, set: m_isaPassSet);
        WriteWorldBuffer(buffer: masks, member: SdfWorldPackage.InstanceMasks, set: m_isaPassSet);
        WriteWorldBuffer(buffer: masks, member: SdfWorldPackage.InstanceMasksWritten, set: m_isaPassSet);
        WriteWorldBuffer(buffer: cullBounds, member: SdfWorldPackage.CullBounds, set: m_isaPassSet);
        WriteWorldBuffer(buffer: cullBounds, member: SdfWorldPackage.CullBoundsWritten, set: m_isaPassSet);
        WriteWorldBuffer(buffer: arguments, member: SdfWorldPackage.ViewsArgsWritten, set: m_isaPassSet);
        WriteWorldBuffer(buffer: visibility, member: SdfWorldPackage.VisibilityRecords, set: m_isaPassSet);
        WriteWorldBuffer(buffer: visibility, member: SdfWorldPackage.VisibilityRecordsWritten, set: m_isaPassSet);
        m_bindings.WriteStorageImage(
            arrayElement: 0,
            binding: WorldBinding(member: SdfWorldPackage.Output),
            descriptorSetHandle: m_isaPassSet,
            imageViewHandle: reportImage.ImageViewHandle
        );
        m_bindings.WriteSampledImage(
            arrayElement: 0,
            binding: WorldBinding(member: SdfWorldPackage.MeshVisibility),
            descriptorSetHandle: m_isaPassSet,
            imageViewHandle: sampledImage.ImageViewHandle
        );

        for (var screen = 0u; (screen < MaxScreenSurfaces); screen++) {
            m_bindings.WriteSampledImage(
                arrayElement: screen,
                binding: WorldBinding(member: SdfWorldPackage.ScreenSources),
                descriptorSetHandle: m_isaPassSet,
                imageViewHandle: sampledImage.ImageViewHandle
            );
        }

        var first = true;

        foreach (var (index, variant) in ((ReadOnlySpan<(int, string)>)[
            (ViewsPipelineIndex, "full views"),
            (ViewsCorePipelineIndex, "core views"),
            (ViewsFoldsPipelineIndex, "fold views"),
            (PrimaryPipelineIndex, "primary traversal"),
            (SurfacePipelineIndex, "surface evaluation"),
            (AmbientPipelineIndex, "ambient occlusion"),
        ])) {
            var report = DispatchIsaReport(
                initializeImages: first,
                pipeline: m_pipelines.Pipeline(index: index),
                readback: readback,
                reportImage: reportImage,
                sampledImage: sampledImage,
                tiles: tiles
            );

            SdfShaderSetVerification.ValidateReport(
                report: report.Span,
                viewsVariant: variant
            );
            first = false;
        }
    }
}
