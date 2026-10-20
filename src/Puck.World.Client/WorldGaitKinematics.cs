using System.Numerics;
using Puck.SdfVm.Views;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>
/// The per-body motion a creation's directional and follower driver signals read, kept beside the driver phases on the
/// stamp pool's registration: the body's eased planar velocity in its own frame and in the world, the travel direction
/// and planar acceleration derived from them, and one second-order follower per driver that names a
/// <see cref="CreationDriverDocument.Dynamics"/> row. Presentation-only floats; nothing here re-enters the simulation.
/// </summary>
public sealed class WorldGaitKinematics {
    private const float TwoPi = (2f * MathF.PI);

    private readonly SecondOrderResponse[] m_responses = new SecondOrderResponse[CreationDocument.MaxDrivers];
    private readonly bool[] m_follows = new bool[CreationDocument.MaxDrivers];
    private readonly float[] m_value = new float[CreationDocument.MaxDrivers];
    private readonly float[] m_velocity = new float[CreationDocument.MaxDrivers];
    private readonly float[] m_previousTarget = new float[CreationDocument.MaxDrivers];
    private readonly bool[] m_seeded = new bool[CreationDocument.MaxDrivers];
    private Vector2 m_localVelocity;
    private Vector2 m_worldVelocity;
    private IReadOnlyList<CreationDriverDocument>? m_resolvedDrivers;
    private IReadOnlyList<DynamicsRow>? m_resolvedRows;

    /// <summary>Gets the eased planar travel direction relative to the rendered facing, radians in (−π, π]; positive
    /// travels to the body's left. Holds its last value while the body stands still.</summary>
    public float TravelDirection { get; private set; }
    /// <summary>Gets the eased planar acceleration across the rendered facing, metres per second squared, positive
    /// toward the body's left.</summary>
    public float LateralAcceleration { get; private set; }
    /// <summary>Gets the eased planar acceleration along the rendered facing, metres per second squared, positive
    /// forward.</summary>
    public float ForwardAcceleration { get; private set; }

    /// <summary>Clears the eased motion and every follower, so a reused body slot starts at rest facing forward.</summary>
    public void Reset() {
        m_localVelocity = Vector2.Zero;
        m_worldVelocity = Vector2.Zero;
        TravelDirection = 0f;
        LateralAcceleration = 0f;
        ForwardAcceleration = 0f;
        Array.Clear(array: m_seeded);
    }
    /// <summary>Turns the eased world-frame velocity with a rotation of the world about up — an in-place crossing
    /// that turns its traveller — so the acceleration signals read no turn the body did not make. The body-frame
    /// velocity and every driver follower are unchanged by it.</summary>
    /// <param name="rotation">The unit rotation.</param>
    public void Carry(Quaternion rotation) {
        var turned = Vector3.Transform(
            rotation: rotation,
            value: new Vector3(
                x: m_worldVelocity.X,
                y: 0f,
                z: m_worldVelocity.Y
            )
        );

        m_worldVelocity = new Vector2(
            x: turned.X,
            y: turned.Z
        );
    }
    /// <summary>Resolves each driver's <see cref="CreationDriverDocument.Dynamics"/> row into its follower response.
    /// Re-resolves only when the driver list or the row list is a different instance from the last call; a driver
    /// naming no row, or a row the list does not hold, tracks its signal exactly.</summary>
    /// <param name="drivers">The creation's declared drivers, or <see langword="null"/> for none.</param>
    /// <param name="rows">The world's declared <c>dynamics</c> rows, or <see langword="null"/> for none.</param>
    public void Resolve(IReadOnlyList<CreationDriverDocument>? drivers, IReadOnlyList<DynamicsRow>? rows) {
        if (ReferenceEquals(objA: drivers, objB: m_resolvedDrivers) && ReferenceEquals(objA: rows, objB: m_resolvedRows)) {
            return;
        }

        m_resolvedDrivers = drivers;
        m_resolvedRows = rows;
        Array.Clear(array: m_follows);

        var count = Math.Min(
            val1: (drivers?.Count ?? 0),
            val2: CreationDocument.MaxDrivers
        );

        for (var index = 0; (index < count); index++) {
            m_follows[index] = WorldDynamicsResponse.TryResolveResponse(
                name: drivers![index].Dynamics,
                response: out m_responses[index],
                rows: rows
            );
        }
    }
    /// <summary>Eases the body's planar motion by one frame and derives the direction and acceleration signals.</summary>
    /// <param name="travel">The body's rendered displacement this frame, world units, already clamped against a
    /// teleport.</param>
    /// <param name="yaw">The body's rendered yaw about world up this frame, radians, engine −Z forward.</param>
    /// <param name="deltaSeconds">The frame delta, seconds; a non-positive delta changes nothing.</param>
    /// <param name="blend">The fraction of the remaining error the eased velocities close this frame.</param>
    public void Observe(Vector3 travel, float yaw, float deltaSeconds, float blend) {
        if (deltaSeconds <= 0f) {
            return;
        }

        // Engine forward is −Z; a yaw of θ about +Y carries it to (−sin θ, −cos θ) in (x, z), and the body's left to
        // (−cos θ, sin θ), the direction a positive yaw turns toward.
        var (sin, cos) = MathF.SinCos(x: yaw);
        var forward = new Vector2(x: -sin, y: -cos);
        var left = new Vector2(x: -cos, y: sin);
        var velocity = (new Vector2(x: travel.X, y: travel.Z) / deltaSeconds);
        var local = new Vector2(
            x: Vector2.Dot(value1: velocity, value2: forward),
            y: Vector2.Dot(value1: velocity, value2: left)
        );

        m_localVelocity += ((local - m_localVelocity) * blend);

        if (m_localVelocity.Length() > WorldGaitDrivers.MovingSpeed) {
            TravelDirection = MathF.Atan2(
                x: m_localVelocity.X,
                y: m_localVelocity.Y
            );
        }

        var previous = m_worldVelocity;

        m_worldVelocity += ((velocity - m_worldVelocity) * blend);

        var acceleration = ((m_worldVelocity - previous) / deltaSeconds);

        ForwardAcceleration = Vector2.Dot(value1: acceleration, value2: forward);
        LateralAcceleration = Vector2.Dot(value1: acceleration, value2: left);
    }
    /// <summary>Returns a driver's phase this frame: <paramref name="target"/> itself when the driver names no
    /// dynamics row, otherwise its follower stepped one frame toward it. A periodic target is unwrapped against the
    /// follower's last one first, so a direction crossing its ±π seam eases the short way round.</summary>
    /// <param name="index">The driver's row index.</param>
    /// <param name="target">The driver's cadence × signal this frame.</param>
    /// <param name="period">The target's period (2π × |cadence| for an angle), or zero for a level.</param>
    /// <param name="deltaSeconds">The frame delta, seconds.</param>
    /// <returns>The driver's phase.</returns>
    public float Follow(int index, float target, float period, float deltaSeconds) {
        if (!m_follows[index]) {
            return target;
        }
        if (!m_seeded[index]) {
            m_seeded[index] = true;
            m_value[index] = target;
            m_velocity[index] = 0f;
            m_previousTarget[index] = target;

            return target;
        }
        if (period > 0f) {
            target += (period * MathF.Round(x: ((m_previousTarget[index] - target) / period)));
        }
        if (deltaSeconds <= 0f) {
            m_previousTarget[index] = target;

            return m_value[index];
        }

        var response = m_responses[index];
        var targetVelocity = ((target - m_previousTarget[index]) / deltaSeconds);

        m_previousTarget[index] = target;
        SecondOrderResponse.Step(
            position: ref m_value[index],
            propagator: response.Propagator(deltaSeconds: deltaSeconds),
            target: target,
            targetVelocity: targetVelocity,
            targetVelocityGain: response.TargetVelocityGain,
            velocity: ref m_velocity[index]
        );

        return m_value[index];
    }
    /// <summary>Returns the period a driver's target repeats over: one turn of the travel direction scaled by the
    /// cadence, zero for every other signal.</summary>
    /// <param name="signal">The driver's signal.</param>
    /// <param name="cadence">The driver's cadence.</param>
    /// <returns>The period, or zero.</returns>
    public static float Period(string? signal, float cadence) => ((signal is CreationDriverDocument.SignalTravelDirection)
        ? (TwoPi * MathF.Abs(x: cadence))
        : 0f
    );
}
