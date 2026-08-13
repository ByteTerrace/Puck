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
    private long m_cadenceAccumulator;
    private bool m_captureWasActive;
    private bool m_captureDue;
    private readonly Func<bool>? m_captureGate;
    private readonly Func<ReadOnlyMemory<byte>>? m_cpuReadback;
    private bool m_faulted;
    private readonly int m_frameRate;
    private readonly IRenderNode m_inner;
    private readonly int m_maxFrames;
    private readonly ICaptureSink m_sink;
    private readonly int m_sourceFrameRate;

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
    /// <exception cref="ArgumentNullException"><paramref name="inner"/>, <paramref name="sink"/>, or
    /// <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="CaptureOptions.FrameRate"/> or
    /// <see cref="CaptureOptions.SourceFrameRate"/> is less than one, or <see cref="CaptureOptions.MaxFrames"/> is
    /// negative.</exception>
    public CapturingRenderNode(IRenderNode inner, ICaptureSink sink, CaptureOptions options, Func<bool>? captureGate = null, Func<ReadOnlyMemory<byte>>? cpuReadback = null) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: options.FrameRate, other: 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: options.SourceFrameRate, other: 1);
        ArgumentOutOfRangeException.ThrowIfNegative(value: options.MaxFrames);

        m_captureGate = captureGate;
        m_cpuReadback = cpuReadback;
        m_frameRate = Math.Min(val1: options.FrameRate, val2: options.SourceFrameRate);
        m_inner = inner;
        m_maxFrames = options.MaxFrames;
        m_sink = sink;
        m_sourceFrameRate = options.SourceFrameRate;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_inner.Descriptor;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        var surface = m_inner.ProduceFrame(context: context);

        if (!m_faulted) {
            try {
                var active = (m_captureGate?.Invoke() ?? true);

                if (!active) {
                    // A newly armed capture starts with the first available frame rather than inheriting a partial cadence
                    // interval from an earlier session.
                    m_captureWasActive = false;
                    m_captureDue = true;
                } else if (ShouldCaptureThisFrame() && TryResolveCaptureSurface(produced: surface, captured: out var captured)) {
                    m_captureWasActive = true;
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
                    m_captureDue = false;
                }
            } catch (Exception exception) {
                // Every capture-only callback is isolated from the render loop; disable the tap and best-effort report
                // once. A closed/erroring diagnostic stream must not let the reporting path defeat that isolation.
                m_faulted = true;

                try {
                    Console.Error.WriteLine(value: $"capture | tap disabled after error: {exception.Message}");
                } catch (Exception) {
                    // The tap is already disabled; there is no second error channel that is safe on the render thread.
                }
            }
        }

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

        if (!m_captureWasActive) {
            m_cadenceAccumulator = 0L;
            m_captureDue = true;
        } else if (!m_captureDue) {
            m_cadenceAccumulator += m_frameRate;

            if (m_cadenceAccumulator >= m_sourceFrameRate) {
                m_cadenceAccumulator -= m_sourceFrameRate;
                m_captureDue = true;
            }
        }

        m_captureWasActive = true;

        return m_captureDue;
    }
}
