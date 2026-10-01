using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Presentation;

/// <summary>The terminal outcome of one render capture. Success means the writer returned after closing the PNG;
/// it does not promise durable storage or that another process cannot subsequently change the file.</summary>
/// <param name="Path">The requested output path.</param>
/// <param name="Error">The capture failure, or null on success.</param>
/// <param name="Tick">The tick of the state the served image shows, the tick its server says the image was rendered at;
/// <see langword="null"/> when the server names none or no frame served the request.</param>
/// <param name="Frame">The serving node's ordinal of the frame that rendered the served image, counted as its completed
/// frames were when that frame's render finished; <see langword="null"/> when the server counts no frames or no frame
/// served the request.</param>
public sealed record FrameCaptureResult(string Path, Exception? Error, ulong? Tick = null, ulong? Frame = null) {
    /// <summary>Gets whether the PNG write completed successfully.</summary>
    public bool Succeeded => (Error is null);
}
/// <summary>One process-local capture request, forwarded unchanged through a render chain. Arming and serving
/// happen on the render host's pump. Workers may await <see cref="Completion"/>; cancelling that wait does not
/// cancel an accepted capture or make its output path available for reuse.</summary>
public sealed class FrameCaptureRequest {
    private readonly TaskCompletionSource<FrameCaptureResult> m_completion = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

    private int m_claimed;

    /// <summary>Creates an unserved request. The caller owns directory creation and path policy.</summary>
    /// <param name="path">The PNG path.</param>
    /// <param name="converge">The number of frozen rendered samples to capture; zero captures the next frame normally.</param>
    public FrameCaptureRequest(string path, int converge = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: converge);
        Converge = converge;
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    /// <summary>Gets this request's terminal result. Failures are result data, so fire-and-forget console requests
    /// do not leave unobserved task exceptions. Continuations never run inline on the render pump.</summary>
    public Task<FrameCaptureResult> Completion => m_completion.Task;
    /// <summary>Gets the requested PNG path.</summary>
    public string Path { get; }
    /// <summary>Gets the number of frozen samples requested, or zero for an ordinary capture.</summary>
    public int Converge { get; }

    /// <summary>Refuses an unserved request: a renderer being disposed, or the requester withdrawing a capture it
    /// will no longer wait for, which a <see cref="CaptureRequestSlot"/> still holding it then drops. A write that
    /// has started retains ownership of its outcome.</summary>
    /// <param name="error">The reason no capture will be written.</param>
    /// <returns>Whether this call completed the request.</returns>
    public bool TryFail(Exception error) {
        ArgumentNullException.ThrowIfNull(error);
        if (Interlocked.CompareExchange(
            comparand: 0,
            location1: ref m_claimed,
            value: 1
        ) != 0) {
            return false;
        }

        m_completion.SetResult(result: new FrameCaptureResult(
            Path,
            error
        ));
        return true;
    }
    /// <summary>Serves this request once, completing only after the synchronous readback and PNG writer returns.
    /// A writer exception becomes a failed result. Device loss also propagates to the host's recovery loop;
    /// ordinary capture failures do not interrupt rendering.</summary>
    /// <param name="writer">The readback and PNG writer. It must close the file before returning.</param>
    /// <param name="tick">The tick of the state the image the writer reads was rendered from, which the result records
    /// (<see cref="FrameCaptureResult.Tick"/>): the tick of the frame that rendered it, however many frames before it is
    /// served; <see langword="null"/> when the server names none.</param>
    /// <param name="frame">The serving node's ordinal of the frame that rendered that image
    /// (<see cref="FrameCaptureResult.Frame"/>); <see langword="null"/> when the server counts none.</param>
    /// <returns>The terminal result, including any writer exception.</returns>
    /// <exception cref="InvalidOperationException">Another owner already claimed or refused this request.</exception>
    /// <exception cref="DeviceLostException">Readback lost the graphics device. Completion contains the same
    /// failure before this signal is rethrown for host recovery.</exception>
    public FrameCaptureResult Write(Action<string> writer, ulong? tick = null, ulong? frame = null) {
        ArgumentNullException.ThrowIfNull(writer);
        if (Interlocked.CompareExchange(
            comparand: 0,
            location1: ref m_claimed,
            value: 1
        ) != 0) {
            throw new InvalidOperationException(message: "The capture request has already been served or refused.");
        }

        Exception? error = null;

        try {
            writer(Path);
        } catch (DeviceLostException exception) {
            m_completion.SetResult(result: new FrameCaptureResult(
                Path,
                exception,
                tick,
                frame
            ));
            throw;
        } catch (Exception exception) {
            error = exception;
        }

        var result = new FrameCaptureResult(
            Path,
            error,
            tick,
            frame
        );

        m_completion.SetResult(result: result);
        return result;
    }
}
