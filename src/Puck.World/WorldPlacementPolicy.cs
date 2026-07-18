namespace Puck.World;

/// <summary>
/// THE one home for every world-varying placement policy value P5 introduced — deliberately centralized (owner
/// directive: scattered policy constants violate everything-as-data) and a DATA-FICATION CANDIDATE: the P5.5 sweep
/// lifts this tier into a <see cref="WorldDefinition"/> editor-defaults section; nothing here may be duplicated
/// per-file, and nothing here is a structural contract (those live beside the mechanisms that own them, documented as
/// contracts). Values are read at probe/validate/replay time only — never per-pixel.
/// </summary>
internal static class WorldPlacementPolicy {
    /// <summary>The per-stamp shape budget: the largest <see cref="Puck.Authoring.CreationDocument.StampShapeCount"/>
    /// (authored shapes + expanded text-run glyphs) a creation row may carry. The probe reserves exactly this many
    /// worst-case shapes per reserved placement, so the validator's rejection line names this ceiling word-exactly.</summary>
    public const int MaxShapesPerStamp = 48;

    /// <summary>The placement rows of AUTHORING HEADROOM the construction probe reserves beyond the boot placements,
    /// so a live editor can stamp creations into a booted world; a placement past boot + headroom rejects loudly at
    /// apply time through the probed render envelope (the P3 rejection shape).</summary>
    public const int AuthoringHeadroomPlacements = 8;

    /// <summary>The largest per-axis repeat count one emitted segment carries (the Demo auto-split precedent): a
    /// repeat row longer than this splits into several instances so no single instance bound defeats the tile cull.</summary>
    public const int MaxRepeatPerSegment = 8;

    /// <summary>The uniform placement scale envelope (the Demo authoring envelope, adopted).</summary>
    public const float MinScale = 0.2f;
    public const float MaxScale = 5.0f;

    /// <summary>The hard cap on simultaneously ANIMATED placements (creations carrying timeline frames) — the
    /// dynamic-transform pool reserves exactly this many replay slots at probe time, so the validator's rejection
    /// line names this ceiling word-exactly.</summary>
    public const int MaxAnimatedPlacements = 4;

    /// <summary>The per-animated-placement shape-slot pool (mirrors <see cref="MaxShapesPerStamp"/> so an animated
    /// creation obeys the same stamp budget as a static one).</summary>
    public const int MaxAnimatedStampShapes = MaxShapesPerStamp;

    /// <summary>The timeline replay hold per frame, in seconds — the settled 8-tick-at-60-Hz cadence the Demo
    /// companion/workbench replay established (hold-style: each frame holds this long, no interpolation).
    /// Presentation-only (rides the render clock, never simulation state).</summary>
    public const float TimelineSecondsPerFrame = (8f / 60f);
}
