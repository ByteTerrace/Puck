namespace Puck.SdfVm;

public interface ISdfFrameSource {
    /// <summary>Captures the scene, cameras, and per-entity transforms for one rendered frame.</summary>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="deltaSeconds">The seconds the simulation advanced since the previous frame (for time-based presentation smoothing).</param>
    /// <param name="interpolationAlpha">The fraction in <c>[0, 1)</c> between the previous and current fixed simulation tick, for interpolating presentation state toward the variable display rate; a static source may ignore it.</param>
    /// <returns>The frame to render.</returns>
    SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha);

    /// <summary>Screen-surface TRANSFORM providers keyed by the program-declared screen index (see
    /// <see cref="SdfEngineNode"/>'s <c>screenSurfaceTransforms</c> constructor parameter): a screen riding a dynamic
    /// entity re-poses its world-space sampling frame every frame it moved. Default null (no dynamic screen
    /// surfaces) — a frame source that never declares one need not override this. Reading this straight off the
    /// frame source (rather than threading it through a separate render-spec field) keeps a host node's own type
    /// coupling from growing just to wire this seam through.</summary>
    IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? ScreenSurfaceTransforms => null;

    /// <summary>The single font atlas the <see cref="SdfShapeType.Glyph"/> primitive samples, or <see langword="null"/>
    /// when the source declares no world text. STATIC: the host uploads it ONCE (<see cref="SdfWorldEngine.SetGlyphAtlas"/>)
    /// when the engine is (re)created, not per frame — so this is polled once at engine build, not on the per-frame
    /// path. Default null — a source with no glyphs need not override it, and the glyph binding then samples a neutral
    /// filler. Mirrors <see cref="ScreenSurfaceTransforms"/>: read straight off the frame source so a host node's type
    /// coupling doesn't grow to thread it.</summary>
    SdfGlyphAtlas? GlyphAtlas => null;
}
