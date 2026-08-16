using System.Numerics;

using Puck.Physics.Tests.Geometry;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

namespace Puck.Physics.Tests.Fixtures;

/// <summary>The six deliberately non-planar scenarios these fixtures prove the solver's mechanics on.</summary>
internal static class SpikeFixtures {
    /// <summary>The identity of the floor half-space.</summary>
    internal const int FloorSourceId = 1;
    /// <summary>The identity of the wall half-space.</summary>
    internal const int WallSourceId = 2;
    /// <summary>The identity of the field-described slab.</summary>
    internal const int FieldSourceId = 3;
    /// <summary>The identity of the analytic slab.</summary>
    internal const int SlabSourceId = 4;

    private static FixedVector3 AxisX => new(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
    private static FixedVector3 AxisY => new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);

    /// <summary>Creates a vector from three authored components.</summary>
    /// <param name="x">The first component.</param>
    /// <param name="y">The second component.</param>
    /// <param name="z">The third component.</param>
    /// <returns>The vector.</returns>
    internal static FixedVector3 Vector(double x, double y, double z) =>
        new(X: FixedQ4816.FromDouble(value: x), Y: FixedQ4816.FromDouble(value: y), Z: FixedQ4816.FromDouble(value: z));
    /// <summary>Fixture 1 — a sphere resting in a floor-wall corner: two persistent contacts with distinct normals.</summary>
    /// <param name="options">The solver options; the fixture supplies its own gravity and side load when the caller
    /// leaves them at their defaults through <see cref="CornerOptions"/>.</param>
    /// <returns>The world.</returns>
    internal static SpikeWorld Corner(FixedRigidSolverOptions options) {
        ArgumentNullException.ThrowIfNull(argument: options);

        var radius = FixedQ4816.FromDouble(value: 0.5d);
        var body = SpikeBodies.Sphere(radius: radius, density: FixedQ4816.FromInteger(value: 20L), scales: options.Scales);
        var pose = new BodyPose { Center = Vector(x: 0.62d, y: 0.62d, z: 0d), };

        return new(
            options: options,
            body: body,
            pose: pose,
            shape: SpikeShape.Sphere(radius: radius),
            reach: radius,
            new HalfSpaceSurface(sourceId: FloorSourceId, normal: AxisY, offset: FixedQ4816.Zero),
            new HalfSpaceSurface(sourceId: WallSourceId, normal: AxisX, offset: FixedQ4816.Zero)
        );
    }
    /// <summary>The options fixture 1 is measured under: gravity plus a steady load into the wall, so BOTH contacts
    /// stay live rather than one of them idling.</summary>
    /// <param name="rateHz">The simulation rate.</param>
    /// <param name="substepCount">The substep count.</param>
    /// <param name="solveIterations">The biased solve iteration budget per substep.</param>
    /// <param name="warmStart">Whether stored impulses are re-applied at the head of each substep.</param>
    /// <param name="compositeIdentity">Whether candidates are associated by composite identity plus geometry.</param>
    /// <returns>The options.</returns>
    internal static FixedRigidSolverOptions CornerOptions(int rateHz, int substepCount, int solveIterations = 1, bool warmStart = true, bool compositeIdentity = true) =>
        new() {
            RateHz = rateHz,
            SubstepCount = substepCount,
            SolveIterations = solveIterations,
            WarmStart = warmStart,
            CompositeIdentity = compositeIdentity,
            AppliedAcceleration = Vector(x: -4d, y: 0d, z: 0d),
        };
    /// <summary>Fixture 2 — a capsule whose WAIST rests on a slab both end spheres clear, described by a standalone
    /// signed-distance program.</summary>
    /// <param name="options">The solver options.</param>
    /// <param name="mode">How the generator looks for the capsule's witness.</param>
    /// <param name="surface">The field surface, so a caller can read its sample counters and endpoint separations.</param>
    /// <returns>The world.</returns>
    internal static SpikeWorld CapsuleWaist(FixedRigidSolverOptions options, CapsuleWitnessMode mode, out FieldSurface surface) {
        ArgumentNullException.ThrowIfNull(argument: options);

        var radius = FixedQ4816.FromDouble(value: 0.25d);
        var half = Vector(x: 0.9d, y: 0d, z: 0d);
        var body = SpikeBodies.CapsuleAlongX(
            radius: radius,
            centerDistance: FixedQ4816.FromDouble(value: 1.8d),
            density: FixedQ4816.FromInteger(value: 40L),
            scales: options.Scales
        );

        surface = new(
            sourceId: FieldSourceId,
            field: new SdfFieldEvaluator(program: SlabProgram()),
            mode: mode,
            scanSegments: 12,
            refinementSteps: 4
        );

        return new(
            options: options,
            body: body,
            pose: new() { Center = Vector(x: 0d, y: 0.42d, z: 0d), },
            shape: SpikeShape.Capsule(radius: radius, segmentHalf: half),
            reach: FixedQ4816.FromDouble(value: 1.15d),
            surface
        );
    }
    /// <summary>Fixture 3 — a box dropped with spin onto a plane, settling without jitter at zero restitution.</summary>
    /// <param name="options">The solver options.</param>
    /// <returns>The world.</returns>
    internal static SpikeWorld RotatingBox(FixedRigidSolverOptions options) {
        ArgumentNullException.ThrowIfNull(argument: options);

        var halfExtents = Vector(x: 0.4d, y: 0.25d, z: 0.3d);
        var body = SpikeBodies.Box(halfExtents: halfExtents, density: FixedQ4816.FromInteger(value: 50L), scales: options.Scales);

        body.LinearVelocity = Vector(x: 0d, y: -1d, z: 0d);
        body.AngularVelocity = Vector(x: 0d, y: 0d, z: 0.9d);

        return new(
            options: options,
            body: body,
            pose: new() { Center = Vector(x: 0d, y: 0.34d, z: 0d), },
            shape: SpikeShape.Box(halfExtents: halfExtents),
            reach: FixedQ4816.FromDouble(value: 0.5545d),
            new HalfSpaceSurface(sourceId: FloorSourceId, normal: AxisY, offset: FixedQ4816.Zero)
        );
    }
    /// <summary>Fixture 4 — a body outside every margin last step and through the surface this step, caught by the
    /// speculative constraint alone.</summary>
    /// <param name="options">The solver options.</param>
    /// <param name="height">The starting height of the sphere's centre.</param>
    /// <param name="downwardSpeed">The starting downward speed.</param>
    /// <returns>The world.</returns>
    internal static SpikeWorld HighSpeedApproach(FixedRigidSolverOptions options, double height, double downwardSpeed) {
        ArgumentNullException.ThrowIfNull(argument: options);

        var radius = FixedQ4816.FromDouble(value: 0.1d);
        var body = SpikeBodies.Sphere(radius: radius, density: FixedQ4816.FromInteger(value: 100L), scales: options.Scales);

        body.LinearVelocity = Vector(x: 0d, y: -downwardSpeed, z: 0d);

        return new(
            options: options,
            body: body,
            pose: new() { Center = Vector(x: 0d, y: height, z: 0d), },
            shape: SpikeShape.Sphere(radius: radius),
            reach: radius,
            new HalfSpaceSurface(sourceId: FloorSourceId, normal: AxisY, offset: FixedQ4816.Zero)
        );
    }
    /// <summary>Fixture 5 — a sphere INJECTED past a thin slab's midplane, whose nearest surface is on the wrong
    /// side.</summary>
    /// <param name="options">The solver options.</param>
    /// <returns>The world.</returns>
    internal static SpikeWorld DeepOverlap(FixedRigidSolverOptions options) {
        ArgumentNullException.ThrowIfNull(argument: options);

        var radius = FixedQ4816.FromDouble(value: 0.5d);
        var body = SpikeBodies.Sphere(radius: radius, density: FixedQ4816.FromInteger(value: 20L), scales: options.Scales);

        body.EscapeDirection = AxisY;

        return new(
            options: options,
            body: body,
            pose: new() { Center = Vector(x: 0d, y: -0.02d, z: 0d), },
            shape: SpikeShape.Sphere(radius: radius),
            reach: radius,
            new SlabSurface(
                sourceId: SlabSourceId,
                axis: AxisY,
                lower: FixedQ4816.FromDouble(value: -0.1d),
                upper: FixedQ4816.FromDouble(value: 0.1d)
            )
        );
    }
    /// <summary>Fixture 6 — a box wedged into a floor-wall corner, so one step generates a candidate list long enough
    /// for permutation to matter.</summary>
    /// <param name="options">The solver options.</param>
    /// <returns>The world.</returns>
    internal static SpikeWorld BoxInCorner(FixedRigidSolverOptions options) {
        ArgumentNullException.ThrowIfNull(argument: options);

        var halfExtents = Vector(x: 0.4d, y: 0.25d, z: 0.3d);
        var body = SpikeBodies.Box(halfExtents: halfExtents, density: FixedQ4816.FromInteger(value: 50L), scales: options.Scales);

        body.AngularVelocity = Vector(x: 0d, y: 0d, z: 0.4d);

        return new(
            options: options,
            body: body,
            pose: new() { Center = Vector(x: 0.46d, y: 0.31d, z: 0d), },
            shape: SpikeShape.Box(halfExtents: halfExtents),
            reach: FixedQ4816.FromDouble(value: 0.5545d),
            new HalfSpaceSurface(sourceId: FloorSourceId, normal: AxisY, offset: FixedQ4816.Zero),
            new HalfSpaceSurface(sourceId: WallSourceId, normal: AxisX, offset: FixedQ4816.Zero)
        );
    }
    /// <summary>The options fixture 6 is measured under.</summary>
    /// <param name="rateHz">The simulation rate.</param>
    /// <param name="substepCount">The substep count.</param>
    /// <param name="solveIterations">The biased solve iteration budget per substep.</param>
    /// <param name="warmStart">Whether stored impulses are re-applied at the head of each substep.</param>
    /// <param name="canonicalOrder">Whether candidates are canonically ordered before association.</param>
    /// <returns>The options.</returns>
    internal static FixedRigidSolverOptions BoxInCornerOptions(int rateHz, int substepCount, int solveIterations = 1, bool warmStart = true, bool canonicalOrder = true) =>
        new() {
            RateHz = rateHz,
            SubstepCount = substepCount,
            SolveIterations = solveIterations,
            WarmStart = warmStart,
            CanonicalOrder = canonicalOrder,
            AppliedAcceleration = Vector(x: -3d, y: 0d, z: 0d),
        };

    // The whole SDF program fixture 2 collides against: one axis-aligned slab, narrow in Y, so a capsule lying along
    // X touches it at the waist while both end spheres stay clear.
    private static SdfProgram SlabProgram() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.5f, y: 0.5f, z: 0.5f)));

        builder.Box(halfExtents: new Vector3(x: 0.55f, y: 0.1f, z: 0.6f), round: 0f, material: material);

        return builder.Build();
    }
}
