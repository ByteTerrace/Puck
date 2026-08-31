namespace Puck.World;

/// <summary>
/// The placement contract invariants that size fixed engine buffers allocated before any <see cref="WorldDefinition"/>
/// exists. World-varying policy values live in <see cref="WorldDefinition.Authoring"/> /
/// <see cref="WorldPlacementPolicyDefaults"/> instead. What is left here stays a compile-time constant because each one
/// sizes a FIXED engine buffer — <c>Client.WorldSceneEmitter</c> declares its dynamic-transform slot count
/// (<c>WorldRigCatalog.DynamicTransformCapacity + WorldStampPool.DynamicSlotCount</c>) as the composition host
/// reads it, before any definition is in hand, and <c>Client.WorldStampPool</c>'s replay
/// pool and per-shape stackalloc spans are sized from the same static chain. Making one of these per-world data would
/// require redesigning that allocation to run after the boot definition loads. Values are read at probe/validate/replay
/// time only — never per-pixel.
/// </summary>
public static class WorldPlacementPolicy {
    /// <summary>The first reserved derived-face screen index — high in the engine's screen-surface range so it never
    /// collides with authored screens (which pack from index 0). Single-sourced here (rather than beside
    /// <c>Client.WorldPrototypeFacets.Derive</c>, which needs Puck.SdfVm and so cannot live in Puck.World.Schema)
    /// because the document validator must exclude the same reserved band an authored screen index cannot enter.</summary>
    public const int DerivedFaceBase = 24;
    /// <summary>The per-animated-placement shape-slot pool — equal to <see cref="MaxShapesPerStamp"/>, so an animated
    /// creation obeys the same stamp budget as a static one. CONTRACT INVARIANT for the same reason as
    /// <see cref="MaxShapesPerStamp"/>.</summary>
    public const int MaxAnimatedStampShapes = MaxShapesPerStamp;
    /// <summary>The most derived-face slots a world may reserve in the engine surface table.</summary>
    public const int MaxDerivedFaceScreens = (Puck.SignedDistance.SdfProgramBuilder.MaxScreenSurfaces - DerivedFaceBase);
    /// <summary>The per-stamp shape budget: the largest <see cref="Puck.World.Authoring.CreationDocument.StampShapeCount"/>
    /// (authored shapes + expanded text-run glyphs) a creation row may carry. CONTRACT INVARIANT: feeds
    /// <see cref="MaxAnimatedStampShapes"/>, which sizes <c>Client.WorldStampPool</c>'s per-slot
    /// stackalloc spans and (via <c>Client.WorldStampPool.SlotsPerPlacement</c>) the
    /// field-initializer-time dynamic-transform array. The validator's rejection line names this ceiling word-exactly.</summary>
    public const int MaxShapesPerStamp = 48;
    /// <summary>The document-wide ceiling on convex colliders materialized from SOLID placements by the analytic
    /// provider. Protects boot-time allocation and the per-body O(colliders) solver walk. <c>32768</c> admits one
    /// 48-shape stamp across hundreds of materialized pattern copies while refusing unbounded authored lattices
    /// before materialization.</summary>
    public const int MaxSolidPlacementColliders = 32_768;
    /// <summary>The hard cap on simultaneous STAMP-POOL registrations — an ANIMATED placement (a creation carrying
    /// timeline frames), an ATTACHED placement (<see cref="WorldPlacementAttach"/>, rooted on a live body), OR a
    /// body-rooted creation stamp (an inhabited placement's body, or a crowd body wearing a creation look).
    /// CONTRACT INVARIANT: sizes <c>Client.WorldStampPool</c>'s pool array
    /// (<c>new Registration?[MaxStampRegistrations]</c>) and the field-initializer-time dynamic-transform capacity — the
    /// validator's rejection line names this ceiling word-exactly. Set to 8: all three sources share the pool; the
    /// validator gates the two document-declared ones (animated + attached) against it and the pool degrades a starved
    /// body-rooted stamp to a catalog avatar with a loud warn.</summary>
    public const int MaxStampRegistrations = 8;
    /// <summary>The timeline replay hold per frame, in seconds — an 8-tick-at-60-Hz cadence, hold-style with no
    /// interpolation. Presentation-only (rides the render clock, never simulation state). A contract invariant, not
    /// an authoring knob — a world wanting a different replay feel is a future authoring surface.</summary>
    public const float TimelineSecondsPerFrame = (8f / 60f);

    /// <summary>Determines whether <paramref name="index"/> falls inside the reserved derived-face band
    /// <c>[<see cref="DerivedFaceBase"/>, DerivedFaceBase + <paramref name="derivedFaceScreens"/>)</c> — the ONE
    /// exclusion every rule that hands out a screen index shares (the document validator refuses an AUTHORED screen
    /// here; <c>Client.WorldSceneEmitter</c>'s authoring-headroom scan skips it).</summary>
    /// <param name="index">The engine screen-surface index to test.</param>
    /// <param name="derivedFaceScreens">The count of reserved derived-face slots (<c>authoring.derivedFaceScreens</c>).</param>
    /// <returns><see langword="true"/> when the index is reserved for a derived face.</returns>
    public static bool IsReservedFaceIndex(int index, int derivedFaceScreens) =>
        ((index >= DerivedFaceBase) && (index < (DerivedFaceBase + derivedFaceScreens)));

}
