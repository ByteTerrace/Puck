using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Presentation;

/// <summary>The terminal outcome of one render capture. Success means the writer returned after closing the PNG;
/// it does not promise durable storage or that another process cannot subsequently change the file.</summary>
/// <param name="Path">The requested output path.</param>
/// <param name="Error">The capture failure, or null on success.</param>
public sealed record FrameCaptureResult(string Path, Exception? Error) {
    /// <summary>Gets whether the PNG write completed successfully.</summary>
    public bool Succeeded => Error is null;
}

/// <summary>One process-local capture request, forwarded unchanged through a render chain. Arming and serving
/// happen on the render host's pump. Workers may await <see cref="Completion"/>; cancelling that wait does not
/// cancel an accepted capture or make its output path available for reuse.</summary>
public sealed class FrameCaptureRequest {
    private readonly TaskCompletionSource<FrameCaptureResult> m_completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int m_claimed;

    /// <summary>Creates an unserved request. The caller owns directory creation and path policy.</summary>
    /// <param name="path">The PNG path.</param>
    public FrameCaptureRequest(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    /// <summary>Gets the requested PNG path.</summary>
    public string Path { get; }
    /// <summary>Gets this request's terminal result. Failures are result data, so fire-and-forget console requests
    /// do not leave unobserved task exceptions. Continuations never run inline on the render pump.</summary>
    public Task<FrameCaptureResult> Completion => m_completion.Task;

    /// <summary>Serves this request once, completing only after the synchronous readback and PNG writer returns.
    /// A writer exception becomes a failed result. Device loss also propagates to the host's recovery loop;
    /// ordinary capture failures do not interrupt rendering.</summary>
    /// <param name="writer">The readback and PNG writer. It must close the file before returning.</param>
    /// <returns>The terminal result, including any writer exception.</returns>
    /// <exception cref="InvalidOperationException">Another owner already claimed or refused this request.</exception>
    /// <exception cref="DeviceLostException">Readback lost the graphics device. Completion contains the same
    /// failure before this signal is rethrown for host recovery.</exception>
    public FrameCaptureResult Write(Action<string> writer) {
        ArgumentNullException.ThrowIfNull(writer);
        if (Interlocked.CompareExchange(ref m_claimed, 1, 0) != 0) {
            throw new InvalidOperationException("The capture request has already been served or refused.");
        }

        Exception? error = null;
        try {
            writer(Path);
        } catch (DeviceLostException exception) {
            m_completion.SetResult(new FrameCaptureResult(Path, exception));
            throw;
        } catch (Exception exception) {
            error = exception;
        }

        var result = new FrameCaptureResult(Path, error);
        m_completion.SetResult(result);
        return result;
    }

    /// <summary>Refuses an unserved request, for example during renderer disposal. A write that has started
    /// retains ownership of its outcome.</summary>
    /// <param name="error">The reason no capture will be written.</param>
    /// <returns>Whether this call completed the request.</returns>
    public bool TryFail(Exception error) {
        ArgumentNullException.ThrowIfNull(error);
        if (Interlocked.CompareExchange(ref m_claimed, 1, 0) != 0) {
            return false;
        }

        m_completion.SetResult(new FrameCaptureResult(Path, error));
        return true;
    }
}
