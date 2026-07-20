namespace Puck.World;

/// <summary>
/// The placement contract invariants that size fixed engine buffers allocated before any <see cref="WorldDefinition"/>
/// exists. World-varying policy values live in <see cref="WorldDefinition.Authoring"/> /
/// <see cref="WorldAuthoringDefaults"/> instead. What is left here stays a compile-time constant because each one
/// sizes a FIXED engine buffer — <see cref="Client.WorldFrameSource"/>'s dynamic-transform array is a FIELD
/// INITIALIZER (<c>WorldAvatarCatalog.DynamicTransformCapacity + WorldStampPool.DynamicSlotCount</c>), which
/// runs before the constructor body ever sees a definition, and <see cref="Client.WorldStampPool"/>'s replay
/// pool and per-shape stackalloc spans are sized from the same static chain. Making one of these per-world data would
/// require redesigning that allocation to run after the boot definition loads. Values are read at probe/validate/replay
/// time only — never per-pixel.
/// </summary>
internal static class WorldPlacementPolicy {
    /// <summary>The per-stamp shape budget: the largest <see cref="Puck.Authoring.CreationDocument.StampShapeCount"/>
    /// (authored shapes + expanded text-run glyphs) a creation row may carry. CONTRACT INVARIANT: feeds
    /// <see cref="MaxAnimatedStampShapes"/>, which sizes <see cref="Client.WorldStampPool"/>'s per-slot
    /// stackalloc spans and (via <see cref="Client.WorldStampPool.SlotsPerPlacement"/>) the
    /// field-initializer-time dynamic-transform array. The validator's rejection line names this ceiling word-exactly.</summary>
    public const int MaxShapesPerStamp = 48;

    /// <summary>The hard cap on simultaneous STAMP-POOL registrations — an ANIMATED placement (a creation carrying
    /// timeline frames) OR a body-rooted creation stamp (an inhabited placement's body, or a crowd body wearing a
    /// creation look). CONTRACT INVARIANT: sizes <see cref="Client.WorldStampPool"/>'s pool array
    /// (<c>new Registration?[MaxStampRegistrations]</c>) and the field-initializer-time dynamic-transform capacity — the
    /// validator's rejection line names this ceiling word-exactly. Set to 8: animated placements and body-rooted creation
    /// stamps (inhabitants + crowd creation-looks) share the pool; the validator gates animated placements against it and
    /// the pool degrades a starved body-rooted stamp to a catalog avatar with a loud warn.</summary>
    public const int MaxStampRegistrations = 8;

    /// <summary>The per-animated-placement shape-slot pool — equal to <see cref="MaxShapesPerStamp"/>, so an animated
    /// creation obeys the same stamp budget as a static one. CONTRACT INVARIANT for the same reason as
    /// <see cref="MaxShapesPerStamp"/>.</summary>
    public const int MaxAnimatedStampShapes = MaxShapesPerStamp;

    /// <summary>The timeline replay hold per frame, in seconds — an 8-tick-at-60-Hz cadence, hold-style with no
    /// interpolation. Presentation-only (rides the render clock, never simulation state). A contract invariant, not
    /// an authoring knob — a world wanting a different replay feel is a future authoring surface.</summary>
    public const float TimelineSecondsPerFrame = (8f / 60f);
}
