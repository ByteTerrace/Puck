namespace Puck.World;

/// <summary>
/// THE one home for the placement CONTRACT INVARIANTS that survived the P5.5 "a constant must justify not being
/// data" sweep — every world-varying policy value P5 introduced (headroom, the repeat-segment cap, the scale envelope)
/// moved to the <see cref="WorldDefinition.Authoring"/> section (see <see cref="WorldAuthoringDefaults"/>); what is
/// left here stays a compile-time constant because each one sizes a FIXED engine buffer that is allocated before any
/// <see cref="WorldDefinition"/> exists — <see cref="Client.WorldFrameSource"/>'s dynamic-transform array is a FIELD
/// INITIALIZER (<c>WorldAvatarCatalog.DynamicTransformCapacity + WorldPlacementAnimator.DynamicSlotCount</c>), which
/// runs before the constructor body ever sees a definition, and <see cref="Client.WorldPlacementAnimator"/>'s replay
/// pool and per-shape stackalloc spans are sized from the same static chain. Making one of these per-world data would
/// require redesigning that allocation to run after the boot definition loads — a structural change this sweep does
/// not make. Values are read at probe/validate/replay time only — never per-pixel.
/// </summary>
internal static class WorldPlacementPolicy {
    /// <summary>The per-stamp shape budget: the largest <see cref="Puck.Authoring.CreationDocument.StampShapeCount"/>
    /// (authored shapes + expanded text-run glyphs) a creation row may carry. CONTRACT INVARIANT: feeds
    /// <see cref="MaxAnimatedStampShapes"/>, which sizes <see cref="Client.WorldPlacementAnimator"/>'s per-slot
    /// stackalloc spans and (via <see cref="Client.WorldPlacementAnimator.SlotsPerPlacement"/>) the
    /// field-initializer-time dynamic-transform array. The validator's rejection line names this ceiling word-exactly.</summary>
    public const int MaxShapesPerStamp = 48;

    /// <summary>The hard cap on simultaneously ANIMATED placements (creations carrying timeline frames). CONTRACT
    /// INVARIANT: sizes <see cref="Client.WorldPlacementAnimator"/>'s pool array
    /// (<c>new Registration?[MaxAnimatedPlacements]</c>) and the field-initializer-time dynamic-transform capacity —
    /// the validator's rejection line names this ceiling word-exactly.</summary>
    public const int MaxAnimatedPlacements = 4;

    /// <summary>The per-animated-placement shape-slot pool (mirrors <see cref="MaxShapesPerStamp"/> so an animated
    /// creation obeys the same stamp budget as a static one). CONTRACT INVARIANT for the same reason as
    /// <see cref="MaxShapesPerStamp"/>.</summary>
    public const int MaxAnimatedStampShapes = MaxShapesPerStamp;

    /// <summary>The timeline replay hold per frame, in seconds — the settled 8-tick-at-60-Hz cadence the Demo
    /// companion/workbench replay established (hold-style: each frame holds this long, no interpolation).
    /// Presentation-only (rides the render clock, never simulation state). CONTRACT INVARIANT: this is the ported
    /// Companion-pattern cadence itself (§"mechanisms port; content does not"), not an authoring knob — a world
    /// wanting a different replay feel is a future authoring surface, not this sweep's scope.</summary>
    public const float TimelineSecondsPerFrame = (8f / 60f);
}
