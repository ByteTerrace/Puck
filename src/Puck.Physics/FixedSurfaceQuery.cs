using Puck.Maths;

namespace Puck.Physics;

/// <summary>Which of <see cref="FixedSurfaceQuery"/>'s two collider spans supplied a candidate — the same
/// static/dynamic split <see cref="FixedStaticContactSolver.Resolve"/> already takes two spans for, so a caller
/// holding a set it compiled once beside one it rebuilds per tick reads back which one answered.</summary>
public enum FixedSurfaceColliderSource : byte {
    /// <summary>The first span (<c>colliders</c>) — compiled once.</summary>
    Static,
    /// <summary>The second span (<c>dynamicColliders</c>) — recomputed by the caller every tick.</summary>
    Dynamic,
}

/// <summary>One surface-attach candidate: the nearest analytic surface point within the caller's reach, its
/// outward unit normal there, which collider owns it, and the probe's distance to it.</summary>
/// <param name="Point">The nearest point on the owning collider's surface.</param>
/// <param name="Normal">The unit outward surface normal at <paramref name="Point"/> (see
/// <see cref="FixedSurfaceQuery"/> for the per-primitive exactness this carries).</param>
/// <param name="Distance">The non-negative Euclidean distance from the probe (or, for
/// <see cref="FixedSurfaceQuery.TryNearestDirected"/>, the ray origin) to <paramref name="Point"/>.</param>
/// <param name="Source">Which span <paramref name="ColliderIndex"/> indexes into.</param>
/// <param name="ColliderIndex">The candidate's index within its <paramref name="Source"/> span — half of the
/// deterministic tie-break identity two equidistant (or, for the directed query, equally-scored) colliders are
/// resolved by; see <see cref="FixedSurfaceQuery"/>.</param>
public readonly record struct FixedSurfaceAttachCandidate(
    FixedVector3 Point,
    FixedVector3 Normal,
    FixedQ4816 Distance,
    FixedSurfaceColliderSource Source,
    int ColliderIndex
);

/// <summary>
/// The nearest-surface-point primitive climbing (surface attach/conform) and grappling (tether anchor selection)
/// both resolve against — one deterministic query over the same analytic collider vocabulary
/// <see cref="FixedStaticContactSolver"/> depenetrates a body out of: <see cref="FixedStaticColliderKind.Sphere"/>,
/// <see cref="FixedStaticColliderKind.AxisAlignedBox"/>, and <see cref="FixedStaticColliderKind.HalfSpace"/>. A
/// later slice reads a candidate's <see cref="FixedSurfaceAttachCandidate.Point"/> as the anchor and
/// <see cref="FixedSurfaceAttachCandidate.Normal"/> as the surface-tangent-plane basis; nothing here decides grip,
/// rope length, or reach — every distance and angle is caller-supplied, never a constant baked in.
/// </summary>
/// <remarks>
/// <para><b>Exactness per collider kind.</b> A box's nearest point is an exact componentwise clamp — pure add/sub/
/// compare, no rounding at all — for every face, edge, and corner case. Its normal is exact too on a face (the
/// probe is outside on exactly one axis, so the normal is that axis's unit vector by construction); on an edge or
/// corner (outside on two or three axes) the normal is <see cref="FixedVector3.Normalize"/>'s correctly-rounded
/// unit direction — the nearest representable Q16 vector to the true direction, each component within one
/// <see cref="FixedQ4816.Epsilon"/>, deterministic and bit-identical for the same input (see
/// <see cref="FixedVector3.Normalize"/>'s own remarks and the single corrective increment
/// <c>FixedVectorMath.RootOfSquaredSum</c>/<c>TryNormalizeWithMagnitude</c> apply to an exact integer square root —
/// not an unbounded iteration). A half-space's point and normal are exact by construction: the normal is the
/// collider's own already-unit <see cref="FixedStaticCollider.Extent"/> (never recomputed), and the point is one
/// projection — a dot product and one scaled subtract, each rounding once the way every <see cref="FixedQ4816"/>
/// operation does. A sphere's point and normal use the same correctly-rounded <see cref="FixedVector3.Normalize"/>
/// as a box's edge/corner case, so it carries the same one-<see cref="FixedQ4816.Epsilon"/>-per-component bound; a
/// probe exactly at a sphere's center has no defined gradient, so that degenerate case reports <c>+Y</c> rather
/// than an arbitrary or discontinuous direction.</para>
/// <para><b>Tie-breaking.</b> <see cref="TryNearest"/> ranks candidates by ascending <c>Distance</c>; ties (and,
/// for <see cref="TryNearestDirected"/>, ties on its two-key score — see that method) fall back to a total order
/// over <c>(Source, ColliderIndex)</c>: <see cref="FixedSurfaceColliderSource.Static"/> before
/// <see cref="FixedSurfaceColliderSource.Dynamic"/>, then ascending index within that span. The rule depends on
/// nothing but the two spans' own contents and order, so the same two spans — rebuilt from the same colliders in
/// the same order any number of times, in any order relative to each other — always resolve the same winner.</para>
/// <para><b>Reach.</b> A candidate exactly at the caller's reach bound is included: <c>Distance &lt;= reach</c>, a
/// closed interval, not <c>&lt;</c>.</para>
/// <para>Zero allocation: every method is at most two <c>for</c> loops over the caller's own spans, updating one
/// by-value running best; nothing here allocates, boxes, or retains a reference into either span.</para>
/// </remarks>
public static class FixedSurfaceQuery {
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

    private static FixedQ4816 Sign(FixedQ4816 value) => ((value < FixedQ4816.Zero)
        ? -FixedQ4816.One
        : FixedQ4816.One
    );
    // Exact for every face, edge, and corner: a componentwise clamp always lands `clamped` on the box's boundary
    // whenever the probe is outside on at least one axis (the interior case below is the only one where clamping
    // alone is not enough, because clamping a point already inside the range changes nothing). The normal is exact
    // on a face (delta nonzero on exactly one axis) and Normalize()-correct on an edge/corner (delta nonzero on two
    // or three) — see the type's remarks.
    private static (FixedVector3 Point, FixedVector3 Normal) NearestOnBox(FixedVector3 center, FixedVector3 halfExtents, FixedVector3 probe) {
        var local = (probe - center);
        var clamped = new FixedVector3(
            X: FixedQ4816.Clamp(
                value: local.X,
                minimum: -halfExtents.X,
                maximum: halfExtents.X
            ),
            Y: FixedQ4816.Clamp(
                value: local.Y,
                minimum: -halfExtents.Y,
                maximum: halfExtents.Y
            ),
            Z: FixedQ4816.Clamp(
                value: local.Z,
                minimum: -halfExtents.Z,
                maximum: halfExtents.Z
            )
        );
        var delta = (local - clamped);
        var clampedX = (delta.X != FixedQ4816.Zero);
        var clampedY = (delta.Y != FixedQ4816.Zero);
        var clampedZ = (delta.Z != FixedQ4816.Zero);

        if (clampedX || clampedY || clampedZ) {
            var point = (center + clamped);

            // Face case: the probe is outside on exactly one axis, so the outward normal is that axis's unit
            // vector — no square root, no rounding.
            if (clampedX && !clampedY && !clampedZ) {
                return (Point: point, Normal: (UnitX * Sign(value: delta.X)));
            }
            if (clampedY && !clampedX && !clampedZ) {
                return (Point: point, Normal: (UnitY * Sign(value: delta.Y)));
            }
            if (clampedZ && !clampedX && !clampedY) {
                return (Point: point, Normal: (UnitZ * Sign(value: delta.Z)));
            }

            // Edge or corner: `delta` already points from the nearest boundary point straight at the probe, which
            // is the analytically correct outward gradient there too — Normalize() just rescales it to unit length.
            return (Point: point, Normal: delta.Normalize());
        }

        // Interior (including exactly ON a face, where the gap on that axis is zero): no axis needed clamping, so
        // project out through the nearest exit face — the axis with the smallest gap to its half-extent. Ties break
        // X, then Y, then Z, the same order TrySpherePush's interior branch already uses for this collider kind.
        var gapX = (halfExtents.X - FixedQ4816.Abs(value: local.X));
        var gapY = (halfExtents.Y - FixedQ4816.Abs(value: local.Y));
        var gapZ = (halfExtents.Z - FixedQ4816.Abs(value: local.Z));

        if ((gapX <= gapY) && (gapX <= gapZ)) {
            var axisSign = Sign(value: local.X);

            return (Point: (center + new FixedVector3(X: (halfExtents.X * axisSign), Y: local.Y, Z: local.Z)), Normal: (UnitX * axisSign));
        }

        if (gapY <= gapZ) {
            var axisSign = Sign(value: local.Y);

            return (Point: (center + new FixedVector3(X: local.X, Y: (halfExtents.Y * axisSign), Z: local.Z)), Normal: (UnitY * axisSign));
        }

        {
            var axisSign = Sign(value: local.Z);

            return (Point: (center + new FixedVector3(X: local.X, Y: local.Y, Z: (halfExtents.Z * axisSign))), Normal: (UnitZ * axisSign));
        }
    }
    private static (FixedVector3 Point, FixedVector3 Normal) NearestOnHalfSpace(FixedVector3 boundaryPoint, FixedVector3 normal, FixedVector3 probe) {
        // A plane projection: exact by construction, the same single-rounded dot/multiply/subtract every other
        // fixed-point vector operation already carries — no iteration, no square root.
        var signedDistance = FixedVector3.Dot(
            left: (probe - boundaryPoint),
            right: normal
        );
        var point = (probe - (normal * signedDistance));

        return (Point: point, Normal: normal);
    }
    private static (FixedVector3 Point, FixedVector3 Normal) NearestOnSphere(FixedVector3 center, FixedQ4816 radius, FixedVector3 probe) {
        var delta = (probe - center);
        var normal = delta.Normalize();

        // The center itself has no defined gradient; report the canonical up rather than an arbitrary or
        // discontinuous direction (see the type's remarks).
        if (normal == FixedVector3.Zero) {
            normal = UnitY;
        }

        return (Point: (center + (normal * radius)), Normal: normal);
    }
    private static (FixedVector3 Point, FixedVector3 Normal) NearestOnCollider(in FixedStaticCollider collider, FixedVector3 probe) => collider.Kind switch {
        FixedStaticColliderKind.Sphere => NearestOnSphere(
        center: collider.Center,
        probe: probe,
        radius: collider.Extent.X
    ),
        FixedStaticColliderKind.AxisAlignedBox => NearestOnBox(
        center: collider.Center,
        halfExtents: collider.Extent,
        probe: probe
    ),
        FixedStaticColliderKind.HalfSpace => NearestOnHalfSpace(
        boundaryPoint: collider.Center,
        normal: collider.Extent,
        probe: probe
    ),
        _ => throw new InvalidOperationException(message: $"Unknown collider kind {collider.Kind}."),
    };
    private static bool IsCloser(FixedQ4816 distance, FixedSurfaceColliderSource source, int index, in FixedSurfaceAttachCandidate best) {
        if (distance != best.Distance) {
            return (distance < best.Distance);
        }
        if (source != best.Source) {
            return (source < best.Source);
        }

        return (index < best.ColliderIndex);
    }
    private static bool IsBetterDirected(FixedQ4816 cosine, FixedQ4816 distance, FixedSurfaceColliderSource source, int index, FixedQ4816 bestCosine, in FixedSurfaceAttachCandidate best) {
        // Angular deviation is the primary key: a larger cosine is a smaller angle, so a strictly larger cosine
        // always wins regardless of distance. Distance is the secondary key, then the same (Source, ColliderIndex)
        // total order TryNearest uses.
        if (cosine != bestCosine) {
            return (cosine > bestCosine);
        }
        if (distance != best.Distance) {
            return (distance < best.Distance);
        }
        if (source != best.Source) {
            return (source < best.Source);
        }

        return (index < best.ColliderIndex);
    }
    private static void ConsiderSpan(
        ReadOnlySpan<FixedStaticCollider> colliders,
        FixedSurfaceColliderSource source,
        FixedVector3 probe,
        FixedQ4816 reach,
        ref bool found,
        ref FixedSurfaceAttachCandidate best
    ) {
        for (var index = 0; (index < colliders.Length); index++) {
            var (point, normal) = NearestOnCollider(
                collider: in colliders[index],
                probe: probe
            );
            var distance = (probe - point).Length;

            if (
                (distance <= reach) &&
                (!found || IsCloser(
                distance: distance,
                index: index,
                source: source,
                best: in best
            ))
            ) {
                found = true;
                best = new FixedSurfaceAttachCandidate(
                    ColliderIndex: index,
                    Distance: distance,
                    Normal: normal,
                    Point: point,
                    Source: source
                );
            }
        }
    }
    private static void ConsiderSpanDirected(
        ReadOnlySpan<FixedStaticCollider> colliders,
        FixedSurfaceColliderSource source,
        FixedVector3 origin,
        FixedVector3 unitDirection,
        FixedQ4816 maxDistance,
        FixedQ4816 cosineHalfAngle,
        ref bool found,
        ref FixedQ4816 bestCosine,
        ref FixedSurfaceAttachCandidate best
    ) {
        for (var index = 0; (index < colliders.Length); index++) {
            var (point, normal) = NearestOnCollider(
                collider: in colliders[index],
                probe: origin
            );
            var delta = (point - origin);
            var distance = delta.Length;

            if (distance > maxDistance) {
                continue;
            }

            // A candidate already at the origin has no defined bearing; it is maximally aligned (angle zero) rather
            // than excluded by the cone test or scored against an undefined direction.
            var cosine = ((distance == FixedQ4816.Zero)
                ? FixedQ4816.One
                : FixedVector3.Dot(
                    left: delta.Normalize(),
                    right: unitDirection
                )
            );

            if (
                (cosine >= cosineHalfAngle) &&
                (!found || IsBetterDirected(
                bestCosine: bestCosine,
                best: in best,
                cosine: cosine,
                distance: distance,
                index: index,
                source: source
            ))
            ) {
                found = true;
                bestCosine = cosine;
                best = new FixedSurfaceAttachCandidate(
                    ColliderIndex: index,
                    Distance: distance,
                    Normal: normal,
                    Point: point,
                    Source: source
                );
            }
        }
    }

    /// <summary>Finds the nearest analytic surface point to a probe within a caller-supplied reach — the surface-
    /// attach query climbing resolves an anchor against.</summary>
    /// <param name="colliders">The colliders compiled once (<see cref="FixedSurfaceColliderSource.Static"/>).</param>
    /// <param name="dynamicColliders">The colliders the caller recomputes per tick
    /// (<see cref="FixedSurfaceColliderSource.Dynamic"/>), or empty.</param>
    /// <param name="probe">The world-space point to search from — a hand, a grapple muzzle, a foot.</param>
    /// <param name="reach">The non-negative maximum distance a result may sit from <paramref name="probe"/>; a
    /// result exactly at <paramref name="reach"/> is included. Always caller-supplied — never a constant here.</param>
    /// <param name="candidate">The nearest surface point within reach, or <see langword="default"/> when none
    /// exists.</param>
    /// <returns><see langword="true"/> when a collider surface lies within <paramref name="reach"/> of
    /// <paramref name="probe"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reach"/> is negative.</exception>
    public static bool TryNearest(
        ReadOnlySpan<FixedStaticCollider> colliders,
        ReadOnlySpan<FixedStaticCollider> dynamicColliders,
        in FixedVector3 probe,
        FixedQ4816 reach,
        out FixedSurfaceAttachCandidate candidate
    ) {
        ArgumentOutOfRangeException.ThrowIfNegative(
            paramName: nameof(reach),
            value: reach.Value
        );

        var found = false;

        candidate = default;

        ConsiderSpan(
            best: ref candidate,
            colliders: colliders,
            found: ref found,
            probe: probe,
            reach: reach,
            source: FixedSurfaceColliderSource.Static
        );
        ConsiderSpan(
            best: ref candidate,
            colliders: dynamicColliders,
            found: ref found,
            probe: probe,
            reach: reach,
            source: FixedSurfaceColliderSource.Dynamic
        );

        return found;
    }
    /// <summary>Finds the best grapple anchor candidate along an aim direction — a soft-lock cone rather than an
    /// exact ray cast: every collider's own <see cref="TryNearest"/> point to <paramref name="origin"/> is a
    /// candidate, filtered to <paramref name="maxDistance"/> and to within <paramref name="assistHalfAngle"/> of
    /// <paramref name="direction"/>, then ranked by angular deviation first and distance second (a candidate closer
    /// to <paramref name="origin"/> but farther off-axis loses to one farther away but better aligned).</summary>
    /// <param name="colliders">The colliders compiled once (<see cref="FixedSurfaceColliderSource.Static"/>).</param>
    /// <param name="dynamicColliders">The colliders the caller recomputes per tick
    /// (<see cref="FixedSurfaceColliderSource.Dynamic"/>), or empty.</param>
    /// <param name="origin">The world-space aim origin — a grapple muzzle.</param>
    /// <param name="direction">The aim direction; need not be pre-normalized (renormalized once here, not
    /// per-candidate).</param>
    /// <param name="maxDistance">The non-negative maximum distance a candidate's surface point may sit from
    /// <paramref name="origin"/>; a candidate exactly at <paramref name="maxDistance"/> is included. Always
    /// caller-supplied.</param>
    /// <param name="assistHalfAngle">The non-negative half-angle, in radians, of the aim-assist cone around
    /// <paramref name="direction"/> a candidate's bearing must fall within. Always caller-supplied — a later slice
    /// derives it from a document field, never a constant here.</param>
    /// <param name="candidate">The best anchor candidate, or <see langword="default"/> when none qualifies.</param>
    /// <returns><see langword="true"/> when a qualifying candidate exists.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDistance"/> or
    /// <paramref name="assistHalfAngle"/> is negative.</exception>
    public static bool TryNearestDirected(
        ReadOnlySpan<FixedStaticCollider> colliders,
        ReadOnlySpan<FixedStaticCollider> dynamicColliders,
        in FixedVector3 origin,
        in FixedVector3 direction,
        FixedQ4816 maxDistance,
        FixedQ4816 assistHalfAngle,
        out FixedSurfaceAttachCandidate candidate
    ) {
        ArgumentOutOfRangeException.ThrowIfNegative(
            paramName: nameof(maxDistance),
            value: maxDistance.Value
        );
        ArgumentOutOfRangeException.ThrowIfNegative(
            paramName: nameof(assistHalfAngle),
            value: assistHalfAngle.Value
        );

        var unitDirection = direction.Normalize();
        // Comparing cosines instead of angles keeps every per-candidate comparison transcendental-free: cosine is
        // strictly decreasing on [0, pi], so ranking by descending cosine is exactly ranking by ascending angular
        // deviation. This is the only transcendental call the query makes, computed once per call rather than once
        // per candidate.
        var cosineHalfAngle = FixedQ4816.Cos(angle: assistHalfAngle);
        var found = false;
        var bestCosine = FixedQ4816.Zero;

        candidate = default;

        ConsiderSpanDirected(
            best: ref candidate,
            bestCosine: ref bestCosine,
            colliders: colliders,
            cosineHalfAngle: cosineHalfAngle,
            found: ref found,
            maxDistance: maxDistance,
            origin: origin,
            source: FixedSurfaceColliderSource.Static,
            unitDirection: unitDirection
        );
        ConsiderSpanDirected(
            best: ref candidate,
            bestCosine: ref bestCosine,
            colliders: dynamicColliders,
            cosineHalfAngle: cosineHalfAngle,
            found: ref found,
            maxDistance: maxDistance,
            origin: origin,
            source: FixedSurfaceColliderSource.Dynamic,
            unitDirection: unitDirection
        );

        return found;
    }
}
