namespace Puck.Abstractions.Presentation;

/// <summary>
/// The single-request arm, serve, forward and refuse state an <see cref="ICaptureRequestTarget"/> keeps for the
/// capture it owes its next produced frame.
/// </summary>
/// <remarks>Arming and serving are the render host's pump, so this carries no synchronization of its own. A node
/// holding a slot still owns the two facts the slot cannot see: whether it has been disposed, and whether a
/// capture-capable node deeper in its chain is already armed.</remarks>
public sealed class CaptureRequestSlot {
    private FrameCaptureRequest? m_request;

    /// <summary>Gets the path armed on this slot, or <see langword="null"/>. A busy diagnostic, not a success
    /// signal: the completed write is observed through the request's own
    /// <see cref="FrameCaptureRequest.Completion"/>.</summary>
    public string? PendingPath => m_request?.Path;

    /// <summary>Arms a request on this slot.</summary>
    /// <param name="request">The unserved request.</param>
    /// <param name="pendingPath">The owning node's whole-chain <see cref="ICaptureRequestTarget.PendingCapturePath"/>,
    /// which must be <see langword="null"/> for the arm to be accepted.</param>
    /// <exception cref="InvalidOperationException">A capture is already pending, or the request is terminal.</exception>
    public void Arm(FrameCaptureRequest request, string? pendingPath) {
        ArgumentNullException.ThrowIfNull(argument: request);

        if ((pendingPath is not null) || request.Completion.IsCompleted) {
            throw new InvalidOperationException(message: "A capture is already pending or the request is terminal.");
        }

        m_request = request;
    }
    /// <summary>Fails an unserved request and clears the slot — the disposal path, where no frame will ever serve it.</summary>
    /// <param name="error">The reason no capture will be written.</param>
    public void Refuse(Exception error) {
        ArgumentNullException.ThrowIfNull(argument: error);

        _ = m_request?.TryFail(error: error);
        m_request = null;
    }
    /// <summary>Serves the armed request, if any, and clears the slot before the write so a failing write cannot
    /// leave the slot busy. A failed write is reported to standard error; the request itself carries the outcome.</summary>
    /// <param name="failureLabel">The stderr prefix a failed write is reported under.</param>
    /// <param name="writer">The readback and PNG writer, which must close the file before returning.</param>
    /// <exception cref="Gpu.DeviceLostException">The readback lost the graphics device; the request already carries
    /// the same failure.</exception>
    public void Serve(string failureLabel, Action<string> writer) {
        ArgumentException.ThrowIfNullOrEmpty(argument: failureLabel);
        ArgumentNullException.ThrowIfNull(argument: writer);

        if (m_request is not { } request) {
            return;
        }

        m_request = null;

        if (request.Write(writer: writer).Error is { } error) {
            Console.Error.WriteLine(value: $"{failureLabel} -> {request.Path} ({error.Message})");
        }
    }
    /// <summary>Hands an armed request to a capture-capable inner node, so the readback lands on whatever actually
    /// produced the shown frame. A request stays armed here when the inner cannot serve it, which is what stops it
    /// from vanishing silently.</summary>
    /// <param name="target">The inner node, or <see langword="null"/> when it captures nothing.</param>
    public void Forward(ICaptureRequestTarget? target) {
        if ((m_request is not { } request) || (target is null)) {
            return;
        }

        target.RequestCapture(request: request);

        m_request = null;
    }
}
