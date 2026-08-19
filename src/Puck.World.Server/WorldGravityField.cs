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
/// </remarks>
public sealed class WorldGravityField {
    private readonly GravityBody[] m_bodies;
    private readonly FixedVector3[] m_accelerations;
    private readonly FixedWorldGravity m_compiled;
    private readonly int[] m_entityBySlot;
    private readonly int[] m_slotByEntity;
    private readonly IGravitySolver m_solver;

    private int m_bodyCount;

    /// <summary>Initializes the field for a fixed population capacity.</summary>
    /// <param name="compiled">The compiled gravity section.</param>
    /// <param name="capacity">The population capacity; the largest number of body targets one solve can carry.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public WorldGravityField(FixedWorldGravity compiled, int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: capacity);

        m_compiled = compiled;
        m_solver = GravitySolvers.Create(kind: compiled.Kind);
        m_accelerations = new FixedVector3[capacity];
        m_bodies = new GravityBody[(compiled.Attractors.Length + capacity)];
        m_entityBySlot = new int[capacity];
        m_slotByEntity = new int[capacity];

        Array.Fill(
            array: m_slotByEntity,
            value: -1
        );
    }

    /// <summary>Gets the compiled section this field solves.</summary>
    public FixedWorldGravity Compiled => m_compiled;
    /// <summary>Gets a value indicating whether a solve can produce a nonzero acceleration.</summary>
    public bool IsActive => m_compiled.IsActive;
    /// <summary>Gets the work counters the last solve reported.</summary>
    public GravitySolveStatistics Statistics { get; private set; }

    /// <summary>Returns the acceleration solved for a body this tick.</summary>
    /// <param name="entityIndex">The body's entity index.</param>
    /// <param name="acceleration">The solved acceleration, or zero when the body took no part in this tick's solve.</param>
    /// <returns><see langword="true"/> when the body has a nonzero solved acceleration.</returns>
    public bool TryAcceleration(int entityIndex, out FixedVector3 acceleration) {
        acceleration = FixedVector3.Zero;

        if (
            (((uint)entityIndex) >= ((uint)m_slotByEntity.Length)) ||
            (m_slotByEntity[entityIndex] < 0)
        ) {
            return false;
        }

        acceleration = m_accelerations[entityIndex];

        return (acceleration != FixedVector3.Zero);
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

        if (!IsActive) {
            Statistics = default;
            m_bodyCount = 0;

            return;
        }

        if (!m_compiled.HasAttractors) {
            // A uniform-only field needs no solve: every body reads the same constant.
            Statistics = default;
            m_bodyCount = 0;

            foreach (var target in targets) {
                if (((uint)target.EntityIndex) < ((uint)m_slotByEntity.Length)) {
                    m_slotByEntity[target.EntityIndex] = 0;
                    m_accelerations[target.EntityIndex] = m_compiled.Uniform;
                }
            }

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
            m_accelerations[m_entityBySlot[slot]] = (solved[(attractorCount + slot)] + m_compiled.Uniform);
        }
    }
}
/// <summary>One body participating in a gravity solve.</summary>
/// <param name="EntityIndex">The body's entity index, which is also the order its answer is read back by.</param>
/// <param name="Position">The body's world position.</param>
/// <param name="Mass">The body's gravitational mass; zero makes it a target that pulls nothing.</param>
public readonly record struct WorldGravityTarget(int EntityIndex, FixedVector3 Position, FixedQ4816 Mass);
