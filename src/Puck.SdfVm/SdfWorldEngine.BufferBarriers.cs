namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    private readonly SdfBufferEdge[] m_bufferEdges = new SdfBufferEdge[SdfFrameBufferPlan.MaxUsesPerPass];
    private readonly SdfFrameBufferHazards m_bufferHazards = new();

    // Records the buffer transitions SdfFrameBufferPlan says the pass owes before its dispatch. Image hazards (the
    // per-view sources, the output) keep their own barriers in Record, and the regions' copies theirs in
    // RecordRegionCopies.
    private void RecordBufferBarriers(nint commandBuffer, SdfFramePass pass) {
        var recorder = m_gpu.Recorder;
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
                sourceAccessMask: edge.SourceAccess,
                sourceStageMask: edge.SourceStage
            );
        }
    }
    private nint FrameBufferHandle(SdfFrameBuffer buffer) => (buffer switch {
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
