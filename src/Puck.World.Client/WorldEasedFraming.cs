using System.Numerics;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// A seat camera's eased move onto a framing and back: while a framing is supplied the camera eases from its ordinary
/// eye and target toward the framing's (<see cref="WorldFramingEase"/>), and once none is supplied it eases back from
/// the last framing it held. The board view, a seat framing row and any other framing that replaces the chase camera
/// for a while blend through this one step. Presentation only.
/// </summary>
public sealed class WorldEasedFraming {
    private readonly WorldFramingEase m_ease = new();
    private SdfCameraDynamics m_dynamics;
    private Vector3 m_eye;
    private float m_fieldOfView;
    private bool m_held;
    private float m_near;
    private Vector3 m_target;

    /// <summary>Gets how far the camera stands toward the framing: 0 is the seat's ordinary camera, 1 the framing.</summary>
    public float Weight => m_ease.Weight;

    /// <summary>Advances the ease one frame and blends the camera toward the framing by it.</summary>
    /// <param name="framing">The framing to hold this frame (its eye and target in world units, its near-plane
    /// distance from the eye, and its vertical field of view in radians, or zero or less to keep the camera's own), or
    /// <see langword="null"/> to ease back to the camera from the last framing held.</param>
    /// <param name="dynamics">The response that times the ease while <paramref name="framing"/> is supplied; the ease
    /// back keeps the response it eased in on. <see cref="SdfCameraDynamics.None"/> cuts.</param>
    /// <param name="deltaSeconds">The frame step, in seconds.</param>
    /// <param name="eye">The camera's eye, blended in place.</param>
    /// <param name="target">The camera's target, blended in place.</param>
    /// <param name="fieldOfView">The camera's vertical field of view, in radians, blended in place.</param>
    /// <returns>The camera's near-plane distance, in world units: zero for the seat's own camera, the framing's once
    /// framed, and the blend between while easing.</returns>
    public float Apply((Vector3 Eye, Vector3 Target, float Near, float FieldOfView)? framing, in SdfCameraDynamics dynamics, float deltaSeconds, ref Vector3 eye, ref Vector3 target, ref float fieldOfView) {
        if (framing is { } held) {
            m_eye = held.Eye;
            m_target = held.Target;
            m_near = held.Near;
            m_fieldOfView = held.FieldOfView;
            m_dynamics = dynamics;
            m_held = true;
        }

        if (!m_held) {
            return 0f;
        }

        var weight = m_ease.Step(
            deltaSeconds: deltaSeconds,
            dynamics: in m_dynamics,
            goal: ((framing is null) ? 0f : 1f)
        );

        if (weight <= 0f) {
            m_held = false;

            return 0f;
        }

        eye = Vector3.Lerp(value1: eye, value2: m_eye, amount: weight);
        target = Vector3.Lerp(value1: target, value2: m_target, amount: weight);
        if (m_fieldOfView > 0f) {
            fieldOfView = float.Lerp(value1: fieldOfView, value2: m_fieldOfView, amount: weight);
        }

        return (weight * m_near);
    }
}
