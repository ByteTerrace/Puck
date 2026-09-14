using System.Runtime.CompilerServices;

using Puck.Assets;

namespace Puck.Hosting;

/// <summary>
/// The capture-to-PNG encoder call every render node's capture path goes through, isolated from a failure to load
/// Puck.Assets: Puck.Assets is not part of the render contract, so an environment that blocks or cannot load its
/// assembly (an Application Control / code-integrity policy, a missing deployment file) must not take the render
/// loop down with it. One writer instance latches per node, so a doomed load is attempted once rather than per frame.
/// </summary>
public sealed class CapturePngWriter {
    private bool m_unavailable;

    /// <summary>Gets a value indicating whether a previous write proved the encoder unavailable, so no later write
    /// will be attempted.</summary>
    public bool Unavailable => m_unavailable;

    // The ONLY member touching the Puck.Assets-typed PngEncoder.Write call, kept non-inlined so the CLR resolves and
    // loads Puck.Assets.dll when this exact method is JITted — on the first actual capture, not on every produced
    // frame. TryWrite's try/catch wraps the call one frame up, which is where a failure to load surfaces.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteCore(string path, ReadOnlyMemory<byte> rgba, int width, int height) {
        PngEncoder.Write(
            height: height,
            path: path,
            rgba: rgba.Span,
            width: width
        );
    }

    /// <summary>Narrates and refuses a request this writer already knows it cannot serve, so a requester told a path
    /// is not left waiting on a file that is never coming.</summary>
    /// <param name="path">The requested output path.</param>
    /// <exception cref="NotSupportedException">A previous write proved Puck.Assets unavailable.</exception>
    public void ThrowIfUnavailable(string path) {
        if (!m_unavailable) {
            return;
        }

        Console.Error.WriteLine(value: $"[capture] skipped, Puck.Assets is unavailable — no file written to {path}");

        throw new NotSupportedException(message: "PNG capture is unavailable.");
    }
    /// <summary>Attempts one PNG write, surviving (and loudly reporting) an environment that refuses to load Puck.Assets.</summary>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="path">The output path.</param>
    /// <param name="rgba">The pixels, four bytes per pixel.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <returns><see langword="true"/> on success; <see langword="false"/> once the encoder is unavailable, which
    /// latches so a doomed load is never retried.</returns>
    public bool TryWrite(int height, string path, ReadOnlyMemory<byte> rgba, int width) {
        if (m_unavailable) {
            return false;
        }

        try {
            WriteCore(
                height: height,
                path: path,
                rgba: rgba,
                width: width
            );

            return true;
        } catch (Exception exception) when ((exception is FileLoadException or FileNotFoundException or TypeLoadException or BadImageFormatException or TypeInitializationException)) {
            Console.Error.WriteLine(value: $"[capture] WARNING: Puck.Assets is unavailable ({exception.GetType().Name}: {exception.Message}) — frame capture skipped, render continues without it.");

            m_unavailable = true;

            return false;
        }
    }
}
