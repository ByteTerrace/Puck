namespace Puck.World;

/// <summary>
/// The placement contract invariants that size fixed engine buffers allocated before any <see cref="WorldDefinition"/>
/// exists. World-varying policy values live in <see cref="WorldDefinition.Authoring"/> /
/// <see cref="WorldAuthoringDefaults"/> instead. What is left here stays a compile-time constant because each one
/// sizes a FIXED engine buffer — <see cref="Client.WorldFrameSource"/>'s dynamic-transform array is a FIELD
/// INITIALIZER (<c>WorldAvatarCatalog.DynamicTransformCapacity + WorldPlacementAnimator.DynamicSlotCount</c>), which
/// runs before the constructor body ever sees a definition, and <see cref="Client.WorldPlacementAnimator"/>'s replay
/// pool and per-shape stackalloc spans are sized from the same static chain. Making one of these per-world data would
/// require redesigning that allocation to run after the boot definition loads. Values are read at probe/validate/replay
/// time only — never per-pixel.
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

    /// <summary>The per-animated-placement shape-slot pool — equal to <see cref="MaxShapesPerStamp"/>, so an animated
    /// creation obeys the same stamp budget as a static one. CONTRACT INVARIANT for the same reason as
    /// <see cref="MaxShapesPerStamp"/>.</summary>
    public const int MaxAnimatedStampShapes = MaxShapesPerStamp;

    /// <summary>The timeline replay hold per frame, in seconds — an 8-tick-at-60-Hz cadence, hold-style with no
    /// interpolation. Presentation-only (rides the render clock, never simulation state). A contract invariant, not
    /// an authoring knob — a world wanting a different replay feel is a future authoring surface.</summary>
    public const float TimelineSecondsPerFrame = (8f / 60f);
}
