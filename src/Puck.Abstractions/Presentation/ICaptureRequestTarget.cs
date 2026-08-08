namespace Puck.Abstractions.Presentation;

/// <summary>
/// Represents a render-chain node that can capture the next frame it produces — the ENGINE SEAM every overlay
/// decorator and frame producer shares, so capture requests can target the outermost rendered result without
/// widening <c>IRenderNode</c>. Lives in the neutral presentation contract (not any producer library) so a general
/// decorator never references the engine it happens to wrap.
/// </summary>
/// <remarks>
/// A node that has nothing to draw THIS frame (e.g. the console is closed) passes its inner producer's frame
/// through untouched; when that happens with a capture pending, the node forwards the request to its own inner
/// node instead of reading back a target it never wrote to — so the request cascades down to whichever node
/// actually produced the frame that will be shown (down to the bare frame producer at the bottom of the chain).
/// </remarks>
public interface ICaptureRequestTarget {
    /// <summary>The path of an armed request this node has not served yet, or <see langword="null"/> when nothing is
    /// pending here. A request is WORK THAT HAS ONLY BEEN ASKED FOR: it becomes a file when a frame composes, and a
    /// run that ends first writes nothing. This makes that distinction observable, so a caller can report an
    /// unserved request instead of leaving the requester to believe a file exists. A node that has forwarded its
    /// request down the chain (see the remarks above) reports <see langword="null"/> — the node it forwarded to
    /// reports the path.</summary>
    string? PendingCapturePath { get; }

    /// <summary>Arms a one-shot readback of the next frame this node produces (or, if it draws nothing before the
    /// request is served, the next producing node beneath it), writing it to <paramref name="path"/> as a PNG. A
    /// request already pending here is REPLACED and its file is never written — a caller that must not lose one
    /// checks <see cref="PendingCapturePath"/> first.</summary>
    /// <param name="path">The PNG path to write; the caller creates the parent directory.</param>
    void RequestCapture(string path);
}
