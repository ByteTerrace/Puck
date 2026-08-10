using Puck.Dynamics.Spike.Tests.Core;
using Puck.Dynamics.Spike.Tests.Geometry;
using Puck.Maths;

namespace Puck.Dynamics.Spike.Tests.Fixtures;

/// <summary>
/// The harness one fixture runs in: it owns the ABSOLUTE placement, asks each surface for candidates, hands the
/// solver nothing but candidates, and applies the committed per-step displacement afterwards. The split is the point —
/// the solver has no member through which an absolute position could reach it.
/// </summary>
internal sealed class SpikeWorld {
    private readonly List<ContactCandidate> m_candidates = [];
    private readonly ISpikeSurface[] m_surfaces;
    private readonly SolverOptions m_options;
    private readonly FixedQ4816 m_stepSecondsCeiling;
    private readonly FixedQ4816 m_stepSecondsNearest;
    private int m_step;

    /// <summary>Creates a harness.</summary>
    /// <param name="options">The solver options.</param>
    /// <param name="body">The dynamic body.</param>
    /// <param name="pose">The body's absolute placement.</param>
    /// <param name="shape">The body's solid.</param>
    /// <param name="reach">The body's bounding radius about its centre of mass, used by the swept activation bound.</param>
    /// <param name="surfaces">The static surfaces, in the order they are asked for candidates.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    internal SpikeWorld(SolverOptions options, SpikeBody body, BodyPose pose, SpikeShape shape, FixedQ4816 reach, params ISpikeSurface[] surfaces) {
        ArgumentNullException.ThrowIfNull(argument: options);
        ArgumentNullException.ThrowIfNull(argument: body);
        ArgumentNullException.ThrowIfNull(argument: pose);
        ArgumentNullException.ThrowIfNull(argument: surfaces);

        m_options = options;
        m_surfaces = surfaces;
        m_stepSecondsNearest = (FixedQ4816.One / FixedQ4816.FromInteger(value: options.RateHz));
        m_stepSecondsCeiling = (FixedDirectedRounding.TryCeilingQuotient(
            numerator: 1L,
            fractionBitsNumerator: 0,
            denominator: options.RateHz,
            fractionBitsDenominator: 0,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var ceiling
        )
            ? FixedQ4816.FromRawBits(value: ceiling)
            : m_stepSecondsNearest);

        Body = body;
        Pose = pose;
        Shape = shape;
        Reach = reach;
        Slots = new();
        Solver = new(options: options);
        body.Orientation = pose.Orientation;
    }

    /// <summary>Gets the dynamic body.</summary>
    internal SpikeBody Body { get; }
    /// <summary>Gets the body's absolute placement.</summary>
    internal BodyPose Pose { get; }
    /// <summary>Gets the body's solid.</summary>
    internal SpikeShape Shape { get; }
    /// <summary>Gets the body's bounding radius about its centre of mass.</summary>
    internal FixedQ4816 Reach { get; }
    /// <summary>Gets the body's persistent manifold slots.</summary>
    internal ManifoldSlotTable Slots { get; }
    /// <summary>Gets the solver.</summary>
    internal RigidSolver Solver { get; }
    /// <summary>Gets the number of field samples the most recent step consumed.</summary>
    internal int LastStepSampleCount { get; private set; }
    /// <summary>Gets the number of candidates the most recent step generated.</summary>
    internal int LastStepCandidateCount { get; private set; }
    /// <summary>Gets the activation bound the most recent step used.</summary>
    internal FixedQ4816 LastStepActivationBound { get; private set; }
    /// <summary>Gets or sets a rewriting of the candidate list applied after generation and before the solver sees it
    /// — the D4 law's permutation hook, and nothing the production path would carry.</summary>
    internal Func<List<ContactCandidate>, List<ContactCandidate>>? Permutation { get; set; }

    /// <summary>Advances the world by one step.</summary>
    internal void Advance() {
        ++m_step;
        m_candidates.Clear();

        for (var index = 0; (index < m_surfaces.Length); ++index) {
            m_surfaces[index].ResetSampleCount();
        }

        var bound = ActivationBound();

        LastStepActivationBound = bound;

        for (var index = 0; (index < m_surfaces.Length); ++index) {
            m_surfaces[index].Generate(pose: Pose, shape: Shape, activationBound: bound, output: m_candidates);
        }

        if (Permutation is not null) {
            var permuted = Permutation(arg: m_candidates);

            m_candidates.Clear();
            m_candidates.AddRange(collection: permuted);
        }

        LastStepCandidateCount = m_candidates.Count;
        LastStepSampleCount = 0;

        for (var index = 0; (index < m_surfaces.Length); ++index) {
            LastStepSampleCount += m_surfaces[index].SampleCount;
        }

        Solver.Step(body: Body, slots: Slots, candidates: m_candidates, step: m_step);
        Pose.Center += Body.DeltaPosition;
        Pose.Orientation = (Body.DeltaRotation * Pose.Orientation).Normalize();
        Body.Orientation = Pose.Orientation;
    }

    /// <summary>Advances the world by a number of steps.</summary>
    /// <param name="count">The number of steps.</param>
    internal void Advance(int count) {
        for (var index = 0; (index < count); ++index) {
            Advance();
        }
    }

    /// <summary>Gets the fingerprint of everything a replay would have to reproduce.</summary>
    internal ulong Digest {
        get {
            var digest = SpikeArithmetic.DigestSeed;

            digest = SpikeArithmetic.Fold(digest: digest, value: Pose.Center.X.Value);
            digest = SpikeArithmetic.Fold(digest: digest, value: Pose.Center.Y.Value);
            digest = SpikeArithmetic.Fold(digest: digest, value: Pose.Center.Z.Value);
            digest = SpikeArithmetic.Fold(digest: digest, value: Pose.Orientation.X.Value);
            digest = SpikeArithmetic.Fold(digest: digest, value: Pose.Orientation.Y.Value);
            digest = SpikeArithmetic.Fold(digest: digest, value: Pose.Orientation.Z.Value);
            digest = SpikeArithmetic.Fold(digest: digest, value: Pose.Orientation.W.Value);
            digest = Body.Fold(digest: digest);

            return Slots.Fold(digest: digest, step: m_step);
        }
    }

    // Activation is decided from the CURRENT separation and a bound on how far the body can close before the step
    // ends; the previous step's separation decides nothing. Under Conservative every rounding in that bound is
    // directed UP, so the bound can only ever be too generous, never too tight.
    private FixedQ4816 ActivationBound() {
        if (m_options.Activation == SpeculativeActivation.CurrentOnly) {
            return m_options.ContactMargin;
        }

        if (m_options.Activation == SpeculativeActivation.NearestRounded) {
            var nearestSpin = (Body.AngularVelocity.Length * Reach);

            return (m_options.ContactMargin + ((Body.LinearVelocity.Length + nearestSpin) * m_stepSecondsNearest));
        }

        var spin = SpikeArithmetic.CeilingProduct(
            left: SpikeArithmetic.CeilingProduct(left: SpikeArithmetic.CeilingMagnitude(value: Body.AngularVelocity), right: Reach),
            right: m_stepSecondsCeiling
        );

        return (m_options.ContactMargin + SpikeArithmetic.CeilingProductSum(
            left: SpikeArithmetic.CeilingMagnitude(value: Body.LinearVelocity),
            right: m_stepSecondsCeiling,
            addend: spin
        ));
    }
}

/// <summary>Builds solver bodies from authored solids and one density, through the shared mass-property kernels.</summary>
internal static class SpikeBodies {
    /// <summary>The fraction bit count mass is derived at before it is inverted.</summary>
    internal const int MassFractionBitCount = 32;
    /// <summary>The fraction bit count inertia is derived at before it is inverted.</summary>
    internal const int InertiaFractionBitCount = 32;
    /// <summary>The fraction bit count a density is authored at.</summary>
    internal const int DensityFractionBitCount = 16;

    /// <summary>Builds a sphere body.</summary>
    /// <param name="radius">The radius.</param>
    /// <param name="density">The density.</param>
    /// <param name="scales">Where the inverse properties are placed.</param>
    /// <returns>The body.</returns>
    /// <exception cref="InvalidOperationException">A mass-property kernel refused the authored solid.</exception>
    internal static SpikeBody Sphere(FixedQ4816 radius, FixedQ4816 density, SpikeScales scales) {
        if (!FixedMassProperties.TrySphereBody(
            density: density.Value,
            fractionBitsDensity: DensityFractionBitCount,
            radius: radius.Value,
            fractionBitsLength: FixedQ4816.FractionBitCount,
            fractionBitsMass: MassFractionBitCount,
            fractionBitsInertia: InertiaFractionBitCount,
            mass: out var mass,
            inertia: out var inertia
        )) {
            throw new InvalidOperationException(message: "The sphere's mass properties are not representable at the requested placement.");
        }

        return Assemble(mass: mass, ixx: inertia, iyy: inertia, izz: inertia, scales: scales);
    }

    /// <summary>Builds a box body.</summary>
    /// <param name="halfExtents">The half-extents.</param>
    /// <param name="density">The density.</param>
    /// <param name="scales">Where the inverse properties are placed.</param>
    /// <returns>The body.</returns>
    /// <exception cref="InvalidOperationException">A mass-property kernel refused the authored solid.</exception>
    internal static SpikeBody Box(FixedVector3 halfExtents, FixedQ4816 density, SpikeScales scales) {
        if (!FixedMassProperties.TryBoxBody(
            density: density.Value,
            fractionBitsDensity: DensityFractionBitCount,
            halfX: halfExtents.X.Value,
            halfY: halfExtents.Y.Value,
            halfZ: halfExtents.Z.Value,
            fractionBitsLength: FixedQ4816.FractionBitCount,
            fractionBitsMass: MassFractionBitCount,
            fractionBitsInertia: InertiaFractionBitCount,
            mass: out var mass,
            ixx: out var ixx,
            iyy: out var iyy,
            izz: out var izz
        )) {
            throw new InvalidOperationException(message: "The box's mass properties are not representable at the requested placement.");
        }

        return Assemble(mass: mass, ixx: ixx, iyy: iyy, izz: izz, scales: scales);
    }

    /// <summary>Builds a capsule body whose segment lies along the body's X axis.</summary>
    /// <param name="radius">The radius.</param>
    /// <param name="centerDistance">The distance between the two cap centres.</param>
    /// <param name="density">The density.</param>
    /// <param name="scales">Where the inverse properties are placed.</param>
    /// <returns>The body.</returns>
    /// <exception cref="InvalidOperationException">A mass-property kernel refused the authored solid.</exception>
    /// <remarks>The kernel derives a capsule about its own <c>Y</c> axis, so the axial moment is assigned to
    /// <c>X</c> here and the perpendicular moment to the other two axes.</remarks>
    internal static SpikeBody CapsuleAlongX(FixedQ4816 radius, FixedQ4816 centerDistance, FixedQ4816 density, SpikeScales scales) {
        if (!FixedMassProperties.TryCapsuleBody(
            density: density.Value,
            fractionBitsDensity: DensityFractionBitCount,
            radius: radius.Value,
            centerDistance: centerDistance.Value,
            fractionBitsLength: FixedQ4816.FractionBitCount,
            fractionBitsMass: MassFractionBitCount,
            fractionBitsInertia: InertiaFractionBitCount,
            mass: out var mass,
            axial: out var axial,
            perpendicular: out var perpendicular
        )) {
            throw new InvalidOperationException(message: "The capsule's mass properties are not representable at the requested placement.");
        }

        return Assemble(mass: mass, ixx: axial, iyy: perpendicular, izz: perpendicular, scales: scales);
    }

    private static SpikeBody Assemble(long mass, long ixx, long iyy, long izz, SpikeScales scales) {
        if (!FixedMassProperties.TryInvertMass(mass: mass, fractionBitsMass: MassFractionBitCount, fractionBitsOut: scales.InverseMass, inverseMass: out var inverseMass)) {
            throw new InvalidOperationException(message: "The inverse mass is not representable at the requested placement.");
        }

        if (!FixedMassProperties.TryInvertInertia(
            ixx: ixx,
            iyy: iyy,
            izz: izz,
            ixy: 0L,
            ixz: 0L,
            iyz: 0L,
            fractionBitsInertia: InertiaFractionBitCount,
            fractionBitsOut: scales.InverseInertia,
            invXX: out var invXX,
            invYY: out var invYY,
            invZZ: out var invZZ,
            invXY: out var invXY,
            invXZ: out var invXZ,
            invYZ: out var invYZ
        )) {
            throw new InvalidOperationException(message: "The inverse inertia is not representable at the requested placement.");
        }

        return new() {
            InverseMassRaw = inverseMass,
            InverseInertiaXX = invXX,
            InverseInertiaYY = invYY,
            InverseInertiaZZ = invZZ,
            InverseInertiaXY = invXY,
            InverseInertiaXZ = invXZ,
            InverseInertiaYZ = invYZ,
        };
    }
}
