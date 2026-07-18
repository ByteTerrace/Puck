using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>Supplies scene, view, transform, and optional auxiliary content for each SDF render frame.</summary>
public interface ISdfFrameSource {
    /// <summary>Drops any device-owned resources held outside the main engine. The next produced frame rebuilds them
    /// against the replacement device. Default no-op for CPU-only frame sources.</summary>
    void NotifyDeviceLost() { }

    /// <summary>Captures the scene, cameras, and per-entity transforms for one rendered frame.</summary>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="deltaSeconds">The presentation frame delta in seconds (for time-based presentation smoothing).</param>
    /// <param name="interpolationAlpha">The fraction in <c>[0, 1)</c> between the previous and current fixed simulation tick, for interpolating presentation state toward the variable display rate; a static source may ignore it.</param>
    /// <returns>The frame to render.</returns>
    SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha);

    /// <summary>Advances a carve-bake planner by one produced frame against the live engine.
    /// the host node calls this right BEFORE <see cref="CaptureFrame"/> once the engine exists, handing the frame source
    /// the engine's brick-bake seam so its planner can poll bake states, request newly-settled bakes, and flip
    /// <see cref="BrickBakeState.Ready"/> bins to bricks — a Ready flip bumps the source's content revision so THIS
    /// frame's <see cref="CaptureFrame"/> rebuilds emitting the brick. The default implementation is a no-op, and a
    /// pool-less engine's <see cref="ISdfBrickBakeService.BrickBakeAvailable"/>
    /// short-circuits the planner regardless. Read straight off the frame source (mirroring <see cref="GlyphAtlas"/> /
    /// <see cref="ScreenDecals"/>) so a host node's type coupling doesn't grow to thread it.</summary>
    /// <param name="bakes">The engine's brick-bake service (poll/request), never null.</param>
    void AdvanceBricks(ISdfBrickBakeService bakes) { }

    /// <summary>Hands the frame source the live GPU device + compute services once per produced frame, right AFTER
    /// <see cref="CaptureFrame"/> and BEFORE the host polls this frame's screen-source providers — the seam a source
    /// that feeds a screen from CPU pixels uses to upload this frame's image to a stable handle its provider then
    /// returns (the provider is polled just after this call). Default no-op: a source with no CPU-fed screen (the vast
    /// majority) need not override it. Mirrors <see cref="AdvanceBricks"/>: an engine seam handed an engine
    /// capability, not a host-shaped hook.</summary>
    /// <param name="deviceContext">The live GPU device context to upload on.</param>
    /// <param name="gpu">The neutral GPU compute services (resolves the upload factory).</param>
    void PrepareScreenSources(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) { }

    /// <summary>Hands the frame source this frame's full <see cref="Puck.Hosting.FrameContext"/> once per produced frame,
    /// right AFTER <see cref="PrepareScreenSources"/> and BEFORE the host polls this frame's screen-source providers — the
    /// seam a source that hosts its own offscreen view pool (a <see cref="Views.ViewStack"/> of diegetic camera / nested-
    /// world renders) uses to render those views against the live device this frame, so a screen-source provider that
    /// returns a view's handle reads a freshly-rendered image. Distinct from <see cref="PrepareScreenSources"/> (which
    /// hands over the device + compute services alone) because an offscreen view render resolves its own device from the
    /// frame context's host and renders the SAME world program the host is composing. Default no-op: a source with no
    /// view pool (the vast majority) need not override it. Mirrors <see cref="PrepareScreenSources"/>/
    /// <see cref="AdvanceBricks"/>: an engine seam handed an engine-frame capability, not a host-shaped hook.</summary>
    /// <param name="context">This frame's host frame context (its <see cref="Puck.Hosting.FrameContext.Host"/> resolves
    /// the live GPU device the offscreen views render on).</param>
    void RenderViews(in Puck.Hosting.FrameContext context) { }

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

    /// <summary>Per-frame GLYPH DECAL providers keyed by the program-declared screen index — a screen slot showing dense
    /// reading text (the material-level text tier, <see cref="SdfWorldEngine.SetScreenDecal"/>) instead of a bound
    /// image. Each provider returns this frame's cell grid, or <see langword="null"/> to leave the slot on the
    /// image/procedural path (the atlas-unavailable degrade). Default null (no decal screens) — a source with none need
    /// not override it. Mirrors <see cref="ScreenSurfaceTransforms"/>/<see cref="GlyphAtlas"/>: read straight off the
    /// frame source so a host node's type coupling doesn't grow to thread it.</summary>
    IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>>? ScreenDecals => null;
}
