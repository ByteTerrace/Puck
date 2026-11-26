using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Supplies scene, view, transform, and optional auxiliary content for each SDF render frame.</summary>
public interface ISdfFrameSource {
    /// <summary>Holds presentation at the capture's first composition until its request completes.</summary>
    /// <param name="request">The converging capture, whose completion releases the hold.</param>
    void BeginConvergence(Puck.Abstractions.Presentation.FrameCaptureRequest request) { }
    /// <summary>Drops any device-owned resources held outside the main engine. The next produced frame rebuilds them
    /// against the replacement device. Default no-op for CPU-only frame sources.</summary>
    void NotifyDeviceLost() { }
    /// <summary>Releases the claims the source's own frames hold on their programs (<see cref="SdfFrame.Writer.Release"/>),
    /// called by the residency that captures from it at its final release. Only a source that residency alone reads lets
    /// them go; the default keeps them, for a source shared beyond it.</summary>
    void ReleaseFrames() { }
    /// <summary>Captures the scene, cameras, and per-entity transforms for one rendered frame. It never waits for work
    /// another thread does for the frame (a program or a field still building): it films nothing, or a frame that does not
    /// yet show what its content calls for, and names that work in <see cref="Waiting"/>.</summary>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="deltaSeconds">The presentation frame delta in seconds (for time-based presentation smoothing).</param>
    /// <param name="interpolationAlpha">The fraction in <c>[0, 1)</c> between the previous and current fixed simulation tick, for interpolating presentation state toward the variable display rate; a static source may ignore it.</param>
    /// <returns>The frame to render, or <see langword="null"/> when the source films nothing this frame, so the frame
    /// it filmed last stands. The frame is the source's and borrowed until its next capture, which may write it again
    /// (<see cref="SdfFrame.Writer"/>); a caller that keeps it longer keeps a copy of its own.</returns>
    SdfFrame? CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha);
    /// <summary>Gets why the latest <see cref="CaptureFrame"/> filmed nothing, or a frame that does not yet show what its
    /// content calls for, naming the work it waits on; <see langword="null"/>, the default, when the frame shows it. A
    /// residency answers waiting with it and never holds such a frame for a still.</summary>
    string? Waiting => null;
    /// <summary>Presents the frame the source captured last once more, in a produced frame whose residency renders a
    /// held frame (a converging capture's) in place of calling
    /// <see cref="CaptureFrame"/>: whatever the source does for each produced frame beyond composing it, such as placing
    /// its views in the render graph, still happens. The default implementation does nothing.</summary>
    void PresentHeldFrame() { }
    /// <summary>Hands the source what to call with each program a background build of its finishes and the dynamic
    /// transforms it was emitted under, on the build's thread, before any frame carries it: a residency plans its mesh
    /// bake there (<see cref="SdfMeshPathBake.PlanAhead"/>). A later call replaces the earlier. The default
    /// implementation, for a source that builds nothing in the background, ignores it.</summary>
    /// <param name="built">The callback; the transforms it is handed are the source's to write again once it returns.</param>
    void ObserveBuilds(Action<SdfProgram, IReadOnlyList<DynamicTransform>> built) { }

    /// <summary>Screen-surface TRANSFORM providers keyed by the program-declared screen index (see
    /// <see cref="SdfWorldResidency"/>'s <c>screenSurfaceTransforms</c> constructor parameter): a screen riding a dynamic
    /// entity re-poses its world-space sampling frame every frame it moved. Default null (no dynamic screen
    /// surfaces) — a frame source that never declares one need not override this. Reading this straight off the
    /// frame source (rather than threading it through a separate render-spec field) keeps a host node's own type
    /// coupling from growing just to wire this seam through.</summary>
    IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? ScreenSurfaceTransforms => null;
    /// <summary>The single font atlas the <see cref="SdfShapeType.Glyph"/> primitive samples, or <see langword="null"/>
    /// when the source declares no world text. The host uploads it when the engine is created and whenever this
    /// immutable atlas reference changes, so a document reload can replace or remove its catalog without rebuilding
    /// the render node. Default null — a source with no glyphs need not override it, and the glyph binding then samples
    /// a neutral filler. Mirrors <see cref="ScreenSurfaceTransforms"/>: read straight off the frame source so a host
    /// node's type coupling doesn't grow to thread it.</summary>
    SdfGlyphAtlas? GlyphAtlas => null;
    /// <summary>Per-frame GLYPH DECAL providers keyed by the program-declared screen index — a screen slot showing dense
    /// reading text (the material-level text tier, <see cref="SdfWorldTables.SetScreenDecal"/>) instead of a bound
    /// image. Each provider returns this frame's cell grid, or <see langword="null"/> to leave the slot on the
    /// image or unbound-glass path (the atlas-unavailable degrade). Default null (no decal screens) — a source with none need
    /// not override it. Mirrors <see cref="ScreenSurfaceTransforms"/>/<see cref="GlyphAtlas"/>: read straight off the
    /// frame source so a host node's type coupling doesn't grow to thread it.</summary>
    IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>>? ScreenDecals => null;
    /// <summary>Gets what supplies each surface text slot's glyphs each frame: handed a slot
    /// (0..<see cref="Puck.SignedDistance.SdfSurfaceTextLayout.MaxTexts"/>-1) a program's text paints may name
    /// (<see cref="Puck.SignedDistance.SdfPaint.Text"/>), it returns the glyph quads the slot draws this frame
    /// (<see cref="SdfWorldTables.SetSurfaceText"/>), or <see langword="null"/> for a slot that draws nothing.
    /// <see langword="null"/>, the default, for a source whose programs carry no text paint.</summary>
    Func<int, SdfSurfaceTextFrame?>? SurfaceTexts => null;
}
