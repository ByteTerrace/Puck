using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// Where each family of solver quantity sits in its 64-bit carrier. Mass and inertia span a wider window than any one
/// carrier holds once world scale and density are free, so the placement is a per-body-family parameter rather than a
/// library constant; length, velocity and impulse stay at <see cref="FixedQ4816"/>'s own scale because that is what the
/// vector and quaternion types already carry.
/// </summary>
/// <param name="InverseMass">The fraction bit count inverse mass is placed at.</param>
/// <param name="InverseInertia">The fraction bit count the body-local inverse inertia tensor is placed at.</param>
/// <param name="EffectiveMass">The fraction bit count a constraint's effective mass is placed at.</param>
public readonly record struct FixedRigidScales(int InverseMass, int InverseInertia, int EffectiveMass) {
    /// <summary>A placement that suits a body of roughly one to a hundred kilograms at metre scale.</summary>
    public static FixedRigidScales RoomScale => new(
        EffectiveMass: 32,
        InverseInertia: 40,
        InverseMass: 40
    );
}
/// <summary>
/// The rigid solver's controls: the step's rate and substep sequence, the contact softness and the carrier placement.
/// Friction and restitution are per contact pair (<see cref="FixedRigidManifold"/>), and a body's acceleration is its own
/// (<see cref="FixedRigidBody.Acceleration"/>), so neither is a world-wide option.
/// </summary>
public sealed class FixedRigidSolverOptions {
    /// <summary>The world's simulation rate in hertz.</summary>
    public int RateHz { get; init; } = 60;
    /// <summary>The number of temporal substeps per step.</summary>
    public int SubstepCount { get; init; } = 4;
    /// <summary>The number of biased solve iterations per substep.</summary>
    public int SolveIterations { get; init; } = 1;
    /// <summary>The number of unbiased relax iterations per substep.</summary>
    public int RelaxIterations { get; init; } = 1;
    /// <summary>The number of restitution iterations per step.</summary>
    public int RestitutionIterations { get; init; } = 1;
    /// <summary>The authored contact frequency, before the substep-derived clamp.</summary>
    public FixedQ4816 ContactHertz { get; init; } = FixedQ4816.FromInteger(value: 30L);
    /// <summary>The authored contact damping ratio.</summary>
    public FixedQ4816 ContactDampingRatio { get; init; } = FixedQ4816.FromInteger(value: 10L);
    /// <summary>The ceiling on the push-out speed a biased solve may request.</summary>
    public FixedQ4816 ContactSpeed { get; init; } = FixedQ4816.FromInteger(value: 3L);
    /// <summary>The closing normal speed below which a contact restitutes at zero rather than its pair's coefficient.</summary>
    public FixedQ4816 RestitutionThreshold { get; init; } = FixedQ4816.FromInteger(value: 1L);
    /// <summary>Whether stored impulses are re-applied at the head of each substep.</summary>
    public bool WarmStart { get; init; } = true;
    /// <summary>The largest accumulated impulse change, in raw Q48.16 units, an iteration may leave and still be read
    /// as converged. It is a READING, never a control: the solve always runs its full iteration budget, so changing
    /// this cannot change a trajectory.</summary>
    public long ConvergenceToleranceRaw { get; init; } = 64L;
    /// <summary>Where each family of solver quantity sits in its carrier.</summary>
    public FixedRigidScales Scales { get; init; } = FixedRigidScales.RoomScale;
}
/// <summary>
/// One rigid body as the solver sees it. There is no absolute position anywhere on this type: the solver reads the
/// per-step displacement it has itself accumulated and the contact anchors a candidate carried, and nothing else. The
/// owning fixture holds the absolute placement and applies the committed displacement at the end of a step. A body with
/// zero inverse mass is static: it never receives an impulse and never integrates, but its velocity still enters a
/// contact's relative velocity, so a kinematic body pushes without being pushed.
/// </summary>
public sealed class FixedRigidBody {
    /// <summary>The uniform acceleration (gravity and any applied field) the solver adds to a dynamic body's linear
    /// velocity each substep, in world axes.</summary>
    public FixedVector3 Acceleration { get; set; }
    /// <summary>The angular velocity in world axes.</summary>
    public FixedVector3 AngularVelocity { get; set; }
    /// <summary>Gets the rotation taking body axes to world axes as of the current substep.</summary>
    public FixedQuaternion CurrentOrientation => (DeltaRotation * Orientation);
    /// <summary>The displacement accumulated within the current step.</summary>
    public FixedVector3 DeltaPosition { get; set; }
    /// <summary>The body-local inverse inertia's <c>(0,0)</c> entry.</summary>
    public long InverseInertiaXX { get; set; }
    /// <summary>The body-local inverse inertia's <c>(0,1)</c> entry.</summary>
    public long InverseInertiaXY { get; set; }
    /// <summary>The body-local inverse inertia's <c>(0,2)</c> entry.</summary>
    public long InverseInertiaXZ { get; set; }
    /// <summary>The body-local inverse inertia's <c>(1,1)</c> entry.</summary>
    public long InverseInertiaYY { get; set; }
    /// <summary>The body-local inverse inertia's <c>(1,2)</c> entry.</summary>
    public long InverseInertiaYZ { get; set; }
    /// <summary>The body-local inverse inertia's <c>(2,2)</c> entry.</summary>
    public long InverseInertiaZZ { get; set; }
    /// <summary>The inverse mass raw, at <see cref="FixedRigidScales.InverseMass"/>; zero marks a static body.</summary>
    public long InverseMassRaw { get; set; }
    /// <summary>Gets whether the body responds to impulses.</summary>
    public bool IsDynamic => (InverseMassRaw > 0L);
    /// <summary>The linear velocity in world axes.</summary>
    public FixedVector3 LinearVelocity { get; set; }

    /// <summary>The rotation taking body axes to world axes at the head of the step.</summary>
    public FixedQuaternion Orientation { get; set; } = FixedQuaternion.Identity;
    /// <summary>The rotation accumulated within the current step.</summary>
    public FixedQuaternion DeltaRotation { get; set; } = FixedQuaternion.Identity;

    /// <summary>Folds the persistent simulation state into a running digest in a fixed order — linear velocity,
    /// angular velocity, then orientation — leaving the per-step accumulators outside the digest.</summary>
    /// <param name="digest">The running digest, advanced in place.</param>
    public void Fold(ref Fnv1aHash digest) {
        digest.Add(value: LinearVelocity.X.Value);
        digest.Add(value: LinearVelocity.Y.Value);
        digest.Add(value: LinearVelocity.Z.Value);
        digest.Add(value: AngularVelocity.X.Value);
        digest.Add(value: AngularVelocity.Y.Value);
        digest.Add(value: AngularVelocity.Z.Value);
        digest.Add(value: Orientation.X.Value);
        digest.Add(value: Orientation.Y.Value);
        digest.Add(value: Orientation.Z.Value);
        digest.Add(value: Orientation.W.Value);
    }
    /// <summary>Clears the per-step displacement accumulators.</summary>
    public void ResetStepAccumulators() {
        DeltaPosition = FixedVector3.Zero;
        DeltaRotation = FixedQuaternion.Identity;
    }
}
