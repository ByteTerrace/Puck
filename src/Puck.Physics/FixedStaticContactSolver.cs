using Puck.Maths;

namespace Puck.Physics;

/// <summary>The static colliders a sweep may reach from one region: every collider whose padded bounds come within
/// the contact skin of the region, in ascending index order. A body whose bounds stay inside the region needs no other
/// collider's exact query; once its bounds leave the region the sweep queries every remaining collider.</summary>
/// <param name="indices">The candidate collider indices, ascending and distinct.</param>
/// <param name="region">The region's world box.</param>
public readonly ref struct FixedColliderCandidates(ReadOnlySpan<int> indices, FixedAabb region) {
    /// <summary>Gets the candidate collider indices, ascending and distinct.</summary>
    public ReadOnlySpan<int> Indices { get; } = indices;
    /// <summary>Gets the region's world box.</summary>
    public FixedAabb Region { get; } = region;
    /// <summary>Gets whether a region was supplied; <see langword="false"/> for the default value, which queries
    /// every collider.</summary>
    public bool IsActive { get; } = true;

    /// <summary>Returns whether a world-axis box lies inside the region.</summary>
    /// <param name="box">The box, not empty.</param>
    /// <returns><see langword="true"/> when the box lies inside the region on every axis.</returns>
    public bool Contains(in FixedAabb box) => Region.Contains(inner: in box);
}
/// <summary>One static surface a body volume lies within a margin of: the collider it belongs to and its exact gap.</summary>
/// <param name="Source">The collider span: static or per-tick.</param>
/// <param name="ColliderIndex">The collider's index in that span.</param>
/// <param name="Normal">The unit surface normal, pointing from the surface toward the body.</param>
/// <param name="Separation">The signed gap between the volume and the surface; negative overlaps.</param>
public readonly record struct FixedSurfaceContact(FixedSurfaceColliderSource Source, int ColliderIndex, FixedVector3 Normal, FixedQ4816 Separation);
/// <summary>
/// Relaxes a body's compiled volumes out of a set of static colliders — the analytic half of
/// <see cref="IContactField"/>, written once over <see cref="FixedStaticCollider.TryGetPush"/>.
/// </summary>
/// <remarks>
/// <para>Single-pass-per-iteration relaxation: two adjacent colliders can push a body back and forth within one call.
/// Push order is collider order, so the caller's array order is part of the result.</para>
/// <para>The solve takes two collider spans and walks both inside each iteration rather than resolving one and then
/// the other, so a caller may hold a set it rebuilds per tick beside one it compiled once without changing how the
/// two interleave.</para>
/// </remarks>
/// <param name="ContactSkin">The signed skin kept between a body and every surface.</param>
/// <param name="GroundedThreshold">The <c>cos(maxSlope)</c> a contact normal's <c>+Y</c> alignment must clear to
/// ground the body.</param>
/// <param name="MaxIterations">The relaxation iteration count; values below one resolve one pass.</param>
public readonly record struct FixedStaticContactSolver(FixedQ4816 ContactSkin, FixedQ4816 GroundedThreshold, int MaxIterations) {
    // The touched edge's local clearance, not the apex of an entire tilted ramp. For rounded boxes, remove
    // the fillet offset before clamping to the inner edge and charge its full radius above that edge. This
    // still prevents repeated partial landings from climbing a kerb taller than the permitted step.
    private static FixedQ4816 EdgeTop(in FixedStaticCollider collider, in FixedVector3 up,
        in FixedVector3 witness, in FixedVector3 normal) {
        if (collider.Kind is FixedStaticColliderKind.AxisAlignedBox or FixedStaticColliderKind.OrientedBox or FixedStaticColliderKind.RoundedBox) {
            var point = (witness - (normal * collider.Radius));
            var local = ((collider.Kind == FixedStaticColliderKind.AxisAlignedBox) ? (point - collider.Center) : collider.Rotation.RotateInverse(vector: (point - collider.Center)));

            local = FixedVector3.Clamp(value: local, minimum: -collider.Extent, maximum: collider.Extent);
            point = (collider.Center + ((collider.Kind == FixedStaticColliderKind.AxisAlignedBox) ? local : collider.Rotation.Rotate(vector: local)));
            return (FixedVector3.Dot(left: point, right: up) + collider.Radius);
        }
        return FixedVector3.Dot(left: witness, right: up);
    }
    private void ApplyPush(
        ref FixedVector3 position,
        ref FixedVector3 velocity,
        in FixedContactPush push,
        bool blockClimb,
        in FixedVector3 up,
        ref bool grounded,
        ref FixedVector3 lastNormal,
        ref FixedVector3 groundNormal
    ) {
        // The obstruction witness (lastNormal) tracks only a NON-walkable push (a wall, not the ground or a ramp) —
        // a standing body re-resolves its ground contact every solver iteration, so an unconditional "last push"
        // would have the ground overwrite a genuine wall push from an earlier iteration in the SAME call.
        FixedContactPushMath.Commit(
            grounded: ref grounded,
            groundNormal: ref groundNormal,
            lastNormal: ref lastNormal,
            position: ref position,
            trial: FixedContactPushMath.ComputeOrdinary(
                // The capsule's Minkowski edge tilts even when the obstacle is a stair. Lateral approach
                // cannot buy elevation through that normal: only the raised step-up may clear the edge.
                blockClimb: blockClimb,
                groundedThreshold: GroundedThreshold,
                normal: push.Normal,
                penetration: push.Penetration,
                up: up,
                velocity: in velocity
            ),
            velocity: ref velocity
        );
    }

    /// <summary>Collects every static surface one body volume lies within a margin of, at a fixed pose: one exact query
    /// per collider whose bounds and frame come within the margin of the contact skin, so a surface the volume does not
    /// yet touch reports its positive gap. Each gap is measured to the skin's shell rather than the surface, so a body
    /// the solve rests on its surfaces keeps the same skin a swept body keeps. Nothing moves; this is the contact
    /// generation a rigid step solves from, the margin covering the step's travel.</summary>
    /// <param name="colliders">The static colliders.</param>
    /// <param name="dynamicColliders">The per-tick colliders.</param>
    /// <param name="position">The body's world position.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="volume">The body-local convex volume.</param>
    /// <param name="margin">The non-negative distance a surface's skin shell may lie from the volume and still count.</param>
    /// <param name="candidates">The colliders of <paramref name="colliders"/> reachable from a region covering the
    /// volume grown by the margin, or the default value to visit every collider.</param>
    /// <param name="destination">The destination for the surfaces, in collider order, static before attached.</param>
    /// <returns>The number of surfaces written, surfaces past the destination's length unreported; or -1 when a field
    /// collider lies within the margin and its partition does not narrow the region to one exact analytic collider or
    /// to none of its solid (<see cref="FixedFieldPartition"/>), since a field certifies contact only inside its own
    /// band and the margin can reach past it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="colliders"/> or <paramref name="dynamicColliders"/> is
    /// <see langword="null"/>.</exception>
    public int CollectContacts(FixedStaticColliderSet colliders, FixedStaticColliderSet dynamicColliders, FixedVector3 position,
        in FixedQuaternion orientation, in FixedBodyColliderVolume volume, FixedQ4816 margin, in FixedColliderCandidates candidates,
        Span<FixedSurfaceContact> destination) {
        ArgumentNullException.ThrowIfNull(argument: colliders);
        ArgumentNullException.ThrowIfNull(argument: dynamicColliders);

        var bounds = FixedColliderBounds.WorldBounds(orientation: in orientation, position: position, volume: in volume);
        var reach = (margin + ContactSkin);
        var grown = bounds.Grow(margin: reach);
        var written = Collect(boxes: colliders.Boxes, candidates: in candidates, colliders: colliders.Colliders, destination: destination,
            frames: colliders.Frames, grown: in grown, reach: reach, orientation: in orientation, position: position,
            source: FixedSurfaceColliderSource.Static, volume: in volume, written: 0);

        return (written < 0) ? written : Collect(boxes: dynamicColliders.Boxes, candidates: default, colliders: dynamicColliders.Colliders, destination: destination,
            frames: dynamicColliders.Frames, grown: in grown, reach: reach, orientation: in orientation, position: position,
            source: FixedSurfaceColliderSource.Dynamic, volume: in volume, written: written);
    }
    /// <summary>Builds the diagnostic support witness of the deepest walkable surface among those
    /// <see cref="CollectContacts"/> wrote: its collider, its normal and its depth into the skin. Nothing is queried.</summary>
    /// <param name="colliders">The static colliders the surfaces were collected from.</param>
    /// <param name="dynamicColliders">The per-tick colliders the surfaces were collected from.</param>
    /// <param name="contacts">The collected surfaces.</param>
    /// <param name="position">The body's world position at collection.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="volume">The body-local convex volume.</param>
    /// <param name="up">The body's unit up axis.</param>
    /// <returns>The witness, or <see langword="null"/> when no surface lies within <see cref="GroundedThreshold"/> of
    /// <paramref name="up"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="colliders"/> or <paramref name="dynamicColliders"/> is
    /// <see langword="null"/>.</exception>
    public ContactSupportWitness? SupportWitness(FixedStaticColliderSet colliders, FixedStaticColliderSet dynamicColliders,
        ReadOnlySpan<FixedSurfaceContact> contacts, FixedVector3 position, in FixedQuaternion orientation, in FixedBodyColliderVolume volume,
        in FixedVector3 up) {
        ArgumentNullException.ThrowIfNull(argument: colliders);
        ArgumentNullException.ThrowIfNull(argument: dynamicColliders);

        var deepest = -1;

        for (var index = 0; (index < contacts.Length); index++) {
            var alignment = FixedVector3.Dot(left: contacts[index].Normal, right: up);

            if (
                (alignment > FixedQ4816.Zero) &&
                (alignment >= GroundedThreshold) &&
                ((deepest < 0) || (contacts[index].Separation < contacts[deepest].Separation))
            ) {
                deepest = index;
            }
        }

        if (deepest < 0) {
            return null;
        }

        var contact = contacts[deepest];
        var set = ((contact.Source == FixedSurfaceColliderSource.Static) ? colliders : dynamicColliders);
        var collider = set.Colliders[contact.ColliderIndex];

        // A field collider's surface here may be one exact piece's (CollectContacts measured it there): the surface lies
        // within the separation past the skin of the volume, the region the witness narrows, so it names that piece.
        if (collider.Kind == FixedStaticColliderKind.Field) {
            var bounds = FixedColliderBounds.WorldBounds(orientation: in orientation, position: position, volume: in volume);

            if (collider.Narrow(collider: out var piece, reach: (contact.Separation + ContactSkin), region: in bounds) == FixedFieldRegion.Base) {
                collider = piece;
            }
        }

        return new ContactSupportWitness(Collider: collider, ColliderIndex: contact.ColliderIndex,
            Orientation: orientation, Position: position, Push: new FixedContactPush(Normal: contact.Normal, Penetration: -contact.Separation),
            Source: contact.Source, Volume: volume) {
            BodyPoint = (position + FixedRigidWitness.Anchor(volume, FixedVector3.Zero, orientation, -contact.Normal)),
            Up = up,
            WalkableThreshold = GroundedThreshold,
        };
    }
    private int Collect(ReadOnlySpan<FixedStaticCollider> colliders, ReadOnlySpan<FixedAabb> boxes,
        ReadOnlySpan<FixedColliderFrame> frames, in FixedColliderCandidates candidates, FixedSurfaceColliderSource source,
        FixedVector3 position, in FixedQuaternion orientation, in FixedBodyColliderVolume volume,
        in FixedAabb grown, FixedQ4816 reach, Span<FixedSurfaceContact> destination, int written) {
        var indexed = (candidates.IsActive && candidates.Contains(box: in grown));
        var next = 0;
        var center = grown.Center;
        var extent = grown.Extent;

        for (var index = 0; ((index < colliders.Length) && (written < destination.Length)); index++) {
            if (indexed) {
                while ((next < candidates.Indices.Length) && (candidates.Indices[next] < index)) { next++; }
                if (next == candidates.Indices.Length) { break; }
                index = candidates.Indices[next];
            }
            if (!boxes[index].Intersects(other: in grown)) {
                continue;
            }
            if (frames[index].IsOriented && frames[index].Separates(bodyExtent: in extent, offset: (center - boxes[index].Center), skin: FixedQ4816.Zero)) {
                continue;
            }
            var collider = colliders[index];

            if (collider.Kind == FixedStaticColliderKind.Field) {
                switch (collider.NarrowQuery(collider: out var narrowed, reach: FixedQ4816.Zero, region: in grown)) {
                    case FixedFieldRegion.Empty: {
                            continue;
                        }
                    case FixedFieldRegion.Base: {
                            collider = narrowed;
                            break;
                        }
                    default: {
                            return -1;
                        }
                }
            }

            var pushes = collider.TryGetPush(
                orientation: in orientation,
                position: position,
                push: out var push,
                skin: reach,
                uncertain: out _,
                volume: in volume
            );

            FixedStaticContactWork.Process.CountQuery(pushed: pushes);
            if (pushes) {
                destination[written++] = new FixedSurfaceContact(
                    ColliderIndex: index,
                    Normal: push.Normal,
                    Separation: ((reach - push.Penetration) - ContactSkin),
                    Source: source
                );
            }
        }

        return written;
    }
    /// <summary>Gets how far past a body's bounds the solve asks a surface: the contact skin plus the resting band
    /// (<see cref="FixedContactPushMath.RestingBand"/>), so ground a body rests on at its skin still holds it. A cull
    /// ahead of the solve keeps every collider within this reach.</summary>
    public FixedQ4816 ContactReach => (ContactSkin + FixedContactPushMath.RestingBand);

    private bool Sweep(
        ReadOnlySpan<FixedStaticCollider> colliders,
        ReadOnlySpan<FixedAabb> boxes,
        ReadOnlySpan<FixedColliderFrame> frames,
        in FixedColliderCandidates candidates,
        FixedSurfaceColliderSource source,
        ref ContactSupportWitness? support,
        ref FixedVector3 position,
        ref FixedVector3 velocity,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume,
        in FixedVector3 movement,
        in FixedVector3 previousPosition,
        in FixedVector3 up,
        ref bool grounded,
        ref FixedVector3 lastNormal,
        ref FixedVector3 groundNormal,
        ref FixedQ4816 requiredStepHeight,
        ref ContactUncertainty uncertain,
        int volumeIndex
    ) {
        var pushed = false;
        var culls = (boxes.Length == colliders.Length);
        var bounds = (culls ? FixedColliderBounds.WorldBounds(orientation: in orientation, position: position, volume: in volume) : default);
        // Every surface is asked at the skin plus the resting band, so ground the body rests on at its skin still holds it
        // (FixedContactPushMath.Classify).
        var skin = ContactReach;
        var reach = bounds.Grow(margin: skin);

        var indexed = (culls && candidates.IsActive && candidates.Contains(box: in bounds));
        var next = 0;

        for (var index = 0; (index < colliders.Length); index++) {
            if (indexed) {
                while ((next < candidates.Indices.Length) && (candidates.Indices[next] < index)) { next++; }
                if (next == candidates.Indices.Length) { break; }
                index = candidates.Indices[next];
            }
            if (culls && !boxes[index].Intersects(other: in reach)) {
                continue;
            }
            if (culls && frames[index].IsOriented && frames[index].Separates(bodyExtent: bounds.Extent, offset: (bounds.Center - boxes[index].Center), skin: skin)) {
                continue;
            }
            // The collider that answered: a field collider's exact piece where it narrowed, whose surface the edge witness
            // and the support witness below then read, as the push did.
            var met = colliders[index].TryGetAnsweredPush(
                answered: out var collider,
                orientation: in orientation,
                position: position,
                push: out var push,
                skin: skin,
                uncertain: out var unsure,
                volume: in volume
            );

            if (unsure) {
                uncertain = uncertain.With(pair: new ContactUncertainPair(Collider: index, Volume: volumeIndex,
                    Span: ((source == FixedSurfaceColliderSource.Static) ? ContactUncertaintySpan.Static : ContactUncertaintySpan.Dynamic)));
            }

            var depth = FixedQ4816.Zero;
            var kind = (met
                ? FixedContactPushMath.Classify(banded: push.Penetration, depth: out depth, groundedThreshold: GroundedThreshold, normal: push.Normal, up: in up)
                : FixedContactPushMath.ContactKind.None);
            var blockClimb = ((kind != FixedContactPushMath.ContactKind.None) && push.IsEdge &&
                (FixedVector3.Dot(left: push.Normal, right: up) > FixedQ4816.Zero) &&
                (FixedVector3.Dot(left: movement.RejectFrom(unitAxis: up), right: push.Normal) < FixedQ4816.Zero));

            // An edge the body approaches laterally is never resting support: only a push past the skin meets it.
            if ((kind == FixedContactPushMath.ContactKind.Rest) && blockClimb) {
                kind = FixedContactPushMath.ContactKind.None;
            }
            FixedStaticContactWork.Process.CountQuery(pushed: (kind == FixedContactPushMath.ContactKind.Push));
            if (kind != FixedContactPushMath.ContactKind.None) {
                var displaces = (kind == FixedContactPushMath.ContactKind.Push);

                push = push with { Penetration = depth };

                if (displaces && blockClimb) {
                    var bottom = FixedColliderBounds.MinimumProjection(normal: up, orientation: in orientation,
                        position: previousPosition, volume: in volume);
                    var probe = ((position + FixedRigidWitness.Anchor(volume, FixedVector3.Zero, orientation, -push.Normal)) +
                        (push.Normal * (push.Penetration - ContactSkin)));

                    // A nearly flat fillet at or below the pre-move foot is support, not a new riser. A surface that
                    // offers no witness keeps the climb blocked and asks no step height.
                    var witnessed = FixedSurfaceQuery.TryNearestOnCollider(collider: in collider, normal: out var normal, point: out var witness, probe: probe);

                    if (witnessed) {
                        blockClimb = ((FixedVector3.Dot(left: witness, right: up) - bottom) > FixedQ4816.Epsilon);
                    }
                    if (blockClimb && witnessed) {
                        requiredStepHeight = FixedQ4816.Max(x: requiredStepHeight, y: ((EdgeTop(collider: in collider, normal: in normal,
                            up: in up, witness: in witness) + ContactSkin) - bottom));
                    }
                }
                // Ground the body rests on grounds it and clamps its approach, and moves nothing.
                ApplyPush(
                    blockClimb: (displaces && blockClimb),
                    groundNormal: ref groundNormal,
                    grounded: ref grounded,
                    lastNormal: ref lastNormal,
                    position: ref position,
                    push: (displaces ? push : (push with { Penetration = FixedQ4816.Zero })),
                    up: in up,
                    velocity: ref velocity
                );
                var alignment = FixedVector3.Dot(left: push.Normal, right: up);
                if ((alignment > FixedQ4816.Zero) && (alignment >= GroundedThreshold)) {
                    support = new ContactSupportWitness(Collider: collider, ColliderIndex: index, Orientation: orientation,
                        Position: position, Push: push, Source: source, Volume: volume) {
                        BodyPoint = (position + FixedRigidWitness.Anchor(volume, FixedVector3.Zero, orientation, -push.Normal)),
                        Up = up,
                        WalkableThreshold = GroundedThreshold,
                    };
                }
                if (displaces) {
                    pushed = true;
                    if (culls) {
                        bounds = FixedColliderBounds.WorldBounds(orientation: in orientation, position: position, volume: in volume);
                        reach = bounds.Grow(margin: skin);
                        indexed &= candidates.Contains(box: in bounds);
                    }
                }
            }
        }

        return pushed;
    }

    /// <summary>Resolves a body's position and velocity out of the static set and the per-tick colliders.</summary>
    /// <param name="colliders">The colliders compiled once, with the bounds the solve culls by.</param>
    /// <param name="dynamicColliders">The colliders the caller recomputes per tick, with the bounds the solve culls by.</param>
    /// <param name="position">The body's foot point (in/out).</param>
    /// <param name="velocity">The body's velocity (in/out): the component driving into any resolved surface is removed.</param>
    /// <param name="orientation">The body's local-to-world orientation.</param>
    /// <param name="volumes">The body's compiled convex volumes.</param>
    /// <param name="up">The body's up axis, which the grounded test measures a contact normal's alignment against.</param>
    /// <param name="candidates">The colliders of <paramref name="colliders"/> reachable from a region around the body,
    /// or the default value to visit every collider.</param>
    /// <returns>The grounded verdict, the surface it grounded on, and the last resolved non-walkable contact normal.</returns>
    public ContactResolution Resolve(
        FixedStaticColliderSet colliders,
        FixedStaticColliderSet dynamicColliders,
        ref FixedVector3 position,
        ref FixedVector3 velocity,
        in FixedQuaternion orientation,
        ReadOnlySpan<FixedBodyColliderVolume> volumes,
        in FixedVector3 up,
        FixedColliderCandidates candidates = default
    ) {
        return ResolveSweep(candidates: candidates, colliders: colliders,
            dynamicColliders: dynamicColliders, orientation: in orientation, position: ref position, previousPosition: position, up: in up,
            velocity: ref velocity, volumes: volumes);
    }
    /// <summary>Sweeps a move in slices bounded by the moving volumes' support radii, then resolves each endpoint.
    /// Keeps lateral edge approach separate from vertical support. An edge above the
    /// pre-move foot met laterally is a wall; a vertical landing still uses its measured surface normal.</summary>
    /// <param name="colliders">The static colliders, with the bounds the solve culls by; skipping a collider those
    /// bounds rule out never changes the result.</param>
    /// <param name="dynamicColliders">The per-tick colliders, with the bounds the solve culls by.</param>
    /// <param name="previousPosition">The foot point before the move.</param>
    /// <param name="position">The target and resolved foot point.</param>
    /// <param name="velocity">The velocity to resolve.</param>
    /// <param name="orientation">The body orientation.</param>
    /// <param name="volumes">The body volumes.</param>
    /// <param name="up">The body's unit up axis.</param>
    /// <param name="candidates">The colliders of <paramref name="colliders"/> reachable from a region around the move,
    /// or the default value to visit every collider.</param>
    /// <returns>The support and obstruction witnesses.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="colliders"/> or <paramref name="dynamicColliders"/> is
    /// <see langword="null"/>.</exception>
    public ContactResolution ResolveSweep(FixedStaticColliderSet colliders, FixedStaticColliderSet dynamicColliders,
        in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation,
        ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up, FixedColliderCandidates candidates = default) {
        ArgumentNullException.ThrowIfNull(argument: colliders);
        ArgumentNullException.ThrowIfNull(argument: dynamicColliders);
        FixedStaticContactWork.Process.CountResolve();
        if (!FixedVector3.TrySubtract(difference: out var movement, left: position, right: previousPosition)) {
            position = previousPosition;
            return new ContactResolution(Grounded: false, ObstructionNormal: default, Refusal: ContactRefusal.UnrepresentableSweep);
        }
        if ((volumes.IsEmpty) || ((colliders.Count == 0) && (dynamicColliders.Count == 0))) {
            return ResolveEndpoint(candidates: candidates, colliders: colliders, dynamicColliders: dynamicColliders, orientation: orientation, position: ref position, previousPosition: previousPosition, sliceStart: previousPosition, up: up, velocity: ref velocity, volumes: volumes);
        }
        var radius = FixedQ4816.MaxValue;

        foreach (ref readonly var volume in volumes) {
            radius = FixedQ4816.Min(x: radius, y: FixedColliderBounds.MinimumSupportRadius(volume: in volume));
        }
        // Half a body's least support radius is also the field sweep's core radius. L1 bounds the slice's
        // length without a rounded square root. The rate carrier splits all raw displacement exactly.
        var slice = Math.Max(val1: 1L, val2: (radius.Value / 2L));
        var travel = ((Int128.Abs(value: movement.X.Value) + Int128.Abs(value: movement.Y.Value)) + Int128.Abs(value: movement.Z.Value));
        var parts = travel.CeilingDivide(divisor: ((Int128)slice));

        if ((parts <= 1) || !SweptBoundsMeetCollider(candidates: in candidates, colliders: colliders,
            dynamicColliders: dynamicColliders, orientation: in orientation, position: in position, previousPosition: in previousPosition, volumes: volumes)) {
            return ResolveEndpoint(candidates: candidates, colliders: colliders, dynamicColliders: dynamicColliders, orientation: orientation, position: ref position, previousPosition: previousPosition, sliceStart: previousPosition, up: up, velocity: ref velocity, volumes: volumes);
        }
        if (parts > long.MaxValue) {
            position = previousPosition;
            return new ContactResolution(Grounded: false, ObstructionNormal: default, Uncertainty: ContactUncertainty.Unknown);
        }
        var split = new FixedVector3RateAccumulator(ticksPerSecond: ((long)parts));
        var count = ((int)Int128.Min(x: parts, y: FixedFieldContactSolver.DefaultSweepBoundsQueryBudget));
        var resolved = previousPosition;
        var originalVelocity = velocity;
        var result = default(ContactResolution);
        var obstruction = FixedVector3.Zero;
        var stepHeight = FixedQ4816.Zero;
        var uncertain = default(ContactUncertainty);

        for (var part = 0; (part < count); part++) {
            var increment = split.Integrate(elapsedTicks: 1UL, ratePerSecond: movement);

            if (!FixedVector3.TryAdd(left: resolved, right: increment, sum: out position)) {
                position = previousPosition;
                velocity = originalVelocity;
                return new ContactResolution(Grounded: false, ObstructionNormal: default, Refusal: ContactRefusal.UnrepresentableSweep);
            }
            result = ResolveEndpoint(candidates: candidates, colliders: colliders, dynamicColliders: dynamicColliders, orientation: orientation, position: ref position, previousPosition: previousPosition, sliceStart: resolved, up: up, velocity: ref velocity, volumes: volumes);
            resolved = position;
            if (result.ObstructionNormal != FixedVector3.Zero) { obstruction = result.ObstructionNormal; }
            stepHeight = FixedQ4816.Max(x: stepHeight, y: result.RequiredStepHeight);
            uncertain = uncertain.Union(other: result.Uncertainty);
        }
        // Match field-sweep budget exhaustion: keep only the proved prefix and report it as unproved,
        // without turning a work limit into a physical obstruction or integrating the unvisited suffix.
        return result with { ObstructionNormal = obstruction, RequiredStepHeight = stepHeight,
            Uncertainty = ((parts > count) ? uncertain.Union(other: ContactUncertainty.Unknown) : uncertain) };
    }

    /// <summary>Finds the support distance below a body (<see cref="IContactField.TrySupportDistance"/>) over the static
    /// set and the per-tick colliders: the least of each collider's own support distance
    /// (<see cref="FixedSupportDistance.TryDescend"/>), visiting only the colliders whose bounds come within the skin of
    /// the box every pose of the descent lies in.</summary>
    /// <param name="colliders">The static colliders, with the bounds the query culls by.</param>
    /// <param name="dynamicColliders">The per-tick colliders, with the bounds the query culls by.</param>
    /// <param name="position">The foot point the descent starts from.</param>
    /// <param name="orientation">The body orientation.</param>
    /// <param name="volumes">The body volumes.</param>
    /// <param name="up">The body's unit up axis.</param>
    /// <param name="reach">The greatest descent searched, in metres.</param>
    /// <param name="descent">The least descent, in metres, at which the body first comes within the skin of a collider.</param>
    /// <param name="candidates">The colliders of <paramref name="colliders"/> reachable from a region around the descent,
    /// or the default value to visit every collider.</param>
    /// <returns><see langword="true"/> when a collider is met within the reach; <see langword="false"/> as well for a
    /// non-positive reach or a descent outside the Q48.16 carrier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="colliders"/> or <paramref name="dynamicColliders"/> is
    /// <see langword="null"/>.</exception>
    public bool TrySupportDistance(FixedStaticColliderSet colliders, FixedStaticColliderSet dynamicColliders, in FixedVector3 position,
        in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up, FixedQ4816 reach,
        out FixedQ4816 descent, FixedColliderCandidates candidates = default) {
        ArgumentNullException.ThrowIfNull(argument: colliders);
        ArgumentNullException.ThrowIfNull(argument: dynamicColliders);
        FixedStaticContactWork.Process.CountSupportDistance();
        descent = FixedQ4816.Zero;
        if ((reach <= FixedQ4816.Zero) || !FixedVector3.TrySubtract(difference: out var lowest, left: position, right: (up * reach))) {
            return false;
        }
        var met = false;
        var nearest = reach;
        var swept = SweptBounds(orientation: in orientation, position: in lowest, previousPosition: in position, volumes: volumes);

        foreach (ref readonly var volume in volumes) {
            Descend(boxes: colliders.Boxes, candidates: in candidates, colliders: colliders.Colliders, swept: in swept,
                frames: colliders.Frames, met: ref met, nearest: ref nearest, orientation: in orientation, position: in position, up: in up, volume: in volume);
            Descend(boxes: dynamicColliders.Boxes, candidates: default, colliders: dynamicColliders.Colliders, swept: in swept,
                frames: dynamicColliders.Frames, met: ref met, nearest: ref nearest, orientation: in orientation, position: in position, up: in up, volume: in volume);
        }
        descent = (met ? nearest : FixedQ4816.Zero);
        return met;
    }
    // Lowers the nearest support distance found by every collider of one span the descent's box can reach. Each collider
    // searches only as far as the nearest found so far, since a support met past it cannot lower it.
    private void Descend(ReadOnlySpan<FixedStaticCollider> colliders, ReadOnlySpan<FixedAabb> boxes, ReadOnlySpan<FixedColliderFrame> frames,
        in FixedColliderCandidates candidates, in FixedAabb swept, in FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, in FixedVector3 up, ref FixedQ4816 nearest, ref bool met) {
        var culls = (boxes.Length == colliders.Length);
        var indexed = (culls && candidates.IsActive && candidates.Contains(box: in swept));
        var next = 0;
        var reach = swept.Grow(margin: ContactSkin);
        var center = swept.Center;
        var extent = swept.Extent;

        for (var index = 0; (index < colliders.Length); index++) {
            if (indexed) {
                while ((next < candidates.Indices.Length) && (candidates.Indices[next] < index)) { next++; }
                if (next == candidates.Indices.Length) { break; }
                index = candidates.Indices[next];
            }
            if (culls && (!boxes[index].Intersects(other: in reach) ||
                (frames[index].IsOriented && frames[index].Separates(bodyExtent: in extent, offset: (center - boxes[index].Center), skin: ContactSkin)))) {
                continue;
            }
            if (FixedSupportDistance.TryDescend(collider: in colliders[index], descent: out var descent, orientation: in orientation,
                position: position, reach: nearest, skin: ContactSkin, up: in up, volume: in volume) && (!met || (descent < nearest))) {
                nearest = descent;
                met = true;
            }
        }
    }

    // Every intermediate pose of a straight move lies inside the box spanning its start and end bounds. A collider
    // whose bounds miss that box, grown by the skin, pushes no slice, so slicing is spent only where a push can happen.
    // A collider without bounds (a half-space) may meet any move.
    private bool SweptBoundsMeetCollider(FixedStaticColliderSet colliders, FixedStaticColliderSet dynamicColliders,
        in FixedVector3 previousPosition, in FixedVector3 position, in FixedQuaternion orientation,
        ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedColliderCandidates candidates) {
        var swept = SweptBounds(orientation: in orientation, position: in position, previousPosition: in previousPosition, volumes: volumes);
        var reach = swept.Grow(margin: ContactReach);
        var boxes = colliders.Boxes;

        if (candidates.IsActive && candidates.Contains(box: in swept)) {
            foreach (var index in candidates.Indices) {
                if (boxes[index].Intersects(other: in reach)) { return true; }
            }
        } else {
            foreach (ref readonly var box in boxes) {
                if (box.Intersects(other: in reach)) { return true; }
            }
        }
        foreach (ref readonly var box in dynamicColliders.Boxes) {
            if (box.Intersects(other: in reach)) { return true; }
        }
        return false;
    }
    // The world box every volume's bounds lie in at every pose of a straight move: the orientation is fixed, so each
    // volume's bounds only translate, and the box enclosing them at both ends holds every pose between.
    private static FixedAabb SweptBounds(in FixedVector3 previousPosition, in FixedVector3 position,
        in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes) {
        var swept = FixedAabb.Empty;

        foreach (ref readonly var volume in volumes) {
            foreach (var at in (ReadOnlySpan<FixedVector3>)[previousPosition, position]) {
                swept = swept.Enclose(other: FixedColliderBounds.WorldBounds(orientation: in orientation, position: at, volume: in volume));
            }
        }
        return swept;
    }

    // previousPosition is the whole move's start, the pre-move foot every slice's edge test measures from, as
    // FixedStepUp raises from that start; sliceStart begins this slice, whose displacement is the approach direction.
    private ContactResolution ResolveEndpoint(FixedStaticColliderSet colliders, FixedStaticColliderSet dynamicColliders,
        in FixedVector3 previousPosition, in FixedVector3 sliceStart, ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation,
        ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up, FixedColliderCandidates candidates) {
        var movement = (position - sliceStart);
        var requiredStepHeight = FixedQ4816.Zero;
        var grounded = false;
        ContactSupportWitness? support = null;
        var groundNormal = FixedVector3.Zero;
        var lastNormal = FixedVector3.Zero;
        var iterations = Math.Max(
            val1: 1,
            val2: MaxIterations
        );

        var uncertain = default(ContactUncertainty);

        for (var iteration = 0; (iteration < iterations); iteration++) {
            var pushed = false;

            // Only the last pass, at the pose the body keeps, says whether that pose is proved.
            uncertain = default;

            for (var volumeIndex = 0; (volumeIndex < volumes.Length); volumeIndex++) {
                ref readonly var volume = ref volumes[volumeIndex];

                pushed |= Sweep(
                    candidates: in candidates,
                    colliders: colliders.Colliders,
                    boxes: colliders.Boxes,
                    frames: colliders.Frames,
                    groundNormal: ref groundNormal,
                    grounded: ref grounded,
                    lastNormal: ref lastNormal,
                    movement: in movement,
                    orientation: in orientation,
                    position: ref position,
                    previousPosition: in previousPosition,
                    requiredStepHeight: ref requiredStepHeight,
                    source: FixedSurfaceColliderSource.Static,
                    support: ref support,
                    up: in up,
                    velocity: ref velocity,
                    uncertain: ref uncertain,
                    volume: in volume,
                    volumeIndex: volumeIndex
                );
                pushed |= Sweep(
                    candidates: default,
                    colliders: dynamicColliders.Colliders,
                    boxes: dynamicColliders.Boxes,
                    frames: dynamicColliders.Frames,
                    groundNormal: ref groundNormal,
                    grounded: ref grounded,
                    lastNormal: ref lastNormal,
                    movement: in movement,
                    orientation: in orientation,
                    position: ref position,
                    previousPosition: in previousPosition,
                    requiredStepHeight: ref requiredStepHeight,
                    source: FixedSurfaceColliderSource.Dynamic,
                    support: ref support,
                    up: in up,
                    velocity: ref velocity,
                    uncertain: ref uncertain,
                    volume: in volume,
                    volumeIndex: volumeIndex
                );
            }

            if (!pushed) {
                break;
            }
        }

        return new ContactResolution(
            RequiredStepHeight: requiredStepHeight,
            GroundNormal: groundNormal,
            Grounded: grounded,
            ObstructionNormal: lastNormal,
            Uncertainty: uncertain
        ) { Support = support };
    }
}
