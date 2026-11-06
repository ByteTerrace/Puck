using Puck.Maths;

namespace Puck.Physics;

/// <summary>One rigid body's pose and collider as the contact generators read it.</summary>
/// <param name="Id">The caller's id for the body, written into every candidate's body id.</param>
/// <param name="Root">The body root's world position.</param>
/// <param name="Orientation">The body's world orientation.</param>
/// <param name="Volume">The body's single convex volume, root-local and scaled for its live scale.</param>
/// <param name="CenterOffset">The centre-of-mass offset from the root, root-local axes.</param>
public readonly record struct FixedRigidContactShape(int Id, FixedVector3 Root, FixedQuaternion Orientation, FixedBodyColliderVolume Volume, FixedVector3 CenterOffset) {
    /// <summary>Gets the body's world centre of mass.</summary>
    public FixedVector3 Center => (Root + Orientation.Rotate(vector: CenterOffset));
}
/// <summary>
/// Turns one contact a geometry query found — a body against a surface, or two bodies — into the persistent manifold
/// candidates <see cref="FixedRigidWorld"/> solves: up to four points for a face, each with a stable feature id and its
/// own separation, so the solve can hold a box flat and let it tip, and a corner lifted off the surface is a
/// speculative contact rather than a missing one. The geometry query runs once per pair; these generators only
/// distribute its normal and depth over the shapes' support features.
/// </summary>
public static class FixedRigidContacts {
    /// <summary>The feature id of a pair's single witness point, when no face point applies.</summary>
    public const int PairWitnessFeature = 16;

    /// <summary>Appends the candidates of a body touching a static surface: every support point of the body facing the
    /// surface, each separated from the surface's plane through the deepest point by its own depth along the normal.</summary>
    /// <param name="candidates">The list the candidates are appended to.</param>
    /// <param name="surfaceId">The surface's body id, the candidates' A side; its anchors are zero.</param>
    /// <param name="sourceId">The source id naming which surface contact this is, stable from step to step.</param>
    /// <param name="body">The body, the candidates' B side.</param>
    /// <param name="normal">The unit surface normal, pointing from the surface toward the body.</param>
    /// <param name="separation">The signed gap of the body's deepest support point; negative overlaps.</param>
    /// <returns>The number of candidates appended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidates"/> is <see langword="null"/>.</exception>
    public static int AppendSurface(List<FixedTwoBodyContact> candidates, int surfaceId, int sourceId, in FixedRigidContactShape body, FixedVector3 normal, FixedQ4816 separation) {
        ArgumentNullException.ThrowIfNull(argument: candidates);

        Span<FixedVector3> anchors = stackalloc FixedVector3[FixedRigidWitness.MaximumSupportPoints];
        Span<int> features = stackalloc int[FixedRigidWitness.MaximumSupportPoints];
        var count = FixedRigidWitness.SupportManifold(
            anchors: anchors,
            centerOffset: body.CenterOffset,
            features: features,
            normal: normal,
            orientation: body.Orientation,
            volume: body.Volume
        );
        var deepest = Deepest(
            anchors: anchors[..count],
            normal: normal
        );

        for (var point = 0; (point < count); point++) {
            candidates.Add(item: new FixedTwoBodyContact(
                AnchorA: FixedVector3.Zero,
                AnchorB: anchors[point],
                BodyIdA: surfaceId,
                BodyIdB: body.Id,
                FeatureId: features[point],
                Normal: normal,
                Separation: (separation + (FixedVector3.Dot(left: anchors[point], right: normal) - deepest)),
                SourceId: sourceId
            ));
        }

        return count;
    }
    /// <summary>Appends the candidates of two overlapping bodies. Two boxes contribute the corners of the B body's face
    /// toward the A body, each clamped onto the A body's box grown by the margin — the overlap rectangle of two aligned
    /// faces, whichever face is larger; a curved body contacts at its own support point. Each point carries both anchors,
    /// on the same world point, and its own separation.</summary>
    /// <param name="candidates">The list the candidates are appended to.</param>
    /// <param name="sourceId">The source id of the pair's contact, stable from step to step.</param>
    /// <param name="a">The body the normal points away from.</param>
    /// <param name="b">The body the normal points toward.</param>
    /// <param name="normal">The unit contact normal, from <paramref name="a"/> toward <paramref name="b"/>.</param>
    /// <param name="depth">The non-negative overlap depth along the normal.</param>
    /// <param name="clipMargin">The non-negative growth of the A box a face point is clamped onto.</param>
    /// <returns>The number of candidates appended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidates"/> is <see langword="null"/>.</exception>
    public static int AppendPair(List<FixedTwoBodyContact> candidates, int sourceId, in FixedRigidContactShape a, in FixedRigidContactShape b, FixedVector3 normal, FixedQ4816 depth, FixedQ4816 clipMargin) {
        ArgumentNullException.ThrowIfNull(argument: candidates);

        var centerA = a.Center;
        var centerB = b.Center;

        if (
            (a.Volume.Kind == FixedBodyColliderKind.Box) &&
            (b.Volume.Kind == FixedBodyColliderKind.Box)
        ) {
            Span<FixedVector3> anchors = stackalloc FixedVector3[FixedRigidWitness.MaximumSupportPoints];
            Span<int> features = stackalloc int[FixedRigidWitness.MaximumSupportPoints];
            var count = FixedRigidWitness.SupportManifold(
                anchors: anchors,
                centerOffset: b.CenterOffset,
                features: features,
                normal: normal,
                orientation: b.Orientation,
                volume: b.Volume
            );
            var deepest = Deepest(
                anchors: anchors[..count],
                normal: normal
            );

            for (var index = 0; (index < count); index++) {
                var point = FixedRigidWitness.BoxClamp(
                    margin: (depth + clipMargin),
                    orientation: a.Orientation,
                    point: (centerB + anchors[index]),
                    root: a.Root,
                    volume: a.Volume
                );

                candidates.Add(item: new FixedTwoBodyContact(
                    AnchorA: (point - centerA),
                    AnchorB: (point - centerB),
                    BodyIdA: a.Id,
                    BodyIdB: b.Id,
                    FeatureId: features[index],
                    Normal: normal,
                    Separation: (-depth + (FixedVector3.Dot(left: anchors[index], right: normal) - deepest)),
                    SourceId: sourceId
                ));
            }

            return count;
        }

        // A curved A body touches at its own support point; otherwise the B body's support point stands in.
        var witness = ((a.Volume.Kind != FixedBodyColliderKind.Box)
            ? (centerA + FixedRigidWitness.Anchor(
                centerOffset: a.CenterOffset,
                orientation: a.Orientation,
                volume: a.Volume,
                worldDirection: normal
            ))
            : WitnessPoint(b: in b, normal: normal)
        );

        AddWitness(aCenter: centerA, aId: a.Id, b: in b, candidates: candidates, depth: depth, normal: normal, sourceId: sourceId, witness: witness);

        return 1;
    }
    /// <summary>Appends the one candidate of a body overlapping a side whose shape the solve does not read — a kinematic
    /// body that pushes without being pushed: the B body's own support point toward the A side.</summary>
    /// <param name="candidates">The list the candidate is appended to.</param>
    /// <param name="sourceId">The source id of the pair's contact, stable from step to step.</param>
    /// <param name="aId">The A side's body id.</param>
    /// <param name="aCenter">The A side's world centre, which its anchor is measured from.</param>
    /// <param name="b">The body the normal points toward.</param>
    /// <param name="normal">The unit contact normal, from the A side toward <paramref name="b"/>.</param>
    /// <param name="depth">The non-negative overlap depth along the normal.</param>
    /// <returns>The number of candidates appended: one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidates"/> is <see langword="null"/>.</exception>
    public static int AppendWitness(List<FixedTwoBodyContact> candidates, int sourceId, int aId, FixedVector3 aCenter, in FixedRigidContactShape b, FixedVector3 normal, FixedQ4816 depth) {
        ArgumentNullException.ThrowIfNull(argument: candidates);
        AddWitness(aCenter: aCenter, aId: aId, b: in b, candidates: candidates, depth: depth, normal: normal, sourceId: sourceId,
            witness: WitnessPoint(b: in b, normal: normal));

        return 1;
    }

    private static void AddWitness(List<FixedTwoBodyContact> candidates, int sourceId, int aId, FixedVector3 aCenter, in FixedRigidContactShape b, FixedVector3 normal, FixedQ4816 depth, FixedVector3 witness) =>
        candidates.Add(item: new FixedTwoBodyContact(
            AnchorA: (witness - aCenter),
            AnchorB: (witness - b.Center),
            BodyIdA: aId,
            BodyIdB: b.Id,
            FeatureId: PairWitnessFeature,
            Normal: normal,
            Separation: -depth,
            SourceId: sourceId
        ));
    private static FixedVector3 WitnessPoint(in FixedRigidContactShape b, FixedVector3 normal) =>
        (b.Center + FixedRigidWitness.Anchor(
            centerOffset: b.CenterOffset,
            orientation: b.Orientation,
            volume: b.Volume,
            worldDirection: -normal
        ));

    // The least normal component among support points: the deepest point's.
    private static FixedQ4816 Deepest(ReadOnlySpan<FixedVector3> anchors, FixedVector3 normal) {
        var deepest = FixedQ4816.MaxValue;

        foreach (var anchor in anchors) {
            deepest = FixedQ4816.Min(
                x: deepest,
                y: FixedVector3.Dot(left: anchor, right: normal)
            );
        }

        return deepest;
    }
}
