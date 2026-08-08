using System.Numerics;

namespace Puck.Demo.Editing;

/// <summary>The rotation-snap increment: no snap, 90°, or 45°. World-sculpt applies it to a scalar yaw; creator
/// snaps a full orientation to the nearest element of a precomputed coarse-orientation candidate set.</summary>
internal enum RotationSnap {
    /// <summary>No rotation snapping.</summary>
    Off = 0,
    /// <summary>Snap to the nearest 90° lattice orientation (the 24-element octahedral rotation group in creator).</summary>
    Deg90 = 1,
    /// <summary>Snap to the nearest 45° lattice orientation (a richer 45°-granular candidate set in creator).</summary>
    Deg45 = 2,
}

/// <summary>A captured align-to-shape reference — the frozen guide the moved shape snaps against (see
/// <c>GridSnap</c> §1b/1c). Snapshotted at capture time so a later delete/move of the source shape never disturbs
/// the guide (the resolved F4 rule). All fields are authoring-side floats.</summary>
/// <param name="Origin">The reference frame's origin, world space.</param>
/// <param name="Frame">The reference frame's orientation (world-sculpt = a pure yaw quaternion; creator = the full
/// shape rotation).</param>
/// <param name="Pitch">The per-axis object-lattice pitch, in reference-local space (a component &lt;= 0 disables the
/// lattice on that axis; the face candidates still speak).</param>
/// <param name="LocalHalfExtents">The reference's half-extents along its OWN axes — the butt-join face planes sit at
/// <c>±LocalHalfExtents.c</c> per axis.</param>
/// <param name="FaceRadius">The face-snap capture radius (reference-local units): within it, a face/center candidate
/// wins over a lattice node.</param>
internal readonly record struct SnapReference(
    Vector3 Origin,
    Quaternion Frame,
    Vector3 Pitch,
    Vector3 LocalHalfExtents,
    float FaceRadius
);

/// <summary>The snap configuration a caller threads through <see cref="GridSnap"/> — pure declarative state, no
/// behavior. Session-only (the resolved F6 rule): never persisted to a document.</summary>
/// <param name="Enabled">Whether snapping is active at all (off = every function returns its input untouched).</param>
/// <param name="Pitch">The world-lattice per-axis pitch (origin at world 0). A component &lt;= 0 = free on that axis
/// — this is how world-sculpt leaves Y untouched (<c>Pitch.Y = 0</c>, floor-rest).</param>
/// <param name="Rotation">The rotation-snap increment.</param>
/// <param name="Reference">The align-to-shape reference, or null for world-lattice-only.</param>
internal readonly record struct SnapConfig(
    bool Enabled,
    Vector3 Pitch,
    RotationSnap Rotation,
    SnapReference? Reference
) {
    /// <summary>The default world-sculpt config: snapping off, pitch matched to <c>WalkGridBaker.CellSize</c> (0.25 wu)
    /// on X/Z with Y free (floor-rest), no rotation snap, no reference.</summary>
    public static SnapConfig WorldDefault =>
        new(Enabled: false, Pitch: new Vector3(x: 0.25f, y: 0f, z: 0.25f), Rotation: RotationSnap.Off, Reference: null);

    /// <summary>The default creator config: snapping off, a sub-metre uniform pitch (0.25 wu on all axes), no rotation
    /// snap, no reference.</summary>
    public static SnapConfig CreatorDefault =>
        new(Enabled: false, Pitch: new Vector3(x: 0.25f, y: 0.25f, z: 0.25f), Rotation: RotationSnap.Off, Reference: null);
}

/// <summary>
/// Grid-locking's pure snap math — the authoring-side float core shared by world-sculpt and creator (see the
/// grid-locking proposal §1). Every function is pure (no state, no allocation) and takes an explicit
/// <see cref="SnapConfig"/> so callers stay declarative. This is HOST-SIDE PRESENTATION math: it never enters the
/// deterministic simulation or a saved wire format (it only changes the DISTRIBUTION of the plain
/// <c>Position</c>/<c>YawDegrees</c> floats already written).
/// </summary>
internal static class GridSnap {
    // The fraction a node must be departed before the magnetize band releases to the next node (the resolved F3 rule).
    private const float ReleaseBandFraction = 0.6f;
    // How near a value must be to a lattice multiple to count as "resting on a node" for the release band.
    private const float OnNodeEpsilon = 1.0e-4f;
    // The near-quaternion-equality threshold used to dedupe the coarse-orientation candidate sets.
    private const float OrientationDedupeDot = 0.9999f;

    /// <summary>Snaps a world-space position to the world lattice (origin at world 0), per axis: a pitch component
    /// &lt;= 0 leaves that axis free.</summary>
    /// <param name="p">The candidate position.</param>
    /// <param name="pitch">The per-axis lattice pitch.</param>
    /// <returns>The snapped position.</returns>
    public static Vector3 SnapToWorldLattice(Vector3 p, Vector3 pitch) =>
        new(
            x: ((pitch.X > 0f) ? (MathF.Round(x: (p.X / pitch.X)) * pitch.X) : p.X),
            y: ((pitch.Y > 0f) ? (MathF.Round(x: (p.Y / pitch.Y)) * pitch.Y) : p.Y),
            z: ((pitch.Z > 0f) ? (MathF.Round(x: (p.Z / pitch.Z)) * pitch.Z) : p.Z)
        );

    /// <summary>The full position snap (the proposal's combined case §1f). With no reference it is the pure
    /// world-lattice snap (§1a) with the magnetize release band; with a reference it works in reference-local space,
    /// competing the object lattice (§1b) against the true face-to-face / inner-flush / center candidates (§1c) per
    /// axis, face-priority winning inside the capture radius. Returns the intent untouched when snapping is off.</summary>
    /// <param name="intent">The un-snapped integrated cursor (the retained pre-snap intent — the resolved F3
    /// magnetize-while-dragging source of truth).</param>
    /// <param name="config">The snap configuration.</param>
    /// <param name="candidateLocalHalfExtents">The MOVED shape's half-extents along the REFERENCE frame's axes (for
    /// true face-to-face butt-join); unused when there is no reference. Pass <see cref="Vector3.Zero"/> for
    /// center-on-face.</param>
    /// <param name="previousSnapped">The last committed (snapped) value, for the release-band hysteresis; pass
    /// <paramref name="intent"/> to seed (first frame).</param>
    /// <returns>The snapped position.</returns>
    public static Vector3 Apply(Vector3 intent, in SnapConfig config, Vector3 candidateLocalHalfExtents, Vector3 previousSnapped) {
        if (!config.Enabled) {
            return intent;
        }

        if (config.Reference is { } reference) {
            var inverse = Quaternion.Inverse(value: reference.Frame);
            var local = Vector3.Transform(value: (intent - reference.Origin), rotation: inverse);
            var previousLocal = Vector3.Transform(value: (previousSnapped - reference.Origin), rotation: inverse);
            var snappedLocal = new Vector3(
                x: SnapAxisCombined(value: local.X, previousValue: previousLocal.X, pitch: reference.Pitch.X, halfExtent: reference.LocalHalfExtents.X, candidateHalfExtent: candidateLocalHalfExtents.X, faceRadius: reference.FaceRadius),
                y: SnapAxisCombined(value: local.Y, previousValue: previousLocal.Y, pitch: reference.Pitch.Y, halfExtent: reference.LocalHalfExtents.Y, candidateHalfExtent: candidateLocalHalfExtents.Y, faceRadius: reference.FaceRadius),
                z: SnapAxisCombined(value: local.Z, previousValue: previousLocal.Z, pitch: reference.Pitch.Z, halfExtent: reference.LocalHalfExtents.Z, candidateHalfExtent: candidateLocalHalfExtents.Z, faceRadius: reference.FaceRadius)
            );

            return (reference.Origin + Vector3.Transform(value: snappedLocal, rotation: reference.Frame));
        }

        return new Vector3(
            x: SnapAxisBand(value: intent.X, previousValue: previousSnapped.X, pitch: config.Pitch.X),
            y: SnapAxisBand(value: intent.Y, previousValue: previousSnapped.Y, pitch: config.Pitch.Y),
            z: SnapAxisBand(value: intent.Z, previousValue: previousSnapped.Z, pitch: config.Pitch.Z)
        );
    }

    /// <summary>Snaps a scalar yaw (degrees) to the rotation increment — world-sculpt's yaw-only path (§1d). Off
    /// returns the input.</summary>
    /// <param name="yawDegrees">The yaw, degrees.</param>
    /// <param name="mode">The rotation increment.</param>
    /// <returns>The snapped yaw, degrees.</returns>
    public static float SnapYawDegrees(float yawDegrees, RotationSnap mode) {
        var step = IncrementDegrees(mode: mode);

        return ((step > 0f) ? (MathF.Round(x: (yawDegrees / step)) * step) : yawDegrees);
    }

    /// <summary>Snaps a full orientation to the nearest coarse-orientation candidate (creator's path §1d, the
    /// resolved F2 rule): the nearest element of the 24-element octahedral group (Deg90) or the richer 45°-granular
    /// set (Deg45) by geodesic distance — argmax |dot(q, candidate)|, robust against quaternion double-cover. Off
    /// returns the input.</summary>
    /// <param name="orientation">The orientation to snap.</param>
    /// <param name="mode">The rotation increment.</param>
    /// <returns>The snapped orientation (normalized).</returns>
    public static Quaternion SnapRotation(Quaternion orientation, RotationSnap mode) {
        var candidates = mode switch {
            RotationSnap.Deg90 => s_octahedralGroup,
            RotationSnap.Deg45 => s_deg45Set,
            _ => null,
        };

        if (candidates is null) {
            return orientation;
        }

        var normalized = Quaternion.Normalize(value: orientation);
        var best = candidates[0];
        var bestDot = -1f;

        foreach (var candidate in candidates) {
            var dot = MathF.Abs(x: Quaternion.Dot(quaternion1: normalized, quaternion2: candidate));

            if (dot > bestDot) {
                bestDot = dot;
                best = candidate;
            }
        }

        return best;
    }

    // One axis of the reference-space combined pick (§1f): the face/center candidates (§1c) compete with the object
    // lattice (§1b); face priority inside the capture radius, else the nearest lattice node, else free.
    private static float SnapAxisCombined(float value, float previousValue, float pitch, float halfExtent, float candidateHalfExtent, float faceRadius) {
        var faceCandidate = NearestFaceCandidate(value: value, halfExtent: halfExtent, candidateHalfExtent: candidateHalfExtent);

        if (MathF.Abs(x: (value - faceCandidate)) <= faceRadius) {
            return faceCandidate;
        }

        if (pitch > 0f) {
            return SnapAxisBand(value: value, previousValue: previousValue, pitch: pitch);
        }

        return value;
    }

    // The nearest of the true face-to-face / inner-flush / center candidate set (§1c, resolved F1). The moved shape's
    // CENTER lands so its near FACE meets the reference face: outer butt-join at ±(h + candH), inner-flush at
    // ±(h - candH), center-align at 0. candH == 0 collapses to the center-on-face set {-h, 0, +h}.
    private static float NearestFaceCandidate(float value, float halfExtent, float candidateHalfExtent) {
        Span<float> candidates = [
            -(halfExtent + candidateHalfExtent),
            -(halfExtent - candidateHalfExtent),
            0f,
            (halfExtent - candidateHalfExtent),
            (halfExtent + candidateHalfExtent),
        ];
        var best = candidates[0];
        var bestDistance = MathF.Abs(x: (value - best));

        foreach (var candidate in candidates) {
            var distance = MathF.Abs(x: (value - candidate));

            if (distance < bestDistance) {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    // One axis of magnetize-while-dragging with the 0.6*pitch release band (§1g, resolved F3): once resting on a node
    // the intent must move past 0.6*pitch before re-snapping, so stick jitter never buzzes between two nodes.
    private static float SnapAxisBand(float value, float previousValue, float pitch) {
        if (pitch <= 0f) {
            return value;
        }

        var nearest = (MathF.Round(x: (value / pitch)) * pitch);

        // A NaN previous means "no magnetize history" — the path-independent console SET; snap to the nearest node
        // with no release band. Only a valid, on-node previous engages the drag hysteresis.
        if (float.IsNaN(f: previousValue)) {
            return nearest;
        }

        var previousOnNode = (MathF.Abs(x: ((previousValue / pitch) - MathF.Round(x: (previousValue / pitch)))) < OnNodeEpsilon);

        if (previousOnNode && (MathF.Abs(x: (value - previousValue)) <= (ReleaseBandFraction * pitch))) {
            return previousValue;
        }

        return nearest;
    }
    private static float IncrementDegrees(RotationSnap mode) =>
        mode switch {
            RotationSnap.Deg90 => 90f,
            RotationSnap.Deg45 => 45f,
            _ => 0f,
        };

    // The 24-element proper octahedral (cube) rotation group and the richer 45°-granular candidate set, precomputed
    // once. Both are generated by composing coordinate-axis rotations at the increment and deduplicating by
    // near-quaternion-equality; used ONLY as a nearest-by-dot candidate pool, so the Euler generation carries no
    // gimbal ambiguity into the result.
    private static readonly Quaternion[] s_octahedralGroup = BuildOrientationSet(stepDegrees: 90f);
    private static readonly Quaternion[] s_deg45Set = BuildOrientationSet(stepDegrees: 45f);

    private static Quaternion[] BuildOrientationSet(float stepDegrees) {
        var stepCount = (int)MathF.Round(x: (360f / stepDegrees));
        var unique = new List<Quaternion>();

        for (var xi = 0; (xi < stepCount); xi++) {
            for (var yi = 0; (yi < stepCount); yi++) {
                for (var zi = 0; (zi < stepCount); zi++) {
                    var candidate = Quaternion.Normalize(value: Quaternion.CreateFromYawPitchRoll(
                        pitch: float.DegreesToRadians(degrees: (xi * stepDegrees)),
                        roll: float.DegreesToRadians(degrees: (zi * stepDegrees)),
                        yaw: float.DegreesToRadians(degrees: (yi * stepDegrees))
                    ));
                    var duplicate = false;

                    foreach (var existing in unique) {
                        if (MathF.Abs(x: Quaternion.Dot(quaternion1: candidate, quaternion2: existing)) > OrientationDedupeDot) {
                            duplicate = true;

                            break;
                        }
                    }

                    if (!duplicate) {
                        unique.Add(item: candidate);
                    }
                }
            }
        }

        return [.. unique];
    }
}
