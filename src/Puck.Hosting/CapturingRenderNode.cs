using Puck.Abstractions.Capture;
using Puck.Abstractions.Presentation;
namespace Puck.Hosting;

/// <summary>
/// A backend-neutral present-tap: an <see cref="IRenderNode"/> decorator that passes the wrapped node's
/// produced <see cref="Surface"/> straight through to the parent while, on a configured cadence, handing a
/// copy to an <see cref="ICaptureSink"/>. This is the lossless source for capturing the engine's own output —
/// it works for every backend because it only touches the neutral <see cref="Surface"/>.
/// <para>
/// It captures the CPU-pixel surface variant: wrap a node configured to produce CPU pixels (the same mode the
/// engine already uses to cross a device boundary). A GPU/shared-handle surface is passed through uncaptured,
/// since reading it back would need the source image handle the neutral <see cref="Surface"/> does not expose.
/// </para>
/// </summary>
public sealed class CapturingRenderNode : IRenderNode {
    private long m_capturedFrameCount;
    private bool m_faulted;
    private readonly int m_frameStep;
    private readonly IRenderNode m_inner;
    private long m_lastCapturedSourceFrame = -1L;
    private readonly int m_maxFrames;
    private readonly ICaptureSink m_sink;
    private long m_sourceFrameCount;

    /// <summary>Initializes a new instance of the <see cref="CapturingRenderNode"/> class.</summary>
    /// <param name="inner">The node whose output is tapped and passed through.</param>
    /// <param name="sink">The sink captured frames are handed to.</param>
    /// <param name="options">The capture cadence and frame budget.</param>
    public CapturingRenderNode(IRenderNode inner, ICaptureSink sink, CaptureOptions options) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);

        m_frameStep = Math.Max(
            val1: 1,
            val2: (int)Math.Round(a: ((double)options.SourceFrameRate / Math.Max(
                val1: 1,
                val2: options.FrameRate
            )))
        );
        m_inner = inner;
        m_maxFrames = options.MaxFrames;
        m_sink = sink;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_inner.Descriptor;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        var surface = m_inner.ProduceFrame(context: context);

        if (
            !m_faulted &&
            surface.IsCpuPixels &&
            ShouldCaptureThisFrame()
        ) {
            try {
                m_sink.Consume(frame: new CaptureFrame(
                    FrameIndex: m_capturedFrameCount,
                    Surface: surface,
                    TimestampTicks: context.RenderTicks
                ));
                m_capturedFrameCount++;
                m_lastCapturedSourceFrame = m_sourceFrameCount;
            } catch (Exception exception) {
                // A capture must never take the render loop down with it; disable the tap and report once.
                m_faulted = true;
                Console.Error.WriteLine(value: $"capture | tap disabled after error: {exception.Message}");
            }
        }

        m_sourceFrameCount++;
        return surface;
    }
    /// <inheritdoc/>
    public void Dispose() {
        // Owns the wrapped node (it replaced the root in the tree); the sink's lifetime belongs to its owner.
        m_inner.Dispose();
    }

    private bool ShouldCaptureThisFrame() {
        if (
            (m_maxFrames > 0) &&
            (m_capturedFrameCount >= m_maxFrames)
        ) {
            return false;
        }

        return (
            (m_lastCapturedSourceFrame < 0L) ||
            ((m_sourceFrameCount - m_lastCapturedSourceFrame) >= m_frameStep)
        );
    }
}
