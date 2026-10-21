using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// The weight a seat camera eases between its ordinary framing (0) and an engaged framing (1) by, timed by the seat
/// rig's own reported dynamics: a second-order response toward the goal each frame, settling exactly onto it once
/// within a thousandth, and cutting straight to it when the rig reports no dynamics. Presentation only.
/// </summary>
public sealed class WorldFramingEase {
    // The weight within this of its goal settles onto it, so a finished ease is exactly the seat camera or the framing.
    private const float SettleEpsilon = 1e-3f;

    private SdfCameraDynamics m_cachedDynamics;
    private SecondOrderResponse m_cachedResponse;
    private bool m_hasCachedResponse;
    private float m_velocity;
    private float m_weight;

    /// <summary>Gets the eased weight, in [0, 1]: 0 is the seat's ordinary camera, 1 the engaged framing.</summary>
    public float Weight => m_weight;

    /// <summary>Advances the ease one frame toward <paramref name="goal"/>.</summary>
    /// <param name="dynamics">The seat rig's reported dynamics; <see cref="SdfCameraDynamics.None"/> cuts.</param>
    /// <param name="goal">The weight to ease toward, 0 or 1.</param>
    /// <param name="deltaSeconds">The frame step, in seconds.</param>
    /// <returns>The eased weight after the step.</returns>
    public float Step(in SdfCameraDynamics dynamics, float goal, float deltaSeconds) {
        if (!dynamics.IsLive) {
            m_weight = goal;
            m_velocity = 0f;
            m_hasCachedResponse = false;

            return m_weight;
        }

        if (
            !m_hasCachedResponse ||
            (m_cachedDynamics != dynamics)
        ) {
            m_cachedResponse = SecondOrderResponse.Create(
                dampingRatio: dynamics.Damping,
                frequencyHz: dynamics.Frequency,
                initialResponse: dynamics.Response
            );
            m_cachedDynamics = dynamics;
            m_hasCachedResponse = true;
        }

        var weight = m_weight;
        var velocity = m_velocity;

        SecondOrderResponse.Step(
            position: ref weight,
            propagator: m_cachedResponse.Propagator(deltaSeconds: deltaSeconds),
            target: goal,
            targetVelocity: 0f,
            targetVelocityGain: m_cachedResponse.TargetVelocityGain,
            velocity: ref velocity
        );

        if (MathF.Abs(x: (goal - weight)) < SettleEpsilon) {
            weight = goal;
            velocity = 0f;
        }

        m_weight = Math.Clamp(
            max: 1f,
            min: 0f,
            value: weight
        );
        m_velocity = velocity;

        return m_weight;
    }
}
