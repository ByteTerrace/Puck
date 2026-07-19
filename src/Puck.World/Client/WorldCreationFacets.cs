using System.Globalization;
using System.Numerics;
using Puck.Authoring;

namespace Puck.World.Client;

/// <summary>
/// The one creation-facet derivation pass: <c>(placements × creations) → derived world rows</c>, computed at the
/// delivery boundary and NEVER written to the document, so <c>world.save</c> stays clean. Sound already derives through
/// <see cref="WorldAudioDirector.DeriveCreationSounds"/>; this pass adds the CAMERAS facet (a creation's declared eyes
/// become <see cref="WorldCamera"/> feeds on <see cref="WorldAnchor.Placement"/>) and the FACES facet (a creation's
/// declared screen surfaces become derived <see cref="WorldScreen"/> rows lit by any feed the author names — including
/// another creation's eye). Every derivation shares this one entry point rather than forking the "derive a world row from
/// a facet" contract N ways (P5). The implicit-creation-look facet resolves inline in the frame source (an inhabited
/// body wears its own creation), so it needs no row here.
/// </summary>
internal static class WorldCreationFacets {
    /// <summary>The first reserved derived-face screen index — high in the 0..<see cref="Puck.SdfVm.SdfProgramBuilder.MaxScreenSurfaces"/>
    /// range so it never collides with authored screens (which pack from index 0). The binder registers
    /// <c>[DerivedFaceBase, DerivedFaceBase + DerivedFaceScreens)</c> up front so a derived face re-points a slot that
    /// already exists (the render provider key set is frozen at boot).</summary>
    public const int DerivedFaceBase = 24;

    // A derived face slab's half-extents and screen-plane basis — a small static billboard at the placement position.
    private const float FaceHalfWidth = 0.6f;
    private const float FaceHalfHeight = 0.45f;
    private const float FaceHalfDepth = 0.04f;
    private const uint FaceRenderWidth = 256;
    private const uint FaceRenderHeight = 192;

    /// <summary>The derived rows a delivery produces: creation-eye cameras and creation-face screens. Neither is ever
    /// written to the document — they are recomputed each delivery from <c>(placements × creations)</c>.</summary>
    /// <param name="Cameras">The derived camera feeds, concatenated onto the document's own camera rows.</param>
    /// <param name="Faces">The derived face screens at the reserved index range, reconciled onto the boot-reserved slots.</param>
    internal readonly record struct DerivedFacets(IReadOnlyList<WorldCamera> Cameras, IReadOnlyList<WorldScreen> Faces);

    /// <summary>Derives the camera and face rows a definition's placements imply. The face rows land at
    /// <c>[derivedFaceBase, derivedFaceBase + derivedFaceScreens)</c>; a definition declaring more faces than the
    /// reserved range warns and drops the overflow (the render provider key set is frozen at boot).</summary>
    /// <param name="definition">The delivered definition.</param>
    /// <param name="placements">The (possibly drag/workbench-composed) placement rows.</param>
    /// <param name="derivedFaceBase">The first reserved derived-face screen index.</param>
    /// <param name="derivedFaceScreens">The count of reserved derived-face screen slots.</param>
    public static DerivedFacets Derive(WorldDefinition definition, IReadOnlyList<WorldPlacement> placements, int derivedFaceBase, int derivedFaceScreens) {
        var cameras = new List<WorldCamera>();
        var faces = new List<WorldScreen>();
        var derivedCameraNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var faceIndex = derivedFaceBase;

        foreach (var placement in placements) {
            if (WorldPlacementStamper.FindCreation(creations: definition.Creations, id: placement.CreationId) is not { } creation) {
                continue;
            }

            foreach (var eye in (creation.Document.Cameras ?? [])) {
                var feed = (eye.Feed ?? eye.Id.ToString(provider: CultureInfo.InvariantCulture));
                var name = $"creation:{placement.Id}:{feed}";

                _ = derivedCameraNames.Add(item: name);
                cameras.Add(item: new WorldCamera(
                    Name: name,
                    Anchor: new WorldAnchor.Placement(PlacementId: placement.Id, ShapeId: eye.ShapeId),
                    Offset: eye.Position,
                    Rig: new WorldRig.FirstPerson(EyeOffset: Vector3.Zero, FocusDistance: (eye.Focus ?? 1f), FieldOfViewRadians: ((eye.Fov ?? 60f) * (MathF.PI / 180f))),
                    RenderWidth: FaceRenderWidth,
                    RenderHeight: FaceRenderHeight
                ));
            }
        }

        foreach (var placement in placements) {
            if (WorldPlacementStamper.FindCreation(creations: definition.Creations, id: placement.CreationId) is not { } creation) {
                continue;
            }

            foreach (var face in (creation.Document.Behavior?.Faces ?? [])) {
                if (faceIndex >= (derivedFaceBase + derivedFaceScreens)) {
                    Console.Error.WriteLine(value: $"[world.faces: '{placement.Id}':'{face.Name}' has no reserved derived-face slot — the {derivedFaceScreens}-slot range is full; it drops]");

                    break;
                }

                var source = (FindOverride(faceSources: placement.FaceSources, face: face.Name)
                    ?? ParseDefaultSource(token: face.DefaultSource, cameras: derivedCameraNames, definition: definition, placement: placement, face: face)
                    ?? new WorldScreenSource.None());

                faces.Add(item: FaceScreen(index: faceIndex, placement: placement, source: source));
                faceIndex++;
            }
        }

        // Pad the reserved range with None placeholders so the reconcile always covers every reserved slot (a slot
        // dropped from the incoming set would be removed and could never re-bind — the range is frozen at boot).
        for (; (faceIndex < (derivedFaceBase + derivedFaceScreens)); faceIndex++) {
            faces.Add(item: FaceScreen(index: faceIndex, placement: null, source: new WorldScreenSource.None()));
        }

        return new DerivedFacets(Cameras: cameras, Faces: faces);
    }

    /// <summary>The boot-reserved derived-face screen slots (None-sourced placeholders) the binder must register up
    /// front, so a derived face appearing at a later delivery re-points a slot that already exists rather than hitting
    /// the frozen provider key set.</summary>
    /// <param name="derivedFaceBase">The first reserved derived-face screen index.</param>
    /// <param name="derivedFaceScreens">The count of reserved slots.</param>
    public static IReadOnlyList<WorldScreen> ReservedFaceSlots(int derivedFaceBase, int derivedFaceScreens) {
        var slots = new List<WorldScreen>(capacity: derivedFaceScreens);

        for (var offset = 0; (offset < derivedFaceScreens); offset++) {
            slots.Add(item: FaceScreen(index: (derivedFaceBase + offset), placement: null, source: new WorldScreenSource.None()));
        }

        return slots;
    }

    // A derived face's screen row: a small static billboard at the placement position (a documented simplification — it
    // shows the right feed at a fixed slab rather than tracking the body's live pose), or the reserved placeholder at
    // the origin when no placement backs it.
    private static WorldScreen FaceScreen(int index, WorldPlacement? placement, WorldScreenSource source) {
        var origin = ((placement is { } row) ? (row.Position + new Vector3(x: 0f, y: 1.2f, z: 0f)) : new Vector3(x: 0f, y: -1000f, z: 0f));

        return new WorldScreen(
            Index: index,
            Origin: origin,
            Right: Vector3.UnitX,
            Up: Vector3.UnitY,
            HalfWidth: FaceHalfWidth,
            HalfHeight: FaceHalfHeight,
            HalfDepth: FaceHalfDepth,
            Round: 0.02f,
            Source: source,
            Route: WorldScreenRoute.Passive
        );
    }

    private static WorldScreenSource? FindOverride(IReadOnlyList<WorldPlacementFace>? faceSources, string face) {
        foreach (var entry in (faceSources ?? [])) {
            if (string.Equals(a: entry.Face, b: face, comparisonType: StringComparison.Ordinal)) {
                return entry.Source;
            }
        }

        return null;
    }

    // The closed four-token default-source grammar (the ONLY new grammar this arc introduces): none → None; test →
    // TestPattern; feed:<name> / camera:<name> → View(<name>) resolved against the derived-camera names then the
    // world's own camera rows; anything else → null (the caller falls back to None) plus one stderr warn. The Demo's
    // `named:emotes` deliberately does not survive — it named a Demo host registry that does not exist here.
    private static WorldScreenSource? ParseDefaultSource(string? token, HashSet<string> cameras, WorldDefinition definition, WorldPlacement placement, CreationFaceDocument face) {
        if (string.IsNullOrWhiteSpace(value: token) || string.Equals(a: token, b: "none", comparisonType: StringComparison.Ordinal)) {
            return null;
        }

        if (string.Equals(a: token, b: "test", comparisonType: StringComparison.Ordinal)) {
            return new WorldScreenSource.TestPattern(Width: 256, Height: 192);
        }

        var name = (token.StartsWith(value: "camera:", comparisonType: StringComparison.Ordinal) ? token["camera:".Length..]
            : token.StartsWith(value: "feed:", comparisonType: StringComparison.Ordinal) ? token["feed:".Length..]
            : null);

        if ((name is not null) && (cameras.Contains(item: name) || DeclaresCamera(definition: definition, name: name))) {
            return new WorldScreenSource.View(CameraName: name);
        }

        Console.Error.WriteLine(value: $"[world.faces: '{placement.Id}':'{face.Name}' default source '{token}' is not a known token (none|test|camera:<name>|feed:<name>) or names no camera — it lights the no-signal card]");

        return null;
    }

    private static bool DeclaresCamera(WorldDefinition definition, string name) {
        foreach (var camera in definition.Cameras) {
            if (string.Equals(a: camera.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }
}
