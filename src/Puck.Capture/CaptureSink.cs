using Puck.Abstractions;

namespace Puck.Capture;

/// <summary>
/// The default <see cref="ICaptureSink"/>: encodes each captured CPU-pixel frame to a PNG (a single
/// <see cref="CaptureOptions.OutputPath"/> when set, otherwise numbered files under
/// <see cref="CaptureOptions.OutputDirectory"/>) after letting any observers inspect it.
/// </summary>
public sealed class CaptureSink : ICaptureSink {
    private static byte[] ToRgba(Surface surface) {
        var pixels = surface.Pixels.Span;
        var rgba = new byte[checked((((int)surface.Width * (int)surface.Height) * 4))];

        if (pixels.Length != rgba.Length) {
            throw new ArgumentException(
                message: $"Surface pixel length {pixels.Length} does not match {surface.Width}x{surface.Height} at 4 bytes per pixel.",
                paramName: nameof(surface)
            );
        }

        switch (surface.Format) {
            case SurfaceFormat.R8G8B8A8Unorm:
                pixels.CopyTo(destination: rgba);
                break;
            case SurfaceFormat.B8G8R8A8Unorm:
                for (var index = 0; (index < rgba.Length); index += 4) {
                    rgba[index + 0] = pixels[index + 2];
                    rgba[index + 1] = pixels[index + 1];
                    rgba[index + 2] = pixels[index + 0];
                    rgba[index + 3] = pixels[index + 3];
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(
                    actualValue: surface.Format,
                    message: "Only R8G8B8A8 and B8G8R8A8 surfaces can be encoded.",
                    paramName: nameof(surface)
                );
        }

        return rgba;
    }

    private bool m_disposed;
    private readonly ICaptureFrameObserver[] m_observers;
    private readonly CaptureOptions m_options;

    /// <summary>Initializes a new instance of the <see cref="CaptureSink"/> class.</summary>
    /// <param name="options">The output configuration.</param>
    /// <param name="observers">Observers notified of each frame before it is encoded, in registration order.</param>
    public CaptureSink(CaptureOptions options, IEnumerable<ICaptureFrameObserver>? observers = null) {
        ArgumentNullException.ThrowIfNull(options);

        m_observers = ((observers is null)
            ? []
            : [.. observers]);
        m_options = options;
    }

    /// <inheritdoc/>
    public void Consume(in CaptureFrame frame) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var surface = frame.Surface;

        if (!surface.IsCpuPixels) {
            return;
        }

        foreach (var observer in m_observers) {
            observer.OnFrameCaptured(frame: in frame);
        }

        var path = ResolvePath(frameIndex: frame.FrameIndex);

        _ = Directory.CreateDirectory(path: (Path.GetDirectoryName(path: path) ?? "."));
        PngEncoder.Write(
            height: (int)surface.Height,
            path: path,
            rgba: ToRgba(surface: surface),
            width: (int)surface.Width
        );
    }
    /// <inheritdoc/>
    public void Dispose() {
        m_disposed = true;
    }

    private string ResolvePath(long frameIndex) {
        return (string.IsNullOrWhiteSpace(value: m_options.OutputPath)
            ? Path.Combine(
                path1: m_options.OutputDirectory,
                path2: $"{m_options.FileNamePrefix}-{frameIndex:000000}.png"
            )
            : m_options.OutputPath);
    }
}
