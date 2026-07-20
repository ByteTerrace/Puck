using System.Numerics;
using Puck.Authoring;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Queries;

namespace Puck.World.Server;

/// <summary>
/// The SDF-backed <see cref="IContactField"/> — R2's second provider behind the same seam the analytic
/// <see cref="WorldColliderSet"/> answers. It compiles the document's solid rows (boulders as smooth-union spheres,
/// slabs as boxes, solid screens as oriented-frame boxes, solid placements as reach-sized proxy spheres) plus the world
/// ground plane into ONE <see cref="SdfProgram"/> and reads it through a fixed-point <see cref="SdfFieldEvaluator"/>, so
/// the contact surface a body solves against IS the rendered geometry — smooth-union blends are solid where they are
/// drawn, and "up" is the field gradient rather than world <c>+Y</c> (a planetoid, an inverted ceiling, or the inside of
/// a sphere are all walkable with no new authoring vocabulary).
/// </summary>
/// <remarks>
/// <para>IMMUTABLE and per-revision: it holds no per-body state, so one instance is shared by reference across all 128
/// bodies and installing a rebuild is a single reference swap on <see cref="WorldServer"/>. The wrapped
/// <see cref="SdfFieldEvaluator"/> holds only a managed <c>CompiledInstruction[]</c> (no unmanaged handle), so a replaced
/// instance needs no disposal.</para>
/// <para>The "which op can be solid" ceiling is <see cref="SdfFieldEvaluator"/>'s warp-free excluded-op set:
/// <see cref="TryBuild"/> forwards the constructor's <see cref="ArgumentException"/> message verbatim as its reject
/// reason, so <see cref="WorldServer"/> turns an unsupported solid into a LOUD apply-time rejection instead of a
/// constructor throw at install time.</para>
/// <para>OQ-8 cost posture: only collider-bearing kits are solved at all. Every grounded step runs the ONE always-on
/// up tap — <see cref="Resolve"/> reads the field gradient (four <see cref="IFieldEvaluator.TryDistance"/> samples via
/// the tetrahedron) once per iteration to orient the capsule — plus one <see cref="IFieldEvaluator.TryDistance"/> per
/// capsule sphere; <see cref="ResolveSphere"/> takes the SECOND gradient tap (four more samples) only on ACTUAL
/// penetration. So the steady-state floor is <c>4 (up) + 2 (spheres)</c> samples per collider-bearing body per step,
/// rising by four per penetrating sphere. Broadphase/Lipschitz-reuse (the <c>BakedWorldQuery</c> tier) stays behind
/// this seam, unbuilt, until measurement demands it.</para>
/// </remarks>
internal sealed class WorldSolidField : IContactField {
    private static readonly FixedVector3 s_unitY = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);

    private readonly SdfFieldEvaluator m_evaluator;
    private readonly FixedQ4816 m_skin;
    private readonly FixedQ4816 m_groundedThreshold;
    private readonly FixedQ4816 m_gradientProbe;
    private readonly int m_iterations;

    private WorldSolidField(SdfFieldEvaluator evaluator, int instructionCount, FixedWorldCollision tuning) {
        m_evaluator = evaluator;
        InstructionCount = instructionCount;
        m_skin = tuning.ContactSkin;
        m_groundedThreshold = tuning.GroundedThreshold;
        m_gradientProbe = tuning.GradientProbe;
        m_iterations = Math.Max(val1: 1, val2: tuning.MaxIterations);
    }

    /// <summary>The compiled program's instruction count — the <c>world.collision.status</c> read-back (a rough size of
    /// the solid field the solver walks).</summary>
    public int InstructionCount { get; }

    /// <summary>Re-wraps this field's ALREADY-COMPILED program with fresh solver scalars, reusing the wrapped
    /// <see cref="SdfFieldEvaluator"/> (safe to share by reference — it holds only an immutable instruction array). A
    /// <c>SetCollision</c> edit touches only the collision tuning row, never the geometry the program bakes (scene rows,
    /// screens, placements, ground plane), so a slope/skin/probe/iteration tweak reuses the program instead of
    /// recompiling it. The result is a distinct instance (per-revision immutability) so the install-time reference swap
    /// still bumps the revision.</summary>
    /// <param name="tuning">The recompiled collision tuning to adopt.</param>
    /// <returns>A new field over the same evaluator with the new scalars.</returns>
    public WorldSolidField WithTuning(FixedWorldCollision tuning) =>
        new(evaluator: m_evaluator, instructionCount: InstructionCount, tuning: tuning);

    /// <summary>The field evaluator the <c>world.collision.probe</c> verb reads distance/material/gradient from, so the
    /// surface the simulation itself solves against is directly observable.</summary>
    public IFieldEvaluator Evaluator => m_evaluator;

    /// <summary>Builds the SDF contact field from a definition WITHOUT installing it, or reports the offending op by name.
    /// The build always includes the ground half-space; a definition with no solid rows still yields a walkable ground
    /// plane.</summary>
    /// <param name="definition">The world definition supplying the collision tuning, the solid rows, and the ground plane.</param>
    /// <param name="built">The built field on success; <see langword="null"/> on failure.</param>
    /// <param name="reason">The forwarded <see cref="SdfFieldEvaluator"/> reject reason when a solid names an op the
    /// warp-free evaluator cannot interpret; empty on success.</param>
    /// <returns><see langword="true"/> when the field compiled, <see langword="false"/> with a named reason otherwise.</returns>
    public static bool TryBuild(WorldDefinition definition, out WorldSolidField? built, out string reason) {
        built = null;
        reason = string.Empty;

        var tuning = FixedWorldCollision.Compile(collision: definition.Collision);
        var builder = new SdfProgramBuilder();

        // The ground half-space (material 0), the same seed the picker lays down first. The plane SDF is dot(p, +Y) +
        // offset = p.y - groundY, so the offset is the NEGATED ground height (the picker's y=0 ground is offset 0). A far
        // GroundY (a planetoid world pushes it to -1000) leaves it inert everywhere the geometry lives.
        _ = builder.Plane(normal: Vector3.UnitY, offset: -definition.Motion.GroundY, material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)));

        foreach (var row in definition.Scene.Rows) {
            if (row.Solid is not { } solid) {
                continue;
            }

            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder.Translate(offset: row.Center);
            _ = (row switch {
                // Unlike the picker's crisp per-row unions, the solid field melds each row with its authored Smooth radius
                // so the contact surface matches the RENDERED (smooth-union) one — the analytic provider cannot express
                // this blended surface.
                WorldSceneRow.Boulder boulder => builder.Sphere(radius: (boulder.Radius + solid.Margin), material: material, blend: SdfBlendOp.SmoothUnion, smooth: boulder.Smooth),
                WorldSceneRow.Slab slab => builder.Box(halfExtents: (slab.HalfExtents + new Vector3(value: solid.Margin)), round: slab.Round, material: material, blend: SdfBlendOp.SmoothUnion, smooth: slab.Smooth),
                _ => builder,
            });
            _ = builder.ResetPoint();
        }

        foreach (var screen in definition.Screens) {
            if (screen.Solid is not { } solid) {
                continue;
            }

            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
            // The same center derivation the frame source and picker bake: the geometry box sits one HalfDepth behind the
            // lit face along the face normal.
            var normal = Vector3.Normalize(value: Vector3.Cross(vector1: screen.Right, vector2: screen.Up));
            var center = (screen.Origin - (normal * screen.HalfDepth));

            _ = builder
                .Translate(offset: center)
                .Box(halfExtents: new Vector3(x: (screen.HalfWidth + solid.Margin), y: (screen.HalfHeight + solid.Margin), z: (screen.HalfDepth + solid.Margin)), round: screen.Round, material: material)
                .ResetPoint();
        }

        foreach (var placement in definition.Placements) {
            if ((placement.Solid is not { } solid) || (FindCreation(creations: definition.Creations, id: placement.CreationId) is not { } creation)) {
                continue;
            }

            // A reach-sized proxy sphere per stamp — verbatim the picker's formula. Replaying a creation's shape tree in
            // fixed point buys little and risks the evaluator's excluded-op ceiling; the proxy covers the stamp's core mass.
            var radius = MathF.Max(x: (CreationGeometry.Reach(document: creation.Document) * (placement.Scale * 0.5f)), y: 0.35f);
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder
                .Translate(offset: (placement.Position + new Vector3(x: 0f, y: radius, z: 0f)))
                .Sphere(radius: (radius + solid.Margin), material: material)
                .ResetPoint();
        }

        var program = builder.Build(buildInstanceGrid: false);
        SdfFieldEvaluator evaluator;

        try {
            evaluator = new SdfFieldEvaluator(program: program);
        } catch (ArgumentException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }

        built = new WorldSolidField(evaluator: evaluator, instructionCount: program.Instructions.Count, tuning: tuning);

        return true;
    }

    /// <summary>Reads the field at a point the way the solver does — the <c>world.collision.probe</c> diagnostic. The
    /// gradient uses the SAME authored probe step the resolver walks, so the printed direction is exactly the one the
    /// simulation reads.</summary>
    /// <param name="position">The world-space point to sample.</param>
    /// <param name="distance">The signed nearest-surface distance (negative inside geometry), when the field answered.</param>
    /// <param name="material">The nearest surface's material id, when the field answered.</param>
    /// <param name="gradient">The unit gradient (up direction), or <see cref="FixedVector3.Zero"/> on a degenerate query.</param>
    /// <returns><see langword="true"/> when the field has geometry to answer against.</returns>
    public bool Probe(in FixedVector3 position, out FixedQ4816 distance, out int material, out FixedVector3 gradient) {
        var coord = WorldCoord3.FromLocal(local: position);

        gradient = FixedVector3.Zero;

        if (!m_evaluator.TryDistance(position: coord, distance: out distance, material: out material)) {
            return false;
        }

        _ = m_evaluator.TryFieldGradient(position: coord, epsilon: m_gradientProbe, gradient: out gradient);

        return true;
    }

    /// <inheritdoc/>
    public bool Resolve(ref FixedVector3 position, ref FixedVector3 velocity, FixedQ4816 radius, FixedQ4816 height) {
        var grounded = false;

        for (var iteration = 0; (iteration < m_iterations); iteration++) {
            // The capsule's axis is the field up at the foot (the gradient). A degenerate query (a flat/self-canceling
            // point) is the SAFE failure — no push this iteration, the probe verb makes the degeneracy observable, and
            // world.collision.gradient is the fix — rather than a guessed direction.
            if (!TryUp(position: in position, up: out var up)) {
                break;
            }

            var lowerCenter = (position + (up * radius));
            var upperCenter = (position + (up * (height - radius)));
            var pushed = false;

            pushed |= ResolveSphere(position: ref position, velocity: ref velocity, center: lowerCenter, radius: radius, up: up, grounded: ref grounded);
            pushed |= ResolveSphere(position: ref position, velocity: ref velocity, center: upperCenter, radius: radius, up: up, grounded: ref grounded);

            if (!pushed) {
                break;
            }
        }

        return grounded;
    }

    /// <inheritdoc/>
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) {
        if (m_evaluator.TryFieldGradient(position: WorldCoord3.FromLocal(local: position), epsilon: m_gradientProbe, gradient: out var gradient)) {
            // The gradient is the direction of steepest distance INCREASE — the direction pointing directly away from the
            // nearest surface, i.e. UP. A grounded body's gravity opposes it and the standing test aligns against it.
            up = gradient;

            return true;
        }

        up = s_unitY;

        return false;
    }

    // Depenetrate one capsule sphere from the field: sample the distance at its center (the COMMON cost — one TryDistance),
    // and only on actual penetration take the gradient tap for the push direction. Grounds the body when the surface
    // normal's alignment with the body up clears the compiled walkable-slope threshold.
    private bool ResolveSphere(ref FixedVector3 position, ref FixedVector3 velocity, FixedVector3 center, FixedQ4816 radius, FixedVector3 up, ref bool grounded) {
        var coord = WorldCoord3.FromLocal(local: center);

        if (!m_evaluator.TryDistance(position: coord, distance: out var distance, material: out _)) {
            return false;
        }

        var minimum = (radius + m_skin);

        if (distance >= minimum) {
            return false;
        }

        // Penetration confirmed — NOW take the gradient tap for the surface normal.
        if (!m_evaluator.TryFieldGradient(position: coord, epsilon: m_gradientProbe, gradient: out var normal)) {
            return false;
        }

        position += (normal * (minimum - distance));

        if (FixedVector3.Dot(left: normal, right: up) >= m_groundedThreshold) {
            grounded = true;
        }

        var into = FixedVector3.Dot(left: velocity, right: normal);

        if (into < FixedQ4816.Zero) {
            velocity -= (normal * into);
        }

        return true;
    }

    // Resolve a placement's referenced creation by id (a straight document lookup — the server layer avoids reaching into
    // the client's WorldPlacementStamper for one loop).
    private static WorldCreation? FindCreation(IReadOnlyList<WorldCreation> creations, string id) {
        foreach (var creation in creations) {
            if (string.Equals(a: creation.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return creation;
            }
        }

        return null;
    }
}
