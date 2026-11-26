using Puck.Maths;

namespace Puck.World.Server;

/// <summary>The world's live orbits (<see cref="WorldGravityOrbit"/>): each tick it places every orbit at its closed
/// form for the end of the step and moves the body that rides it there.</summary>
/// <remarks>
/// <para>The invariant is that an orbit's position is a function of its clock, its compiled elements and its focus
/// alone, where the clock is the engine tick or, for an orbit that names one, a state row's value at that tick
/// (<see cref="WorldGravityOrbit.PhaseBy"/>). This type holds no state a checkpoint would need: a position is
/// computed from the clock each step, and whether a body rides is read from its state row each step, so a restored
/// world, a replayed one and one that skipped ticks all place an orbit where the closed form stands.</para>
/// <para>Orbits are placed in authored order. A focus attached to a body reads that body's pose as it stands, so an
/// orbit about a body riding another orbit is authored after it.</para>
/// <para>A riding body is moved as continuous motion (<see cref="WorldBody.Glide"/>), so solids, gravity areas and
/// lights attached to it ride along and a body standing on it is carried. A body found away from its orbit closes on
/// it at the orbit's authored join speed, which depends only on where the body and the orbit stand this tick.</para>
/// </remarks>
public sealed class WorldOrbitField {
    private readonly FixedWorldGravityOrbit[] m_orbits;
    private readonly FixedVector3[] m_positions;

    // A riding row's verdict: nonzero rides, and a row that does not read rides, as an orbit that names none does.
    private static bool Rides(in FixedWorldGravityOrbit orbit, WorldDefinition definition, ulong tick, ulong engineTick) => (
        (orbit.RidingRow is not { } row) ||
        !WorldStateReader.TryRead(
            definition: definition,
            engineTick: engineTick,
            key: null,
            rawValue: out var raw,
            row: out _,
            rowName: row,
            text: out _,
            tick: tick
        ) ||
        (raw is not { } value) ||
        (value != 0L)
    );

    // Where an orbit stands, relative to its focus, as of an engine tick: at its clock row's value then, or at the
    // tick's own phase for an orbit that names no row or whose row does not read.
    private static FixedVector3 OffsetOf(in FixedWorldGravityOrbit orbit, WorldDefinition definition, ulong tick, ulong engineTick) => (
        (
            (orbit.PhaseRow is { } row) &&
            WorldStateReader.TryRead(
                definition: definition,
                engineTick: engineTick,
                key: null,
                rawValue: out var raw,
                row: out var read,
                rowName: row,
                text: out _,
                tick: tick
            ) &&
            (read.Kind == Puck.State.CellKind.Fixed) &&
            (raw is { } value)
        )
        ? orbit.OffsetAt(clock: FixedQ4816.FromRawBits(value: value))
        : orbit.OffsetAt(engineTick: engineTick)
    );

    /// <summary>Places every orbit for the step that ends at <paramref name="engineTick"/> plus
    /// <paramref name="stepTicks"/>, and moves each riding body onto or toward its orbit.</summary>
    /// <param name="population">The live population whose bodies ride and whose poses attached foci follow.</param>
    /// <param name="definition">The authority's current definition and state binding.</param>
    /// <param name="tick">The authority tick being advanced.</param>
    /// <param name="engineTick">The engine tick at the start of the step.</param>
    /// <param name="stepTicks">The step's duration in engine ticks.</param>
    /// <exception cref="ArgumentNullException"><paramref name="population"/> or <paramref name="definition"/> is
    /// <see langword="null"/>.</exception>
    public void Advance(WorldPopulation population, WorldDefinition definition, ulong tick, ulong engineTick, ulong stepTicks) {
        ArgumentNullException.ThrowIfNull(argument: population);
        ArgumentNullException.ThrowIfNull(argument: definition);

        var evaluations = 0;
        var riders = 0;
        var joining = 0;

        for (var index = 0; (index < m_orbits.Length); index++) {
            ref readonly var orbit = ref m_orbits[index];
            var center = orbit.AuthoredCenter;

            if (
                (orbit.Attach is { } attach) &&
                !WorldPlacementAttachment.TryResolve(attach: attach, population: population, position: out center, reason: out _, yawRadians: out _)
            ) {
                // A focus riding an inactive body has no position this tick: the orbit keeps its last one.
                continue;
            }

            var offset = OffsetOf(definition: definition, engineTick: (engineTick + stepTicks), orbit: in orbit, tick: tick);
            var target = (center + offset);

            evaluations++;
            m_positions[index] = target;

            if (
                (((uint)orbit.BodyIndex) >= ((uint)population.Capacity)) ||
                !population.IsActive(index: orbit.BodyIndex) ||
                (population.EntryBody(index: orbit.BodyIndex) is not { } body) ||
                !Rides(definition: definition, engineTick: engineTick, orbit: in orbit, tick: tick)
            ) {
                continue;
            }

            riders++;

            if (orbit.JoinSpeed > FixedQ4816.Zero) {
                // What is left once the orbit's own motion over this step is taken out is how far the body stands
                // off the rails; it closes that by at most one step of the join speed.
                var carried = (body.FixedPosition + (offset - OffsetOf(definition: definition, engineTick: engineTick, orbit: in orbit, tick: tick)));
                var gap = (target - carried);
                var reach = FixedTickConversion.ShareOfStep(perSecond: orbit.JoinSpeed, stepTicks: stepTicks);

                if (gap.TryLengthSquared(squaredLength: out var gapSquared) && (gapSquared > (reach * reach))) {
                    joining++;
                    target = (carried + (gap.Normalize() * reach));
                }
            }

            body.Glide(position: target);
        }

        Statistics = new WorldOrbitStatistics(EvaluationCount: evaluations, JoiningCount: joining, OrbitCount: m_orbits.Length, RiderCount: riders);
    }
    /// <summary>Returns an orbit's world position as the last <see cref="Advance"/> placed it.</summary>
    /// <param name="index">The orbit's index in authored order.</param>
    /// <returns>The position; an orbit never advanced reads its focus's authored position.</returns>
    public FixedVector3 Position(int index) => m_positions[index];

    /// <summary>Initializes the field over a compiled gravity section's orbits.</summary>
    /// <param name="compiled">The compiled gravity section.</param>
    public WorldOrbitField(FixedWorldGravity compiled) {
        m_orbits = compiled.Orbits;
        m_positions = new FixedVector3[m_orbits.Length];

        for (var index = 0; (index < m_orbits.Length); index++) {
            m_positions[index] = m_orbits[index].AuthoredCenter;
        }
    }

    /// <summary>Gets the number of orbits.</summary>
    public int Count => m_orbits.Length;
    /// <summary>Gets the compiled orbits, in authored order.</summary>
    public ReadOnlySpan<FixedWorldGravityOrbit> Orbits => m_orbits;
    /// <summary>Gets the work the last <see cref="Advance"/> did.</summary>
    public WorldOrbitStatistics Statistics { get; private set; }
}
/// <summary>The deterministic work one orbit advance reported.</summary>
/// <param name="OrbitCount">The orbits the world authors.</param>
/// <param name="EvaluationCount">The closed-form positions computed, one per orbit whose focus resolved.</param>
/// <param name="RiderCount">The bodies moved along their orbits.</param>
/// <param name="JoiningCount">The riders still closing on their orbit at the join speed.</param>
public readonly record struct WorldOrbitStatistics(int OrbitCount, int EvaluationCount, int RiderCount, int JoiningCount);
