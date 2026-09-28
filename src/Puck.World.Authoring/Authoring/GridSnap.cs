using System.Collections.Concurrent;
using System.Numerics;

namespace Puck.World.Authoring;

/// <summary>A captured align-to-shape reference — the frozen guide a moved shape snaps against. Snapshotted at
/// capture time so a later delete/move of the source shape never disturbs the guide. All fields are authoring-side
/// floats.</summary>
/// <param name="Origin">The reference frame's origin, world space.</param>
/// <param name="Frame">The reference frame's orientation (a pure yaw quaternion for a planar reference; the full
/// shape rotation for an oriented one).</param>
/// <param name="Pitch">The per-axis object-lattice pitch, in reference-local space (a component &lt;= 0 disables the
/// lattice on that axis; the face candidates still speak).</param>
/// <param name="LocalHalfExtents">The reference's half-extents along its own axes — the butt-join face planes sit at
/// <c>±LocalHalfExtents.c</c> per axis.</param>
/// <param name="FaceRadius">The face-snap capture radius (reference-local units): within it, a face/center candidate
/// wins over a lattice node.</param>
public readonly record struct SnapReference(
    Vector3 Origin,
    Quaternion Frame,
    Vector3 Pitch,
    Vector3 LocalHalfExtents,
    float FaceRadius
);
/// <summary>The snap configuration a caller threads through <see cref="GridSnap"/> — pure declarative state, no
/// behavior. Session-only: never persisted to a document.</summary>
/// <param name="Enabled">Whether snapping is active at all (off = every function returns its input untouched).</param>
/// <param name="Pitch">The world-lattice per-axis pitch (origin at world 0). A component &lt;= 0 = free on that axis
/// (e.g. <c>Pitch.Y = 0</c> leaves vertical placement floor-rest / unsnapped).</param>
/// <param name="AngleStepDegrees">The rotation-snap increment, in degrees; zero or less leaves rotation free.</param>
/// <param name="Reference">The align-to-shape reference, or null for world-lattice-only.</param>
public readonly record struct SnapConfig(
    bool Enabled,
    Vector3 Pitch,
    float AngleStepDegrees,
    SnapReference? Reference
) {
    /// <summary>Gets the disabled configuration: snapping off, zero pitch on every axis, no rotation snap, no
    /// reference.</summary>
    public static SnapConfig Disabled => default;
}
/// <summary>
/// Grid-locking's pure snap math — the authoring-side float core shared by every editing surface. Every function
/// is pure (no state, no allocation) and takes an explicit <see cref="SnapConfig"/> so callers stay declarative. This
/// is host-side presentation math: it never enters the deterministic simulation or a saved wire format (it only
/// changes the distribution of the plain <c>Position</c>/<c>YawDegrees</c> floats already written).
/// </summary>
public static class GridSnap {
    // How near a value must be to a lattice multiple to count as "resting on a node" for the release band.
    private const float OnNodeEpsilon = 1.0e-4f;
    // The near-quaternion-equality threshold used to dedupe the coarse-orientation candidate sets.
    private const float OrientationDedupeDot = 0.9999f;
    // The fraction a node must be departed before the magnetize band releases to the next node.
    private const float ReleaseBandFraction = 0.6f;

    private static Quaternion[] BuildOrientationSet(float stepDegrees) {
        var stepCount = ((int)MathF.Round(x: (360f / stepDegrees)));
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
                        if (MathF.Abs(x: Quaternion.Dot(
                            quaternion1: candidate,
                            quaternion2: existing
                        )) > OrientationDedupeDot) {
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
    // The whole turn a coarse-orientation step divides into, or zero when it does not divide a turn exactly.
    private static int StepsPerTurn(float stepDegrees) {
        var steps = MathF.Round(x: (360f / stepDegrees));

        return ((MathF.Abs(x: ((steps * stepDegrees) - 360f)) < 1.0e-3f)
            ? ((int)steps)
            : 0);
    }
    // The nearest of the true face-to-face / inner-flush / center candidate set. The moved shape's
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
    // One axis of magnetize-while-dragging with the 0.6*pitch release band: once resting on a node the intent must
    // move past 0.6*pitch before re-snapping, so stick jitter never buzzes between two nodes.
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

        if (
            previousOnNode &&
            (MathF.Abs(x: (value - previousValue)) <= (ReleaseBandFraction * pitch))
        ) {
            return previousValue;
        }

        return nearest;
    }
    // One axis of the reference-space combined pick: the face/center candidates compete with the object lattice;
    // face priority inside the capture radius, else the nearest lattice node, else free.
    private static float SnapAxisCombined(float value, float previousValue, float pitch, float halfExtent, float candidateHalfExtent, float faceRadius) {
        var faceCandidate = NearestFaceCandidate(
            candidateHalfExtent: candidateHalfExtent,
            halfExtent: halfExtent,
            value: value
        );

        if (MathF.Abs(x: (value - faceCandidate)) <= faceRadius) {
            return faceCandidate;
        }

        if (pitch > 0f) {
            return SnapAxisBand(
                pitch: pitch,
                previousValue: previousValue,
                value: value
            );
        }

        return value;
    }

    /// <summary>The full position snap. With no reference it is the pure world-lattice snap with the magnetize
    /// release band; with a reference it works in reference-local space, competing the object lattice against the
    /// true face-to-face / inner-flush / center candidates per axis, face-priority winning inside the capture
    /// radius. Returns the intent untouched when snapping is off.</summary>
    /// <param name="intent">The un-snapped integrated cursor (the retained pre-snap intent — the
    /// magnetize-while-dragging source of truth).</param>
    /// <param name="config">The snap configuration.</param>
    /// <param name="candidateLocalHalfExtents">The moved shape's half-extents along the reference frame's axes (for
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
            var local = Vector3.Transform(
                value: (intent - reference.Origin),
                rotation: inverse
            );
            var previousLocal = Vector3.Transform(
                value: (previousSnapped - reference.Origin),
                rotation: inverse
            );
            var snappedLocal = new Vector3(
                x: SnapAxisCombined(
                    value: local.X,
                    previousValue: previousLocal.X,
                    pitch: reference.Pitch.X,
                    halfExtent: reference.LocalHalfExtents.X,
                    candidateHalfExtent: candidateLocalHalfExtents.X,
                    faceRadius: reference.FaceRadius
                ),
                y: SnapAxisCombined(
                    value: local.Y,
                    previousValue: previousLocal.Y,
                    pitch: reference.Pitch.Y,
                    halfExtent: reference.LocalHalfExtents.Y,
                    candidateHalfExtent: candidateLocalHalfExtents.Y,
                    faceRadius: reference.FaceRadius
                ),
                z: SnapAxisCombined(
                    value: local.Z,
                    previousValue: previousLocal.Z,
                    pitch: reference.Pitch.Z,
                    halfExtent: reference.LocalHalfExtents.Z,
                    candidateHalfExtent: candidateLocalHalfExtents.Z,
                    faceRadius: reference.FaceRadius
                )
            );

            return (reference.Origin + Vector3.Transform(
                value: snappedLocal,
                rotation: reference.Frame
            ));
        }

        return new Vector3(
            x: SnapAxisBand(
                value: intent.X,
                previousValue: previousSnapped.X,
                pitch: config.Pitch.X
            ),
            y: SnapAxisBand(
                value: intent.Y,
                previousValue: previousSnapped.Y,
                pitch: config.Pitch.Y
            ),
            z: SnapAxisBand(
                value: intent.Z,
                previousValue: previousSnapped.Z,
                pitch: config.Pitch.Z
            )
        );
    }
    /// <summary>Snaps a full orientation to the nearest orientation composed of whole steps about the coordinate axes,
    /// by geodesic distance (argmax |dot(q, candidate)|, robust against the quaternion double cover): at 90° the
    /// 24-element octahedral group. A step of zero or less returns the input.</summary>
    /// <param name="orientation">The orientation to snap.</param>
    /// <param name="stepDegrees">The step, in degrees: at least <see cref="MinOrientationStepDegrees"/> and dividing a
    /// whole turn exactly.</param>
    /// <returns>The snapped orientation (normalized).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepDegrees"/> is positive and under
    /// <see cref="MinOrientationStepDegrees"/>, or does not divide a whole turn.</exception>
    public static Quaternion SnapRotation(Quaternion orientation, float stepDegrees) {
        if (!(stepDegrees > 0f)) {
            return orientation;
        }

        var steps = StepsPerTurn(stepDegrees: stepDegrees);

        if (
            (stepDegrees < MinOrientationStepDegrees) ||
            (steps == 0)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: stepDegrees,
                message: $"A full-orientation snap takes a step of at least {MinOrientationStepDegrees} degrees that divides a whole turn.",
                paramName: nameof(stepDegrees)
            );
        }

        var candidates = OrientationSets.GetOrAdd(
            key: steps,
            valueFactory: static count => BuildOrientationSet(stepDegrees: (360f / count))
        );
        var normalized = Quaternion.Normalize(value: orientation);
        var best = candidates[0];
        var bestDot = -1f;

        foreach (var candidate in candidates) {
            var dot = MathF.Abs(x: Quaternion.Dot(
                quaternion1: normalized,
                quaternion2: candidate
            ));

            if (dot > bestDot) {
                bestDot = dot;
                best = candidate;
            }
        }

        return best;
    }
    /// <summary>Snaps a world-space position to the world lattice (origin at world 0), per axis: a pitch component
    /// &lt;= 0 leaves that axis free.</summary>
    /// <param name="p">The candidate position.</param>
    /// <param name="pitch">The per-axis lattice pitch.</param>
    /// <returns>The snapped position.</returns>
    public static Vector3 SnapToWorldLattice(Vector3 p, Vector3 pitch) =>
        new(
            x: ((pitch.X > 0f)
            ? (MathF.Round(x: (p.X / pitch.X)) * pitch.X)
            : p.X),
            y: ((pitch.Y > 0f)
            ? (MathF.Round(x: (p.Y / pitch.Y)) * pitch.Y)
            : p.Y),
            z: ((pitch.Z > 0f)
            ? (MathF.Round(x: (p.Z / pitch.Z)) * pitch.Z)
            : p.Z)
        );
    /// <summary>Snaps a yaw to the nearest whole multiple of the step. A step of zero or less returns the input.</summary>
    /// <param name="yawDegrees">The yaw, in degrees.</param>
    /// <param name="stepDegrees">The step, in degrees.</param>
    /// <returns>The snapped yaw, in degrees.</returns>
    public static float SnapYawDegrees(float yawDegrees, float stepDegrees) => ((stepDegrees > 0f)
        ? (MathF.Round(x: (yawDegrees / stepDegrees)) * stepDegrees)
        : yawDegrees
    );

    /// <summary>The smallest step a full-orientation snap takes, in degrees; a finer one would build a candidate set
    /// too large to search.</summary>
    public const float MinOrientationStepDegrees = 30f;

    // The coarse-orientation candidate sets, one per whole-turn step count, built the first time a step asks for one.
    // Each composes coordinate-axis rotations at the step and deduplicates by near-quaternion-equality, and serves only
    // as a nearest-by-dot pool, so the Euler generation carries no gimbal ambiguity into the result.
    private static readonly ConcurrentDictionary<int, Quaternion[]> OrientationSets = new();
}
