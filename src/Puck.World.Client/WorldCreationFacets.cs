using System.Globalization;
using System.Numerics;

namespace Puck.World.Client;

/// <summary>
/// The one creation-facet derivation pass: <c>(placements x creations) -> derived world rows</c>, computed at the
/// delivery boundary and never written to the document, so <c>world.save</c> stays clean. Sound already derives
/// through <c>WorldAudioDirector.DeriveCreationSounds</c>; this pass adds the cameras facet (a creation's
/// declared eyes become <see cref="WorldCamera"/> feeds on <see cref="WorldAnchor.Placement"/>) and the faces facet
/// (a creation's declared screen surfaces become derived <see cref="WorldScreen"/> rows lit by any feed the author
/// names — including another creation's eye). The implicit-creation-look facet resolves inline in the frame source
/// (an inhabited body wears its own creation), so it needs no row here.
/// </summary>
/// <remarks>Face geometry is not derived here: <see cref="WorldFaceCatalog"/> owns it, in fixed point, shared with
/// the portal trigger and the arrival isometry. This class is the render consumer — it converts a finished frame to
/// single precision once and applies the render-only policy (proud epsilon, interior fraction) on top.</remarks>
public static class WorldCreationFacets {
    // RENDER POLICY over the shared frame, not geometry: the slab sits proud of the face surface by
    // FaceProudEpsilon so its zero-set never coincides with the host shape's (coincident zero-sets speckle), and it
    // covers FaceInteriorFraction of the frame so the drawn image reads as an inset screen rather than reaching the
    // frame's edge. The portal trigger reads the SAME frame and applies neither.
    private const float FaceInteriorFraction = 0.8f;
    private const float FaceMinimumHalfDepth = 0.01f;
    private const float FaceProudEpsilon = 0.05f;
    private const uint FaceRenderHeight = 192;
    private const uint FaceRenderWidth = 256;
    private const float FaceRound = 0.02f;
    // Where the reserved-band placeholder for an unclaimed slot parks: far below any play area, so a slot that
    // exists only to keep the frozen provider key set complete is never seen.
    private const float PlaceholderDepth = -1000f;
    private const float PlaceholderHalfDepth = 0.04f;
    private const float PlaceholderHalfHeight = 0.45f;
    private const float PlaceholderHalfWidth = 0.6f;

    /// <summary>The first reserved derived-face screen index — high in the 0..<see cref="Puck.SignedDistance.SdfProgramBuilder.MaxScreenSurfaces"/>
    /// range so it never collides with authored screens (which pack from index 0). The binder registers
    /// <c>[DerivedFaceBase, DerivedFaceBase + DerivedFaceScreens)</c> up front so a derived face re-points a slot that
    /// already exists (the render provider key set is frozen at boot). Single-sourced in
    /// <see cref="WorldPlacementPolicy.DerivedFaceBase"/> (Puck.World.Schema) — the document validator needs the same
    /// reserved band and cannot reference this class (it needs Puck.SdfVm for the classes it derives against).</summary>
    public const int DerivedFaceBase = WorldPlacementPolicy.DerivedFaceBase;

    // THE ONE FRAME CONVERSION BOUNDARY for rendering. The frame is derived in fixed point (WorldFaceCatalog) because
    // the portal trigger it also feeds is simulation state; fixed-to-float is exactly rounded, so every machine draws
    // the slab it collides with. Everything applied here — proud epsilon, interior fraction, the round radius, the
    // minimum half-depth — is render policy over that one geometry, never part of it.
    private static WorldScreen FaceScreen(int index, WorldFaceFrame frame, WorldScreenSource source) {
        var normal = frame.Normal.ToVector3();
        var halfDepth = ((float)((double)frame.HalfDepth));

        return new WorldScreen(
            Index: index,
            Origin: (frame.Origin.ToVector3() + (normal * (halfDepth + FaceProudEpsilon))),
            Right: frame.Right.ToVector3(),
            Up: frame.Up.ToVector3(),
            HalfWidth: (((float)((double)frame.HalfWidth)) * FaceInteriorFraction),
            HalfHeight: (((float)((double)frame.HalfHeight)) * FaceInteriorFraction),
            HalfDepth: MathF.Max(
                x: (halfDepth * FaceInteriorFraction),
                y: FaceMinimumHalfDepth
            ),
            Round: FaceRound,
            Source: source,
            Route: WorldScreenRoute.Passive
        );
    }
    private static WorldScreen PlaceholderScreen(int index) => new(
        Index: index,
        Origin: new Vector3(
            x: 0f,
            y: PlaceholderDepth,
            z: 0f
        ),
        Right: Vector3.UnitX,
        Up: Vector3.UnitY,
        HalfWidth: PlaceholderHalfWidth,
        HalfHeight: PlaceholderHalfHeight,
        HalfDepth: PlaceholderHalfDepth,
        Round: FaceRound,
        Source: new WorldScreenSource.None(),
        Route: WorldScreenRoute.Passive
    );

    /// <summary>Derives the camera and face rows a definition's placements imply. Face rows come from
    /// <see cref="WorldFaceCatalog"/> — the same derivation the portal trigger and arrival isometry read — so nothing
    /// re-walks the placement/face order to assign slots. Rows the catalog seated no slot for (a face whose source
    /// renders nothing, or one the reserved band could not seat) contribute no screen; the reserved range is padded
    /// so every boot-registered slot is always covered.</summary>
    /// <param name="definition">The delivered definition.</param>
    /// <param name="derivedFaceBase">The first reserved derived-face screen index.</param>
    /// <param name="derivedFaceScreens">The count of reserved derived-face screen slots.</param>
    /// <returns>The derived camera and face rows.</returns>
    public static DerivedFacets Derive(WorldDefinition definition, int derivedFaceBase, int derivedFaceScreens) {
        var catalog = WorldFaceCatalog.For(definition: definition);
        var cameras = new List<WorldCamera>();
        var faces = new List<WorldScreen>();
        var seated = new HashSet<int>();

        foreach (var placement in definition.Placements) {
            if (WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: placement.CreationId
            ) is not { } creation) {
                continue;
            }

            foreach (var eye in (creation.EngineDocument.Cameras ?? [])) {
                cameras.Add(item: new WorldCamera(
                    Name: WorldFaceCatalog.DerivedCameraName(
                        placementId: placement.Id,
                        feed: (eye.Feed ?? eye.Id.ToString(provider: CultureInfo.InvariantCulture))
                    ),
                    Anchor: new WorldAnchor.Placement(
                        PlacementId: placement.Id,
                        ShapeId: eye.ShapeId
                    ),
                    Rig: new WorldCameraRig(
                        Motion: new WorldCameraMotion.Follow(
                            Offset: eye.Position,
                            WorldAxes: false,
                            SpreadPullback: 0f
                        ),
                        Aim: new WorldCameraAim.Forward(FocusDistance: (eye.Focus ?? 1f)),
                        Lens: new WorldCameraLens(FieldOfViewRadians: ((eye.Fov ?? 60f) * (MathF.PI / 180f)))
                    ),
                    RenderWidth: FaceRenderWidth,
                    RenderHeight: FaceRenderHeight
                ));
            }
        }

        foreach (var notice in catalog.Notices) {
            Console.Error.WriteLine(value: notice);
        }

        foreach (var row in catalog.Rows) {
            if (row.ScreenIndex < 0) {
                continue;
            }

            if (row.ScreenIndex >= (derivedFaceBase + derivedFaceScreens)) {
                // Unreachable while the server refuses a live authoring.derivedFaceScreens raise past the boot
                // reservation (Server.WorldServer.BootDerivedFaceScreens). Named rather than skipped, because a
                // seated row the renderer silently dropped is exactly the disagreement that gate exists to prevent.
                Console.Error.WriteLine(value: $"[world.faces: '{row.PlacementId}':'{row.FaceName}' was seated at screen {row.ScreenIndex}, past this renderer's boot-reserved band [{derivedFaceBase}, {(derivedFaceBase + derivedFaceScreens)}) — it cannot be shown]");

                continue;
            }

            _ = seated.Add(item: row.ScreenIndex);
            faces.Add(item: FaceScreen(
                index: row.ScreenIndex,
                frame: row.Frame,
                source: row.Source
            ));
        }

        // Pad the reserved range so the reconcile always covers every reserved slot — a slot dropped from the
        // incoming set would be removed and could never re-bind (the range is frozen at boot).
        for (var index = derivedFaceBase; (index < (derivedFaceBase + derivedFaceScreens)); index++) {
            if (!seated.Contains(item: index)) {
                faces.Add(item: PlaceholderScreen(index: index));
            }
        }

        return new DerivedFacets(
            Cameras: cameras,
            Faces: faces
        );
    }
    /// <summary>Determines whether <paramref name="index"/> falls inside the reserved derived-face band
    /// <c>[<see cref="DerivedFaceBase"/>, DerivedFaceBase + <paramref name="derivedFaceScreens"/>)</c> — the one
    /// exclusion every rule that hands out a screen index shares. Two of them exist:
    /// <c>WorldDefinitionValidator</c> refuses an authored screen here, and <c>WorldSceneEmitter</c>'s
    /// authoring-headroom scan skips it. They must not be two independently-correct rules — a headroom slot claimed
    /// inside this band collides with the binder's boot-reserved placeholder just as an authored screen would.
    /// Forwards to <see cref="WorldPlacementPolicy.IsReservedFaceIndex"/>.</summary>
    /// <param name="index">The engine screen-surface index to test.</param>
    /// <param name="derivedFaceScreens">The count of reserved derived-face slots (<c>authoring.derivedFaceScreens</c>).</param>
    /// <returns><see langword="true"/> when the index is reserved for a derived face.</returns>
    public static bool IsReservedFaceIndex(int index, int derivedFaceScreens) =>
        WorldPlacementPolicy.IsReservedFaceIndex(
            derivedFaceScreens: derivedFaceScreens,
            index: index
        );
    /// <summary>The boot-reserved derived-face screen slots (None-sourced placeholders) the binder must register up
    /// front, so a derived face appearing at a later delivery re-points a slot that already exists rather than hitting
    /// the frozen provider key set.</summary>
    /// <param name="derivedFaceBase">The first reserved derived-face screen index.</param>
    /// <param name="derivedFaceScreens">The count of reserved slots.</param>
    /// <returns>The placeholder rows.</returns>
    public static IReadOnlyList<WorldScreen> ReservedFaceSlots(int derivedFaceBase, int derivedFaceScreens) {
        var slots = new List<WorldScreen>(capacity: derivedFaceScreens);

        for (var offset = 0; (offset < derivedFaceScreens); offset++) {
            slots.Add(item: PlaceholderScreen(index: (derivedFaceBase + offset)));
        }

        return slots;
    }

    /// <summary>The derived rows a delivery produces: creation-eye cameras and creation-face screens. Neither is ever
    /// written to the document — they are recomputed each delivery from <c>(placements x creations)</c>.</summary>
    /// <param name="Cameras">The derived camera feeds, concatenated onto the document's own camera rows.</param>
    /// <param name="Faces">The derived face screens at the reserved index range, reconciled onto the boot-reserved slots.</param>
    public readonly record struct DerivedFacets(IReadOnlyList<WorldCamera> Cameras, IReadOnlyList<WorldScreen> Faces);
}
