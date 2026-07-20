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
    private readonly Func<bool>? m_captureGate;
    private readonly Func<ReadOnlyMemory<byte>>? m_cpuReadback;
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
    /// <param name="captureGate">An optional predicate polled each frame: when it returns <see langword="false"/> the
    /// tap does no work at all (no readback, no consume), so a tap can be left in the tree and cost nothing until a
    /// consumer arms it. <see langword="null"/> means always active.</param>
    /// <param name="cpuReadback">An optional readback that returns the just-produced frame's CPU pixels (tightly packed
    /// RGBA8) — supplied when the wrapped node hands GPU surfaces the neutral <see cref="Surface"/> cannot expose for
    /// readback (the live windowed present path). When the produced surface is already CPU pixels this is ignored; when
    /// it is a GPU surface and this is <see langword="null"/> the frame passes through uncaptured, as before. An empty
    /// return skips the frame. The returned memory is copied by the sink before the next produce, so a reused staging
    /// buffer is fine.</param>
    public CapturingRenderNode(IRenderNode inner, ICaptureSink sink, CaptureOptions options, Func<bool>? captureGate = null, Func<ReadOnlyMemory<byte>>? cpuReadback = null) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);

        m_captureGate = captureGate;
        m_cpuReadback = cpuReadback;
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
            (m_captureGate?.Invoke() ?? true) &&
            ShouldCaptureThisFrame() &&
            TryResolveCaptureSurface(produced: surface, captured: out var captured)
        ) {
            try {
                m_sink.Consume(frame: new CaptureFrame(
                    FrameIndex: m_capturedFrameCount,
                    Surface: captured,
                    // The fixed-step simulation clock (whole update steps), NOT RenderTicks: RenderTicks folds in
                    // AccumulatorTicks — engine ticks elapsed but not yet consumed, a wall-clock-paced residue — which
                    // would make the sim-clock recording's timestamps vary run to run. ElapsedTicks is the deterministic
                    // engine tick base the CaptureFrame contract promises; the wall clock is measured separately (QPC)
                    // by the session for RecordingClock.Wall.
                    TimestampTicks: context.ElapsedTicks
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
    public void OnDeviceLost() {
        // This node hosts the wrapped node, so it must forward device-loss recovery down the tree (the neutral-Surface
        // tap owns no device resources of its own).
        m_inner.OnDeviceLost();
    }

    /// <inheritdoc/>
    public void Dispose() {
        // Owns the wrapped node (it replaced the root in the tree); the sink's lifetime belongs to its owner.
        m_inner.Dispose();
    }

    // Resolves the CPU-pixel surface to hand the sink: the produced surface directly when it is already CPU pixels, or
    // a readback of the GPU surface when a readback delegate is supplied (the live present path). Returns false — skip
    // this frame — for a GPU surface with no readback, or an empty readback.
    private bool TryResolveCaptureSurface(in Surface produced, out Surface captured) {
        if (produced.IsCpuPixels) {
            captured = produced;

            return true;
        }

        if (m_cpuReadback is not null) {
            var pixels = m_cpuReadback();

            if (!pixels.IsEmpty) {
                captured = new Surface(
                    ImageViewHandle: 0,
                    Width: produced.Width,
                    Height: produced.Height,
                    Format: produced.Format,
                    Pixels: pixels
                );

                return true;
            }
        }

        captured = default;

        return false;
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
