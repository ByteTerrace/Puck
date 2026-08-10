using Puck.Maths;

namespace Puck.Dynamics.Spike.Tests.Core;

/// <summary>
/// Where each family of solver quantity sits in its 64-bit carrier. Mass and inertia span a wider window than any one
/// carrier holds once world scale and density are free, so the placement is a per-fixture parameter rather than a
/// library constant; length, velocity and impulse stay at <see cref="FixedQ4816"/>'s own scale because that is what the
/// vector and quaternion types already carry.
/// </summary>
/// <param name="InverseMass">The fraction bit count inverse mass is placed at.</param>
/// <param name="InverseInertia">The fraction bit count the body-local inverse inertia tensor is placed at.</param>
/// <param name="EffectiveMass">The fraction bit count a constraint's effective mass is placed at.</param>
internal readonly record struct SpikeScales(int InverseMass, int InverseInertia, int EffectiveMass) {
    /// <summary>A placement that suits a body of roughly one to a hundred kilograms at metre scale.</summary>
    internal static SpikeScales RoomScale => new(InverseMass: 40, InverseInertia: 40, EffectiveMass: 32);
}

/// <summary>How the spike activates a contact whose current separation is still positive.</summary>
internal enum SpeculativeActivation {
    /// <summary>Current separation minus a conservatively rounded-UP bound on the closing travel this step.</summary>
    Conservative,
    /// <summary>Current separation alone — the sabotage that removes the predicted term entirely.</summary>
    CurrentOnly,
    /// <summary>Current separation minus a round-to-NEAREST bound on the closing travel — the sabotage that keeps the
    /// prediction but drops its direction.</summary>
    NearestRounded,
}

/// <summary>
/// Every switch the spike's laws turn, so a red run is a mechanism change rather than an edited expectation. The
/// defaults are the intended solver; each non-default is one named sabotage.
/// </summary>
internal sealed class SolverOptions {
    /// <summary>The world's simulation rate in hertz.</summary>
    internal int RateHz { get; init; } = 60;
    /// <summary>The number of temporal substeps per step.</summary>
    internal int SubstepCount { get; init; } = 4;
    /// <summary>The number of biased solve iterations per substep.</summary>
    internal int SolveIterations { get; init; } = 1;
    /// <summary>The number of unbiased relax iterations per substep.</summary>
    internal int RelaxIterations { get; init; } = 1;
    /// <summary>The number of restitution iterations per step.</summary>
    internal int RestitutionIterations { get; init; } = 1;
    /// <summary>The authored contact frequency, before the substep-derived clamp.</summary>
    internal FixedQ4816 ContactHertz { get; init; } = FixedQ4816.FromInteger(value: 30L);
    /// <summary>The authored contact damping ratio.</summary>
    internal FixedQ4816 ContactDampingRatio { get; init; } = FixedQ4816.FromInteger(value: 10L);
    /// <summary>The ceiling on the push-out speed a biased solve may request.</summary>
    internal FixedQ4816 ContactSpeed { get; init; } = FixedQ4816.FromInteger(value: 3L);
    /// <summary>The coefficient of restitution.</summary>
    internal FixedQ4816 Restitution { get; init; } = FixedQ4816.Zero;
    /// <summary>The relative normal speed below which restitution is not applied.</summary>
    internal FixedQ4816 RestitutionThreshold { get; init; } = FixedQ4816.FromInteger(value: 1L);
    /// <summary>The uniform acceleration applied to every dynamic body each substep.</summary>
    internal FixedVector3 Gravity { get; init; } = new(X: FixedQ4816.Zero, Y: FixedQ4816.FromDouble(value: -9.81d), Z: FixedQ4816.Zero);
    /// <summary>An extra uniform acceleration applied to every dynamic body each substep.</summary>
    internal FixedVector3 AppliedAcceleration { get; init; } = FixedVector3.Zero;
    /// <summary>The separation at or below which an ordinary contact activates.</summary>
    internal FixedQ4816 ContactMargin { get; init; } = FixedQ4816.FromDouble(value: 0.04d);
    /// <summary>The separation below which a contact is routed to the recovery path rather than solved.</summary>
    internal FixedQ4816 RecoveryThreshold { get; init; } = FixedQ4816.FromDouble(value: -0.05d);
    /// <summary>The ceiling on the extraction speed the recovery path may request.</summary>
    internal FixedQ4816 RecoverySpeed { get; init; } = FixedQ4816.FromInteger(value: 2L);
    /// <summary>Whether stored impulses are re-applied at the head of each substep.</summary>
    internal bool WarmStart { get; init; } = true;
    /// <summary>Whether candidates are canonically ordered before they are associated into slots.</summary>
    internal bool CanonicalOrder { get; init; } = true;
    /// <summary>Whether a candidate is associated by its composite identity plus deterministic geometric matching.
    /// Turning it off keys a slot by the BODY FEATURE index alone — the refuted scheme in which one sphere touching a
    /// floor and a wall at once collapses into a single cache entry.</summary>
    internal bool CompositeIdentity { get; init; } = true;
    /// <summary>How a positive current separation is turned into an activation decision.</summary>
    internal SpeculativeActivation Activation { get; init; } = SpeculativeActivation.Conservative;
    /// <summary>Whether a candidate past <see cref="RecoveryThreshold"/> is routed to the bounded extraction path.</summary>
    internal bool DeepRecovery { get; init; } = true;
    /// <summary>The largest accumulated impulse change, in raw Q48.16 units, an iteration may leave and still be read
    /// as converged. It is a READING, never a control: the solve always runs its full iteration budget, so changing
    /// this cannot change a trajectory.</summary>
    internal long ConvergenceToleranceRaw { get; init; } = 64L;
    /// <summary>Where each family of solver quantity sits in its carrier.</summary>
    internal SpikeScales Scales { get; init; } = SpikeScales.RoomScale;
}

/// <summary>
/// One rigid body as the solver sees it. There is no absolute position anywhere on this type: the solver reads the
/// per-step displacement it has itself accumulated and the contact anchors a candidate carried, and nothing else. The
/// owning fixture holds the absolute placement and applies the committed displacement at the end of a step.
/// </summary>
internal sealed class SpikeBody {
    /// <summary>The inverse mass raw, at <see cref="SpikeScales.InverseMass"/>; zero marks a static body.</summary>
    internal long InverseMassRaw { get; set; }
    /// <summary>The body-local inverse inertia's <c>(0,0)</c> entry.</summary>
    internal long InverseInertiaXX { get; set; }
    /// <summary>The body-local inverse inertia's <c>(1,1)</c> entry.</summary>
    internal long InverseInertiaYY { get; set; }
    /// <summary>The body-local inverse inertia's <c>(2,2)</c> entry.</summary>
    internal long InverseInertiaZZ { get; set; }
    /// <summary>The body-local inverse inertia's <c>(0,1)</c> entry.</summary>
    internal long InverseInertiaXY { get; set; }
    /// <summary>The body-local inverse inertia's <c>(0,2)</c> entry.</summary>
    internal long InverseInertiaXZ { get; set; }
    /// <summary>The body-local inverse inertia's <c>(1,2)</c> entry.</summary>
    internal long InverseInertiaYZ { get; set; }
    /// <summary>The rotation taking body axes to world axes at the head of the step.</summary>
    internal FixedQuaternion Orientation { get; set; } = FixedQuaternion.Identity;
    /// <summary>The linear velocity in world axes.</summary>
    internal FixedVector3 LinearVelocity { get; set; }
    /// <summary>The angular velocity in world axes.</summary>
    internal FixedVector3 AngularVelocity { get; set; }
    /// <summary>The displacement accumulated within the current step.</summary>
    internal FixedVector3 DeltaPosition { get; set; }
    /// <summary>The rotation accumulated within the current step.</summary>
    internal FixedQuaternion DeltaRotation { get; set; } = FixedQuaternion.Identity;
    /// <summary>The AUTHORED direction the recovery path extracts a deeply overlapping body along. It is authored
    /// rather than derived because a body embedded past a thin solid's midplane has a nearest surface on the wrong
    /// side, and a normal read off that surface would extract it further in.</summary>
    internal FixedVector3 EscapeDirection { get; set; } = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);

    /// <summary>Gets whether the body responds to impulses.</summary>
    internal bool IsDynamic => (InverseMassRaw > 0L);

    /// <summary>Gets the rotation taking body axes to world axes as of the current substep.</summary>
    internal FixedQuaternion CurrentOrientation => (DeltaRotation * Orientation);

    /// <summary>Clears the per-step displacement accumulators.</summary>
    internal void ResetStepAccumulators() {
        DeltaPosition = FixedVector3.Zero;
        DeltaRotation = FixedQuaternion.Identity;
    }

    /// <summary>Folds every state word into a running digest, in declaration order.</summary>
    /// <param name="digest">The running digest.</param>
    /// <returns>The updated digest.</returns>
    internal ulong Fold(ulong digest) {
        digest = SpikeArithmetic.Fold(digest: digest, value: LinearVelocity.X.Value);
        digest = SpikeArithmetic.Fold(digest: digest, value: LinearVelocity.Y.Value);
        digest = SpikeArithmetic.Fold(digest: digest, value: LinearVelocity.Z.Value);
        digest = SpikeArithmetic.Fold(digest: digest, value: AngularVelocity.X.Value);
        digest = SpikeArithmetic.Fold(digest: digest, value: AngularVelocity.Y.Value);
        digest = SpikeArithmetic.Fold(digest: digest, value: AngularVelocity.Z.Value);
        digest = SpikeArithmetic.Fold(digest: digest, value: Orientation.X.Value);
        digest = SpikeArithmetic.Fold(digest: digest, value: Orientation.Y.Value);
        digest = SpikeArithmetic.Fold(digest: digest, value: Orientation.Z.Value);
        digest = SpikeArithmetic.Fold(digest: digest, value: Orientation.W.Value);

        return digest;
    }
}

/// <summary>
/// One contact CANDIDATE: a surface a generator believes the body may be touching, carrying the identity of what
/// produced it, the witness geometry, and the separation. A candidate is not yet a constraint — a slot table decides
/// which persistent manifold slot it lands in, and only a slot carries an accumulated impulse.
/// </summary>
/// <param name="SourceId">The identity of the surface the candidate came from.</param>
/// <param name="FeatureId">The identity of the body feature the candidate came from.</param>
/// <param name="Anchor">The contact point relative to the body's centre of mass, in world axes.</param>
/// <param name="Normal">The unit surface normal, in world axes, pointing out of the surface toward the body.</param>
/// <param name="Separation">The signed gap; negative means overlapping.</param>
internal readonly record struct ContactCandidate(
    int SourceId,
    int FeatureId,
    FixedVector3 Anchor,
    FixedVector3 Normal,
    FixedQ4816 Separation
) {
    /// <summary>Compares two candidates on a TOTAL key: source, feature, normal, separation, then anchor, each read as
    /// a raw carrier word.</summary>
    /// <param name="left">The first candidate.</param>
    /// <param name="right">The second candidate.</param>
    /// <returns>A negative value, zero, or a positive value as <paramref name="left"/> orders before, with, or after
    /// <paramref name="right"/>.</returns>
    /// <remarks>The key covers every declared field, so two candidates comparing equal are bitwise identical and
    /// therefore interchangeable — which is what makes the ordering independent of the order the generator emitted
    /// them in, without needing a stable sort.</remarks>
    internal static int Compare(ContactCandidate left, ContactCandidate right) {
        var order = left.SourceId.CompareTo(value: right.SourceId);

        if (order != 0) { return order; }

        order = left.FeatureId.CompareTo(value: right.FeatureId);

        if (order != 0) { return order; }

        order = left.Normal.X.Value.CompareTo(value: right.Normal.X.Value);

        if (order != 0) { return order; }

        order = left.Normal.Y.Value.CompareTo(value: right.Normal.Y.Value);

        if (order != 0) { return order; }

        order = left.Normal.Z.Value.CompareTo(value: right.Normal.Z.Value);

        if (order != 0) { return order; }

        order = left.Separation.Value.CompareTo(value: right.Separation.Value);

        if (order != 0) { return order; }

        order = left.Anchor.X.Value.CompareTo(value: right.Anchor.X.Value);

        if (order != 0) { return order; }

        order = left.Anchor.Y.Value.CompareTo(value: right.Anchor.Y.Value);

        if (order != 0) { return order; }

        return left.Anchor.Z.Value.CompareTo(value: right.Anchor.Z.Value);
    }

    /// <summary>Sorts a candidate list into canonical order in place.</summary>
    /// <param name="candidates">The candidates to order.</param>
    /// <remarks>An explicit insertion sort rather than a library sort: the ordering is part of the contract the D4 law
    /// proves, so it is written where it can be read, and its cost is irrelevant at manifold sizes.</remarks>
    internal static void Canonicalize(List<ContactCandidate> candidates) {
        ArgumentNullException.ThrowIfNull(argument: candidates);

        for (var index = 1; (index < candidates.Count); ++index) {
            var current = candidates[index];
            var slot = (index - 1);

            while ((slot >= 0) && (Compare(left: candidates[slot], right: current) > 0)) {
                candidates[(slot + 1)] = candidates[slot];
                --slot;
            }

            candidates[(slot + 1)] = current;
        }
    }
}
