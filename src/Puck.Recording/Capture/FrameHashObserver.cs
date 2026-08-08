using Puck.Abstractions.Capture;
using Puck.Maths;
namespace Puck.Recording.Capture;

/// <summary>
/// A capture observer that writes a deterministic per-frame content hash — the core regression signal for
/// verifying that deterministic frames render bit-identically across runs or backends. Hashes the CPU pixels
/// with 64-bit FNV-1a.
/// </summary>
public sealed class FrameHashObserver : ICaptureFrameObserver {
    private readonly TextWriter m_output;

    /// <summary>Initializes a new instance of the <see cref="FrameHashObserver"/> class.</summary>
    /// <param name="output">Where hash lines are written; defaults to <see cref="Console.Out"/>.</param>
    public FrameHashObserver(TextWriter? output = null) {
        m_output = (output ?? Console.Out);
    }

    /// <inheritdoc/>
    public void OnFrameCaptured(in CaptureFrame frame) {
        if (!frame.Surface.IsCpuPixels) {
            return;
        }

        m_output.WriteLine(value: $"capture | frame {frame.FrameIndex} ticks={frame.TimestampTicks} hash={Fnv1aHash.Compute(values: frame.Surface.Pixels.Span):x16}");
    }
}
