using Puck.Abstractions;

namespace Puck.Capture;

/// <summary>
/// A capture observer that writes a deterministic per-frame content hash — the core regression signal for
/// verifying that deterministic frames render bit-identically across runs or backends. Hashes the CPU pixels
/// with 64-bit FNV-1a.
/// </summary>
public sealed class FrameHashObserver : ICaptureFrameObserver {
    private const ulong Fnv64OffsetBasis = 0xCBF29CE484222325UL;
    private const ulong Fnv64Prime = 0x100000001B3UL;

    private static ulong Hash(ReadOnlySpan<byte> data) {
        var hash = Fnv64OffsetBasis;

        foreach (var value in data) {
            hash ^= value;
            hash *= Fnv64Prime;
        }

        return hash;
    }

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

        m_output.WriteLine(value: $"capture | frame {frame.FrameIndex} ticks={frame.TimestampTicks} hash={Hash(data: frame.Surface.Pixels.Span):x16}");
    }
}
