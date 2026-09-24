namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    private static readonly SdfFramePass[] FrameUploadPasses = [
        SdfFramePass.UploadViewports,
        SdfFramePass.UploadDynamicTransforms,
        SdfFramePass.UploadInstanceGrid,
    ];

    private readonly SdfBufferEdge[] m_bufferEdges = new SdfBufferEdge[SdfFrameBufferPlan.MaxUsesPerPass];
    private readonly SdfFrameBufferHazards m_bufferHazards = new();

    // Records the buffer transitions SdfFrameBufferPlan says the pass owes before its dispatch. Image hazards (the
    // per-view sources, the output) keep their own barriers in Record.
    private void RecordBufferBarriers(nint commandBuffer, SdfFramePass pass) {
        var recorder = m_gpu.ComputeRecorder;
        var count = m_bufferHazards.Enter(
            edges: m_bufferEdges,
            pass: pass
        );

        for (var index = 0; (index < count); index++) {
            var edge = m_bufferEdges[index];

            recorder.TransitionBuffer(
                bufferHandle: FrameBufferHandle(buffer: edge.Buffer),
                commandBufferHandle: commandBuffer,
                destinationAccessMask: edge.DestinationAccess,
                destinationStageMask: edge.DestinationStage,
                deviceHandle: m_deviceHandle,
                sourceAccessMask: edge.SourceAccess,
                sourceStageMask: edge.SourceStage
            );
        }
    }
    private nint FrameBufferHandle(SdfFrameBuffer buffer) => (buffer switch {
        SdfFrameBuffer.Viewports => m_viewportDeviceBuffer,
        SdfFrameBuffer.DynamicTransforms => m_dynamicTransformDeviceBuffer,
        SdfFrameBuffer.InstanceGrid => m_instanceGridDeviceBuffer,
        SdfFrameBuffer.BrickPool => m_brickPoolBuffer,
        SdfFrameBuffer.InstanceMasks => m_instanceMaskBuffer,
        SdfFrameBuffer.Tiles => m_tileBuffer,
        SdfFrameBuffer.ViewsArgs => m_viewsArgsBuffer,
        SdfFrameBuffer.CullBounds => m_cullBoundsBuffer,
        SdfFrameBuffer.PrimaryHits => m_primaryHitBuffer,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: buffer,
            message: "Unknown SDF frame buffer.",
            paramName: nameof(buffer)
        ),
    }).BufferHandle;
}
