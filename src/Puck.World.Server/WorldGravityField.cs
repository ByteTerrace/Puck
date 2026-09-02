using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>
/// The world's live gravitational field: the compiled sources, the selected solver, and the per-tick acceleration it
/// produces for every body.
/// </summary>
/// <remarks>
/// <para>One solve per tick, before any body advances, so every body reads the same snapshot — the same relationship
/// <c>WorldColliderSet.RefreshAttached</c> already gives attached colliders. A body reads its own answer by entity
/// index through <see cref="TryAcceleration"/>.</para>
/// <para>Sources are the authored attractors plus every massive body; targets are the bodies. An attractor rides a
/// placement's authored transform and cannot move, so the acceleration computed for one is discarded. Bodies are both,
/// which is what lets two massive bodies attract each other.</para>
/// <para>Body order is the entity index, so the solver's input order is the population's own stable order and the
/// answer does not depend on activation history.</para>
/// <para>Matching local areas fold over the global answer in their compiled priority/authored order. Participation is
/// tracked independently of vector magnitude: authored zero is a result, while an unmatched target in an area-only
/// field retains ordinary kit gravity.</para>
/// </remarks>
public sealed class WorldGravityField {
    private readonly GravityBody[] m_bodies;
    private readonly FixedVector3[] m_accelerations;
    private readonly bool[] m_areaActive;
    private readonly FixedVector3[] m_areaPositions;
    private readonly FixedQuaternion[] m_areaRotations;
    private readonly FixedWorldGravity m_compiled;
    private readonly int[] m_entityBySlot;
    private readonly bool[] m_participating;
    private readonly int[] m_slotByEntity;
    private readonly IGravitySolver m_solver;

    private int m_bodyCount;

    // Composition is the one place independently valid accelerations meet. Saturating componentwise prevents an
    // overlap from wrapping toward the opposite direction; the fixed extreme is deterministic and Replace can still
    // reset it before a later Combine.
    private static FixedVector3 Compose(FixedVector3 left, FixedVector3 right) => new(
        X: FixedSaturate.Add(left: left.X, right: right.X),
        Y: FixedSaturate.Add(left: left.Y, right: right.Y),
        Z: FixedSaturate.Add(left: left.Z, right: right.Z)
    );

    /// <summary>Initializes the field for a fixed population capacity.</summary>
    /// <param name="compiled">The compiled gravity section.</param>
    /// <param name="capacity">The population capacity; the largest number of body targets one solve can carry.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public WorldGravityField(FixedWorldGravity compiled, int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: capacity);

        m_compiled = compiled;
        m_solver = GravitySolvers.Create(kind: compiled.Kind);
        m_accelerations = new FixedVector3[capacity];
        m_areaActive = new bool[compiled.Areas.Length];
        m_areaPositions = new FixedVector3[compiled.Areas.Length];
        m_areaRotations = new FixedQuaternion[compiled.Areas.Length];
        m_bodies = new GravityBody[(compiled.Attractors.Length + capacity)];
        m_entityBySlot = new int[capacity];
        m_participating = new bool[capacity];
        m_slotByEntity = new int[capacity];

        Array.Fill(
            array: m_slotByEntity,
            value: -1
        );

        for (var index = 0; (index < compiled.Areas.Length); index++) {
            var area = compiled.Areas[index];

            m_areaPositions[index] = area.AuthoredPosition;
            m_areaRotations[index] = area.AuthoredRotation;
            m_areaActive[index] = (area.Attach is null);
        }
    }

    /// <summary>Gets the compiled section this field solves.</summary>
    public FixedWorldGravity Compiled => m_compiled;
    /// <summary>Gets a value indicating whether a global or bounded local field is authored.</summary>
    public bool IsActive => m_compiled.IsActive;
    /// <summary>Gets the work counters the last solve reported.</summary>
    public GravitySolveStatistics Statistics { get; private set; }
    /// <summary>Gets the local-area work counters from the last solve.</summary>
    public WorldGravityAreaStatistics AreaStatistics { get; private set; }

    /// <summary>Returns the acceleration solved for a body this tick.</summary>
    /// <param name="entityIndex">The body's entity index.</param>
    /// <param name="acceleration">The solved acceleration. Zero may be an authored participating answer or the
    /// default written when the body took no part; the return value distinguishes those cases.</param>
    /// <returns><see langword="true"/> when the body participated in the active authored field. The acceleration may
    /// be zero after a Replace pocket, exact cancellation, or at a radial area's centre.</returns>
    public bool TryAcceleration(int entityIndex, out FixedVector3 acceleration) {
        acceleration = FixedVector3.Zero;

        if (
            (((uint)entityIndex) >= ((uint)m_slotByEntity.Length)) ||
            (m_slotByEntity[entityIndex] < 0) ||
            !m_participating[entityIndex]
        ) {
            return false;
        }

        acceleration = m_accelerations[entityIndex];

        return true;
    }
    /// <summary>Refreshes areas riding attached placements from the current authoritative body poses.</summary>
    /// <param name="population">The live population used by the established placement-attachment resolver.</param>
    public void RefreshAttachedAreas(WorldPopulation population) {
        ArgumentNullException.ThrowIfNull(argument: population);

        for (var index = 0; (index < m_compiled.Areas.Length); index++) {
            var area = m_compiled.Areas[index];

            if (area.Attach is not { } attach) {
                continue;
            }

            if (!WorldPlacementAttachment.TryResolve(
                attach: attach,
                population: population,
                position: out var position,
                reason: out _,
                yawRadians: out var yaw
            )) {
                m_areaActive[index] = false;

                continue;
            }

            m_areaActive[index] = true;
            m_areaPositions[index] = position;
            m_areaRotations[index] = FixedQuaternion.FromAxisAngle(
                angle: yaw,
                axis: new FixedVector3(
                    X: FixedQ4816.Zero,
                    Y: FixedQ4816.One,
                    Z: FixedQ4816.Zero
                )
            );
        }
    }
    /// <summary>Solves the field for this tick from the supplied body targets.</summary>
    /// <param name="targets">Each participating body's entity index, world position, and gravitational mass, in entity
    /// order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="targets"/> is <see langword="null"/>.</exception>
    public void Solve(IReadOnlyList<WorldGravityTarget> targets) {
        ArgumentNullException.ThrowIfNull(targets);

        Array.Fill(
            array: m_slotByEntity,
            value: -1
        );
        Array.Fill(
            array: m_participating,
            value: false
        );

        if (!IsActive) {
            Statistics = default;
            AreaStatistics = default;
            m_bodyCount = 0;

            return;
        }

        Statistics = default;
        AreaStatistics = default;

        if (!m_compiled.HasGlobalSolve) {
            // A uniform/area-only field needs no global solve. Every valid target still participates so a matching
            // zero Replace remains authored zero gravity rather than falling back to kit gravity.
            m_bodyCount = 0;

            foreach (var target in targets) {
                if (((uint)target.EntityIndex) < ((uint)m_slotByEntity.Length)) {
                    m_slotByEntity[target.EntityIndex] = 0;
                    m_accelerations[target.EntityIndex] = m_compiled.Uniform;
                    m_participating[target.EntityIndex] = (m_compiled.Uniform != FixedVector3.Zero);
                }
            }

            ApplyAreas(targets: targets);

            return;
        }

        var attractorCount = m_compiled.Attractors.Length;

        Array.Copy(
            destinationArray: m_bodies,
            length: attractorCount,
            sourceArray: m_compiled.Attractors,
            sourceIndex: 0,
            destinationIndex: 0
        );

        var count = attractorCount;

        foreach (var target in targets) {
            if (
                (((uint)target.EntityIndex) >= ((uint)m_slotByEntity.Length)) ||
                (count >= m_bodies.Length)
            ) {
                continue;
            }

            m_entityBySlot[(count - attractorCount)] = target.EntityIndex;
            m_slotByEntity[target.EntityIndex] = (count - attractorCount);
            m_participating[target.EntityIndex] = true;
            m_bodies[count] = new GravityBody(
                Mass: target.Mass,
                Position: target.Position
            );
            count++;
        }

        m_bodyCount = (count - attractorCount);

        Span<FixedVector3> solved = new FixedVector3[count];

        Statistics = m_solver.ComputeAccelerations(
            accelerations: solved,
            bodies: m_bodies.AsSpan(
                length: count,
                start: 0
            ),
            parameters: m_compiled.Parameters
        );

        // The attractors' own accelerations are discarded: they ride a placement transform and never move.
        for (var slot = 0; (slot < m_bodyCount); slot++) {
            m_accelerations[m_entityBySlot[slot]] = Compose(
                left: solved[(attractorCount + slot)],
                right: m_compiled.Uniform
            );
        }

        ApplyAreas(targets: targets);
    }

    private void ApplyAreas(IReadOnlyList<WorldGravityTarget> targets) {
        if (m_compiled.Areas.Length == 0) {
            return;
        }

        var evaluations = 0;
        var matches = 0;
        var activeAreas = 0;

        foreach (var active in m_areaActive) {
            if (active) {
                activeAreas++;
            }
        }

        foreach (var target in targets) {
            if (((uint)target.EntityIndex) >= ((uint)m_slotByEntity.Length)) {
                continue;
            }

            var acceleration = m_accelerations[target.EntityIndex];

            for (var index = 0; (index < m_compiled.Areas.Length); index++) {
                if (!m_areaActive[index]) {
                    continue;
                }

                evaluations++;
                var area = m_compiled.Areas[index];

                if (!area.Contains(
                    center: m_areaPositions[index],
                    point: target.Position,
                    rotation: m_areaRotations[index]
                )) {
                    continue;
                }

                matches++;
                m_participating[target.EntityIndex] = true;
                var contribution = area.AccelerationAt(
                    center: m_areaPositions[index],
                    point: target.Position,
                    rotation: m_areaRotations[index]
                );

                acceleration = ((area.Mode == WorldGravityAreaMode.Replace)
                    ? contribution
                    : Compose(
                        left: acceleration,
                        right: contribution
                    )
                );
            }

            m_accelerations[target.EntityIndex] = acceleration;
        }

        AreaStatistics = new WorldGravityAreaStatistics(
            ActiveAreaCount: activeAreas,
            EvaluationCount: evaluations,
            MatchCount: matches,
            TargetCount: targets.Count
        );
    }
}
/// <summary>The deterministic local-area work reported by one gravity solve.</summary>
/// <param name="ActiveAreaCount">The areas with a resolved placement pose this tick.</param>
/// <param name="TargetCount">The participating population targets considered for local areas.</param>
/// <param name="EvaluationCount">The body-area analytic bound tests performed.</param>
/// <param name="MatchCount">The matching contributions folded into body answers.</param>
public readonly record struct WorldGravityAreaStatistics(int ActiveAreaCount, int TargetCount, int EvaluationCount, int MatchCount);
/// <summary>One body participating in a gravity solve.</summary>
/// <param name="EntityIndex">The body's entity index, which is also the order its answer is read back by.</param>
/// <param name="Position">The body's world position.</param>
/// <param name="Mass">The body's gravitational mass; zero makes it a target that pulls nothing.</param>
public readonly record struct WorldGravityTarget(int EntityIndex, FixedVector3 Position, FixedQ4816 Mass);
