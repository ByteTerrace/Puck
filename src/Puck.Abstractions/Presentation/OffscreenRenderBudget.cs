namespace Puck.Abstractions.Presentation;

/// <summary>The presentation budget for offscreen renders: the most full extra offscreen engine submits one produced
/// frame is asked to pay, beyond the frame's own composite. Two consumers each bound themselves by this one figure:
/// a view stack's round-robin refresh share of budgeted offscreen views (at most this many re-render on any one
/// produced frame; the rest keep their last image until the cursor returns), and a document validator's ceiling on
/// unbudgeted window sessions, which resolve every produced frame unconditionally (at most this many may be authored
/// at once). Neither may exceed it, so the worst case either path can ask a frame to pay is the same number.</summary>
public static class OffscreenRenderBudget {
    /// <summary>The most full extra offscreen engine submits one produced frame pays.</summary>
    public const int PerProducedFrame = 4;
    /// <summary>The most offscreen views one presentation holds registered at once. Registration is cheap state (a
    /// view past <see cref="PerProducedFrame"/> keeps its last image between refreshes), so this bounds bookkeeping, not
    /// per-frame cost; a document validator caps the rows that each carry a persistent offscreen render (cameras)
    /// by the same figure, so a document can never declare more than the runtime can register.</summary>
    public const int RegisteredViews = 64;
}
