namespace Puck.Abstractions.Presentation;

/// <summary>A render-chain node capable of capturing its composed output. A pass-through decorator forwards the
/// same request to its inner target; a decorator without a capture-capable inner retains it until it draws or
/// is disposed. Requests and frame production are serialized by the host pump.</summary>
public interface ICaptureRequestTarget {
    /// <summary>Gets the armed path here or deeper in the chain, or null. This is a busy diagnostic, not a success
    /// signal: use the request's <see cref="FrameCaptureRequest.Completion"/> to observe the completed write.</summary>
    string? PendingCapturePath { get; }

    /// <summary>Arms a capture of the next frame this node serves, including its decoration. Busy or disposed
    /// targets refuse by throwing; they never replace an accepted request. Disposal fails any unserved request.</summary>
    /// <param name="request">The unserved request. The caller creates its parent directory.</param>
    /// <exception cref="InvalidOperationException">A capture is already pending, or the request is terminal.</exception>
    /// <exception cref="ObjectDisposedException">The node has been disposed.</exception>
    void RequestCapture(FrameCaptureRequest request);
}
