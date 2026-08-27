using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Forge.Authoring;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.World;

/// <summary>
/// One derived face: the geometry a placement's declared creation face resolves to, plus the screen source it shows
/// and the slot that source claims.
/// </summary>
/// <param name="PlacementId">The owning placement's id.</param>
/// <param name="FaceName">The declared <see cref="Puck.Forge.Authoring.CreationFaceDocument.Name"/>.</param>
/// <param name="ShapeId">The named shape id, or <see langword="null"/> when the face names none.</param>
/// <param name="ShapeType">The named shape's primitive kind, or <see langword="null"/> when the face names none.</param>
/// <param name="Frame">The face's derived geometry.</param>
/// <param name="Aperture">The region recipe the named shape's primitive opens (<see cref="WorldFaceApertures"/>), or
/// <see langword="null"/> when it opens none — the face can be drawn, but not walked through.</param>
/// <param name="Source">The resolved screen source.</param>
/// <param name="ScreenIndex">The reserved derived-face screen index this row occupies, or <c>-1</c> when it holds
/// none — either because <paramref name="Source"/> renders nothing or because the band was exhausted (see
/// <paramref name="SlotStarved"/>).</param>
/// <param name="SlotStarved">Whether this row wanted a slot and the reserved band had none left. Geometry is
/// unaffected: the face still opens, it just shows nothing.</param>
public readonly record struct WorldFaceRow(
    string PlacementId,
    string FaceName,
    int? ShapeId,
    SdfSolidPrimitive? ShapeType,
    WorldFaceFrame Frame,
    WorldFaceApertureRecipe? Aperture,
    WorldScreenSource Source,
    int ScreenIndex,
    bool SlotStarved
);
/// <summary>
/// The one derivation of a document's placement faces — <c>(placements x declared creation faces)</c> walked once,
/// in document order, into geometry plus a resolved screen source. Trigger, arrival, render, and the
/// <c>world.faces</c> read-back all consume these rows rather than re-walking the same order independently.
/// </summary>
/// <remarks>
/// <para>GEOMETRY BEFORE SLOTS. Every face gets a frame regardless of whether a screen slot is available: a door is
/// a geometric fact, and slot exhaustion may only darken presentation. A face whose resolved source renders nothing
/// (<see cref="WorldScreenSource.None"/>) claims no slot at all.</para>
/// <para>FIXED POINT END TO END. Every frame constant is derived through <see cref="FixedQuaternion"/> and
/// <see cref="FixedQ4816"/> — yaw through the integer SinCos path, shape orientation through
/// <see cref="FixedQuaternion.FromQuaternion"/> — because the trigger decision these frames feed is simulation
/// state, and <see cref="System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3, float)"/> routes
/// to the platform's own libm. Rendering converts a finished frame to single precision at its own boundary; that
/// conversion is exactly rounded, so every machine draws the same geometry it collides with.</para>
/// <para>Cached per definition instance. A definition is swapped atomically per revision, so the instance IS the
/// revision and nothing needs to compare revision numbers.</para>
/// </remarks>
public sealed class WorldFaceCatalog {
    // The frame a face names no concrete shape for: a small billboard proud of the placement's own origin, on the
    // placement's yawed forward axis. Sized here rather than authored because there is no shape to read a size from.
    private const float FallbackHalfDepth = 0.04f;
    private const float FallbackHalfHeight = 0.45f;
    private const float FallbackHalfWidth = 0.6f;

    private readonly Dictionary<(string PlacementId, string FaceName), int> m_index;

    private static readonly ConditionalWeakTable<WorldDefinition, WorldFaceCatalog> PerDefinition = new();
    // Puck.Forge.Authoring.CreationFrame's 180°-about-+Y conversion quaternion, built the same way (an exact axis
    // swap/negate, never Quaternion.CreateFromAxisAngle) — the value every author-frame shape's own rotation carries
    // when the author declared none.
    private static readonly Quaternion EngineFrameHalfTurn = new(
        w: 0f,
        x: 0f,
        y: 1f,
        z: 0f
    );
    private static readonly FixedQ4816 FallbackHalfDepthFixed = FixedQ4816.FromDouble(value: FallbackHalfDepth);
    private static readonly FixedQ4816 FallbackHalfHeightFixed = FixedQ4816.FromDouble(value: FallbackHalfHeight);
    private static readonly FixedQ4816 FallbackHalfWidthFixed = FixedQ4816.FromDouble(value: FallbackHalfWidth);
    private static readonly FixedVector3 UnitX = new(
        X: FixedQ4816.One,
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.Zero
    );
    private static readonly FixedVector3 UnitY = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    );
    private static readonly FixedVector3 UnitZ = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.One
    );

    private WorldFaceCatalog(IReadOnlyList<WorldFaceRow> rows, IReadOnlyList<string> notices, int claimingFaceCount, int slotCapacity) {
        Rows = rows;
        Notices = notices;
        ClaimingFaceCount = claimingFaceCount;
        SlotCapacity = slotCapacity;
        m_index = new Dictionary<(string, string), int>(capacity: rows.Count);

        for (var index = 0; (index < rows.Count); index++) {
            m_index[(rows[index].PlacementId, rows[index].FaceName)] = index;
        }
    }

    /// <summary>Gets how many rows resolved to a source that renders something, and so asked for a screen slot.</summary>
    public int ClaimingFaceCount { get; }
    /// <summary>Gets the named echoes this derivation produced — an unresolvable default-source token, or a face the
    /// reserved band could not seat.</summary>
    public IReadOnlyList<string> Notices { get; }
    /// <summary>Gets the derived rows, in document order: placements outer, each placement's declared creation faces
    /// inner.</summary>
    public IReadOnlyList<WorldFaceRow> Rows { get; }
    /// <summary>Gets the reserved derived-face slot count this derivation allocated against
    /// (<c>authoring.derivedFaceScreens</c>).</summary>
    public int SlotCapacity { get; }

    private static bool DeclaresCamera(WorldDefinition definition, string name) {
        foreach (var camera in definition.Cameras) {
            if (string.Equals(
                a: camera.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return true;
            }
        }

        return false;
    }
    private static WorldFaceCatalog Derive(WorldDefinition definition) {
        var rows = new List<WorldFaceRow>();
        var notices = new List<string>();
        var cameras = DerivedCameraNames(definition: definition);
        var capacity = definition.Authoring.DerivedFaceScreens;
        var claiming = 0;
        var screenIndex = WorldPlacementPolicy.DerivedFaceBase;

        foreach (var placement in definition.Placements) {
            if (
                (placement is null) ||
                (WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: placement.PrototypeId
            ) is not { } creation)
            ) {
                continue;
            }

            foreach (var face in (creation.Document.Behavior?.Faces ?? [])) {
                var shape = FindShape(
                    document: creation.EngineDocument,
                    id: face.ShapeId
                );
                var source = (FindOverride(
                    faceSources: placement.FaceSources,
                    face: face.Name
                )
                    ?? (ParseDefaultSource(
                    token: face.DefaultSource,
                    cameras: cameras,
                    definition: definition,
                    placementId: placement.Id,
                    faceName: face.Name,
                    notices: notices
                )
                    ?? new WorldScreenSource.None()));
                var claimsSlot = (source is not WorldScreenSource.None);
                var seated = -1;
                var starved = false;

                if (claimsSlot) {
                    claiming++;

                    if (screenIndex < (WorldPlacementPolicy.DerivedFaceBase + capacity)) {
                        seated = screenIndex;
                        screenIndex++;
                    } else {
                        starved = true;
                        notices.Add(item: $"[world.faces: '{placement.Id}':'{face.Name}' is DARKENED — the {capacity}-slot reserved derived-face band is full, so this face shows nothing. Its geometry is unaffected: the door still opens]");
                    }
                }

                rows.Add(item: new WorldFaceRow(
                    PlacementId: placement.Id,
                    FaceName: face.Name,
                    ShapeId: shape?.Id,
                    ShapeType: shape?.Type,
                    Frame: DeriveFrame(
                        placement: placement,
                        shape: shape
                    ),
                    Aperture: WorldFaceApertures.For(primitive: shape?.Type),
                    Source: source,
                    ScreenIndex: seated,
                    SlotStarved: starved
                ));
            }
        }

        return new WorldFaceCatalog(
            claimingFaceCount: claiming,
            notices: notices,
            rows: rows,
            slotCapacity: capacity
        );
    }
    // The face frame. Origin is the named shape's own stamped center; the plane basis composes the placement's
    // authored yaw with the shape's own orientation; the half-extents are the shape's authored Scale under the
    // placement's uniform Scale — the MEASURED convention (a Box's drawn face matches its authored Scale, not
    // CreationGeometry's canonical reach table, which is a conservative circumscribing bound).
    //
    // ALL THREE AXES come from the one composed rotation. Taking Right and Normal from the quaternion while pinning
    // Up to world +Y describes a DIFFERENT plane the moment a shape carries pitch or roll: the renderer rebuilds its
    // own normal as Cross(Right, Up) (Client.WorldScreenStamper), so the drawn screen and the walked slab would sit
    // on planes that disagree by the pitch angle, with nothing to notice it. A yaw-only face is unaffected bit for
    // bit — rotating (0,1,0) about +Y leaves it unchanged exactly (both cross products vanish).
    private static WorldFaceFrame DeriveFrame(WorldPlacement placement, ShapeDocument? shape) {
        var origin = FixedVector3.FromVector3(value: placement.Position);
        var scale = FixedQ4816.FromDouble(value: placement.Scale);
        var yawDegrees = FixedQ4816.FromDouble(value: placement.YawDegrees);

        // Authored cardinal yaw with an unrotated (or half-turned) face has an exact axis-aligned frame. Sending those
        // angles through pi, SinCos, quaternion rotation, and normalization introduces a small perpendicular
        // component, so reciprocal quilt faces that occupy the same plane derive different seam points. Preserve the
        // exact authored geometry. A shape's own rotation is HalfTurn (0,1,0,0), never Identity, whenever it entered
        // the engine through Puck.Forge.Authoring.CreationFrame — the author-frame conversion pre-multiplies every
        // shape by exactly that quaternion, so an author's OWN unrotated shape is this exact value, not Identity, on
        // every real creation. Both are pure Y rotations Right/Normal negate under exactly, so both stay on this path.
        var shapeIsIdentity = ((shape is null) || shape.Rotation.Equals(other: Quaternion.Identity));
        var shapeIsHalfTurn = ((shape is not null) && shape.Rotation.Equals(other: EngineFrameHalfTurn));

        if (
            (shapeIsIdentity || shapeIsHalfTurn) &&
            TryCardinalBasis(
            normal: out var cardinalNormal,
            right: out var cardinalRight,
            yawDegrees: yawDegrees
        )
        ) {
            if (shapeIsHalfTurn) {
                cardinalNormal = -cardinalNormal;
                cardinalRight = -cardinalRight;
            }

            if (shape is null) {
                return new WorldFaceFrame(
                    HalfDepth: FallbackHalfDepthFixed,
                    HalfHeight: FallbackHalfHeightFixed,
                    HalfWidth: FallbackHalfWidthFixed,
                    Normal: cardinalNormal,
                    Origin: origin,
                    Right: cardinalRight,
                    Up: UnitY
                );
            }

            var local = (FixedVector3.FromVector3(value: shape.Position) * scale);
            var halfCardinal = (FixedVector3.FromVector3(value: shape.Scale) * scale);

            return new WorldFaceFrame(
                Origin: (((origin + (cardinalRight * local.X)) + (UnitY * local.Y)) + (cardinalNormal * local.Z)),
                Right: cardinalRight,
                Up: UnitY,
                Normal: cardinalNormal,
                HalfWidth: FixedQ4816.Abs(value: halfCardinal.X),
                HalfHeight: FixedQ4816.Abs(value: halfCardinal.Y),
                HalfDepth: FixedQ4816.Abs(value: halfCardinal.Z)
            );
        }

        var placementRotation = FixedQuaternion.FromAxisAngle(
            angle: (yawDegrees * WorldAngles.DegreesToRadians),
            axis: UnitY
        );

        if (shape is null) {
            return new WorldFaceFrame(
                Origin: origin,
                Right: placementRotation.Rotate(vector: UnitX).Normalize(),
                Up: placementRotation.Rotate(vector: UnitY).Normalize(),
                Normal: placementRotation.Rotate(vector: UnitZ).Normalize(),
                HalfWidth: FallbackHalfWidthFixed,
                HalfHeight: FallbackHalfHeightFixed,
                HalfDepth: FallbackHalfDepthFixed
            );
        }

        var surfaceRotation = (placementRotation * FixedQuaternion.FromQuaternion(value: shape.Rotation).Normalize()).Normalize();
        var half = (FixedVector3.FromVector3(value: shape.Scale) * scale);

        return new WorldFaceFrame(
            Origin: (origin + placementRotation.Rotate(vector: (FixedVector3.FromVector3(value: shape.Position) * scale))),
            Right: surfaceRotation.Rotate(vector: UnitX).Normalize(),
            Up: surfaceRotation.Rotate(vector: UnitY).Normalize(),
            Normal: surfaceRotation.Rotate(vector: UnitZ).Normalize(),
            HalfWidth: FixedQ4816.Abs(value: half.X),
            HalfHeight: FixedQ4816.Abs(value: half.Y),
            HalfDepth: FixedQ4816.Abs(value: half.Z)
        );
    }
    private static HashSet<string> DerivedCameraNames(WorldDefinition definition) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var placement in definition.Placements) {
            if (
                (placement is null) ||
                (WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: placement.PrototypeId
            ) is not { } creation)
            ) {
                continue;
            }

            foreach (var eye in (creation.Document.Cameras ?? [])) {
                _ = names.Add(item: DerivedCameraName(
                    placementId: placement.Id,
                    feed: (eye.Feed ?? eye.Id.ToString(provider: CultureInfo.InvariantCulture))
                ));
            }
        }

        return names;
    }
    private static WorldScreenSource? FindOverride(IReadOnlyList<WorldPlacementFace>? faceSources, string face) {
        foreach (var entry in (faceSources ?? [])) {
            if (
                (entry is not null) &&
                string.Equals(
                a: entry.Face,
                b: face,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return entry.Source;
            }
        }

        return null;
    }
    private static ShapeDocument? FindShape(CreationDocument document, int? id) {
        if (
            (id is not { } targetId) ||
            (targetId < 0)
        ) {
            return null;
        }

        foreach (var shape in (document.Shapes ?? [])) {
            if (shape.Id == targetId) {
                return shape;
            }
        }

        return null;
    }
    // The closed four-token default-source grammar: none -> None; test -> TestPattern; feed:<name> / camera:<name>
    // -> View of the named camera, resolved against the derived creation-eye feeds then the world's own camera rows.
    // Anything else resolves to null (the caller falls back to None) plus one named echo.
    private static WorldScreenSource? ParseDefaultSource(string? token, HashSet<string> cameras, WorldDefinition definition, string placementId, string faceName, List<string> notices) {
        if (
            string.IsNullOrWhiteSpace(value: token) ||
            string.Equals(
            a: token,
            b: "none",
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return null;
        }

        if (string.Equals(
            a: token,
            b: "test",
            comparisonType: StringComparison.Ordinal
        )) {
            return new WorldScreenSource.TestPattern(
                Height: 192,
                Width: 256
            );
        }

        var name = (token.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "camera:"
        )
            ? token["camera:".Length..]
            : (token.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "feed:"
            )
                ? token["feed:".Length..]
                : null
        ));

        if (
            (name is not null) &&
            (cameras.Contains(item: name) || DeclaresCamera(
            definition: definition,
            name: name
        ))
        ) {
            return new WorldScreenSource.View(CameraName: name);
        }

        notices.Add(item: $"[world.faces: '{placementId}':'{faceName}' default source '{token}' is not a known token (none|test|camera:<name>|feed:<name>) or names no camera — it lights the no-signal card]");

        return null;
    }
    private static bool TryCardinalBasis(FixedQ4816 yawDegrees, out FixedVector3 right, out FixedVector3 normal) {
        const long QuarterTurnRaw = (90L << FixedQ4816.FractionBitCount);
        const long FullTurnRaw = (QuarterTurnRaw * 4L);
        var yaw = (yawDegrees.Value % FullTurnRaw);

        if (yaw < 0L) {
            yaw += FullTurnRaw;
        }

        switch (yaw) {
            case 0L:
                right = UnitX;
                normal = UnitZ;
                return true;
            case QuarterTurnRaw:
                right = -UnitZ;
                normal = UnitX;
                return true;
            case (QuarterTurnRaw * 2L):
                right = -UnitX;
                normal = -UnitZ;
                return true;
            case (QuarterTurnRaw * 3L):
                right = UnitZ;
                normal = -UnitX;
                return true;
            default:
                right = default;
                normal = default;
                return false;
        }
    }

    /// <summary>The camera feed name a placement's creation eye derives — the one formula both the derived-camera
    /// rows and a face's <c>camera:</c>/<c>feed:</c> source token resolve through.</summary>
    /// <param name="placementId">The owning placement's id.</param>
    /// <param name="feed">The eye's declared feed name, or its id when it declares none.</param>
    /// <returns>The derived camera name.</returns>
    public static string DerivedCameraName(string placementId, string feed) => $"creation:{placementId}:{feed}";
    /// <summary>Gets the derived faces for a definition, deriving them on first ask and reusing them for the life of
    /// that definition instance.</summary>
    /// <param name="definition">The definition to derive from.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldFaceCatalog For(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        return PerDefinition.GetValue(
            key: definition,
            createValueCallback: static built => Derive(definition: built)
        );
    }
    /// <summary>Finds one derived face row.</summary>
    /// <param name="placementId">The owning placement's id.</param>
    /// <param name="faceName">The declared face name.</param>
    /// <param name="row">The row when it resolves; otherwise the default.</param>
    /// <returns><see langword="true"/> when the placement declares that face.</returns>
    public bool TryFind(string placementId, string faceName, out WorldFaceRow row) {
        if (m_index.TryGetValue(
            key: (placementId, faceName),
            value: out var index
        )) {
            row = Rows[index];

            return true;
        }

        row = default;

        return false;
    }
}
