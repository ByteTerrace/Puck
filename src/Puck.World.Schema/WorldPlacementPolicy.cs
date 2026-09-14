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
    /// field-initializer-time dynamic-transform array. The validator's rejection line names this ceiling word-exactly.
    /// The stamp pool's own worst-case instance draw is <see cref="MaxStampRegistrations"/> x this value; every
    /// other boot-probe consumer (the avatar catalog, static placements, screens, every adjacency band's
    /// reservation, the field bricks) draws against the same
    /// <c>Puck.SignedDistance.SdfProgramBuilder.MaxInstances</c> ceiling (65536, itself synced to
    /// <c>SDF_MAX_INSTANCES</c> in <c>sdf-vm.hlsli</c>), and those draws are not independent of this constant either
    /// (the static-placement probe also reserves one instance per <see cref="MaxShapesPerStamp"/>-shape floor per
    /// authoring-headroom placement, so the shipped overworld's whole probe grows by 144 instances per shape here:
    /// 128 pool registrations + 16 headroom placements). 367 is the measured ceiling that keeps the shipped
    /// overworld's whole COMPOSED boot probe — the presenter's four emitters, the adjacency reservation included —
    /// at least 4096 instances under the 65536 cap (measured at 367: stamp pool 46976, composed boot total 61392,
    /// headroom 4144; 368 measures 4000 headroom, under the floor). The scene emitter alone under-counts by the
    /// adjacency and field reservations (1680 instances for the shipped overworld's ten bands), which is why the
    /// figure that governs this constant is the composed one. A raise here is a measured decision, not a free one —
    /// re-measure with <c>WorldRenderEnvelopeLawTests.ShippedWorldBootProbeInstancesFitTheEngineCeilingWithHeadroom</c>
    /// before changing it.</summary>
    public const int MaxShapesPerStamp = 367;
    /// <summary>The document-wide ceiling on convex colliders materialized from SOLID placements by the analytic
    /// provider. Protects boot-time allocation and the per-body O(colliders) solver walk. <c>32768</c> admits one
    /// full-budget stamp across hundreds of materialized pattern copies while refusing unbounded authored lattices
    /// before materialization.</summary>
    public const int MaxSolidPlacementColliders = 32_768;
    /// <summary>The stamp-pool registration count — one slot per body in the detailed render band
    /// (<see cref="WorldBodiesLimits.DetailedRenderBand"/>), shared by the three registration sources: an ANIMATED
    /// placement (a creation carrying timeline frames), an ATTACHED placement (<see cref="WorldPlacementAttach"/>,
    /// rooted on a live body), and a body-rooted creation stamp (an inhabited placement's body, or a crowd body
    /// wearing a creation look). Derived, never authored: the band that gets a full rig is the band that gets its
    /// creature, so a detailed body's creation stamp cannot starve behind a smaller pool. The validator gates the two
    /// document-declared sources (animated + attached) against this count; a body-rooted stamp past it degrades to
    /// the catalog avatar with a loud warn, exactly as a body past the band degrades to a coarse instance. CONTRACT
    /// INVARIANT: sizes <c>Client.WorldStampPool</c>'s pool array and the field-initializer-time dynamic-transform
    /// capacity; the validator's rejection line names this ceiling word-exactly.</summary>
    public const int MaxStampRegistrations = WorldBodiesLimits.DetailedRenderBand;
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
