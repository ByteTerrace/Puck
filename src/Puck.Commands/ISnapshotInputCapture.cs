namespace Puck.Commands;

/// <summary>A host-loop contribution that captures one presentation frame's non-window input into an
/// <see cref="InputRouter"/>. The launcher invokes every contribution once before it dispatches due fixed ticks.</summary>
public interface ISnapshotInputCapture {
    /// <summary>Captures the source's pending input for a unique host frame.</summary>
    /// <param name="frameKey">A monotonically increasing host-frame key.</param>
    void CaptureFrame(ulong frameKey);
}
