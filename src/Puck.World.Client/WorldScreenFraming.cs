using System.Numerics;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// The emulator view: a seat captured on a screen it engaged frames that screen square-on, its glass filling the
/// viewport's limiting axis with the aspect kept, every number derived from the screen row's own frame (origin, right,
/// up, half extents and depth) and the seat camera's field of view. One instance per seat eases between the seat's
/// ordinary camera and that framing on the seat rig's own reported dynamics, and back again when the seat leaves; a rig
/// reporting no dynamics cuts. While framed, the camera's near plane stands just short of the glass, so the engaging
/// avatar and anything else between the eye and the device draws nothing. Once the ease settles, <see cref="Framed"/>
/// names the screen, and the seat's view shows its 2D emulator view (<see cref="WorldViewGraphHost.PlaceEngaged"/>)
/// until the seat leaves. Presentation only: nothing here feeds the simulation.
/// </summary>
public sealed class WorldScreenFraming {
    // The weight within this of its goal settles onto it, so a finished ease is exactly the chase or the framing.
    private const float SettleEpsilon = 1e-3f;

    private SdfCameraDynamics m_cachedDynamics;
    private SecondOrderResponse m_cachedResponse;
    private bool m_hasCachedResponse;
    private Vector3 m_frameEye;
    private float m_frameNear;
    private Vector3 m_frameTarget;
    private bool m_hasFrame;
    private float m_velocity;
    private float m_weight;

    /// <summary>Gets how far the seat's camera stands toward the framing: 0 is the seat's ordinary camera, 1 the
    /// framed screen.</summary>
    public float Weight => m_weight;
    /// <summary>Gets the screen whose framing the ease has settled on as of the last <see cref="Apply"/>, the one the
    /// seat's emulator view shows (<see cref="WorldViewGraphHost.PlaceEngaged"/>), or <see langword="null"/> while the
    /// seat engages none, is easing in or out, or the screen's frame is degenerate.</summary>
    public WorldScreen? Framed { get; private set; }

    /// <summary>Frames a screen's glass square-on: the eye stands on the face's normal, far enough that the glass
    /// fills the viewport's limiting axis exactly, looking at the glass's center.</summary>
    /// <param name="screen">The screen row whose glass is framed.</param>
    /// <param name="fieldOfViewRadians">The camera's vertical field of view, in radians; must be in (0, pi).</param>
    /// <param name="aspectRatio">The viewport's width over its height; must be positive.</param>
    /// <param name="eye">The framing eye, in world units.</param>
    /// <param name="target">The glass's center, which the eye looks at.</param>
    /// <param name="near">The framing's near-plane distance from the eye, in world units: the glass's distance less
    /// the slab's thickness, so the device face draws and nothing nearer does.</param>
    /// <returns><see langword="false"/> when the row's frame is degenerate (a zero axis, parallel axes, or a
    /// non-positive half extent) or the field of view or aspect is out of range; the outputs are then zero.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="screen"/> is <see langword="null"/>.</exception>
    public static bool TryFrame(WorldScreen screen, float fieldOfViewRadians, float aspectRatio, out Vector3 eye, out Vector3 target, out float near) {
        ArgumentNullException.ThrowIfNull(argument: screen);

        eye = default;
        target = default;
        near = 0f;

        var normal = Vector3.Cross(
            vector1: screen.Right,
            vector2: screen.Up
        );

        if (
            !(normal.LengthSquared() > 0f) ||
            !(screen.HalfWidth > 0f) ||
            !(screen.HalfHeight > 0f) ||
            !(fieldOfViewRadians > 0f) ||
            !(fieldOfViewRadians < MathF.PI) ||
            !(aspectRatio > 0f)
        ) {
            return false;
        }

        var tanHalf = MathF.Tan(x: (fieldOfViewRadians * 0.5f));
        var distance = MathF.Max(
            x: (screen.HalfHeight / tanHalf),
            y: (screen.HalfWidth / (tanHalf * aspectRatio))
        );

        target = screen.Origin;
        eye = (target + (Vector3.Normalize(value: normal) * distance));
        near = MathF.Max(
            x: 0f,
            y: (distance - (2f * screen.HalfDepth))
        );

        return true;
    }

    /// <summary>Advances the ease one frame and blends the seat's camera toward the screen's framing by it.</summary>
    /// <param name="screen">The screen the seat is captured on, or <see langword="null"/> when it engages none; the
    /// ease then returns to the seat's camera from the last framing it held.</param>
    /// <param name="dynamics">The seat rig's reported dynamics, which time the ease; <see cref="SdfCameraDynamics.None"/>
    /// cuts.</param>
    /// <param name="fieldOfViewRadians">The seat camera's vertical field of view, in radians.</param>
    /// <param name="aspectRatio">The viewport's width over its height.</param>
    /// <param name="deltaSeconds">The frame step, in seconds.</param>
    /// <param name="eye">The seat camera's eye, blended in place.</param>
    /// <param name="target">The seat camera's target, blended in place.</param>
    /// <returns>The camera's near-plane distance, in world units: zero for the seat's own camera, the framing's near
    /// plane once framed, and the blend between while easing.</returns>
    public float Apply(WorldScreen? screen, in SdfCameraDynamics dynamics, float fieldOfViewRadians, float aspectRatio, float deltaSeconds, ref Vector3 eye, ref Vector3 target) {
        var goal = 0f;

        Framed = null;

        if (
            (screen is not null) &&
            TryFrame(
                aspectRatio: aspectRatio,
                eye: out var frameEye,
                fieldOfViewRadians: fieldOfViewRadians,
                near: out var frameNear,
                screen: screen,
                target: out var frameTarget
            )
        ) {
            m_frameEye = frameEye;
            m_frameTarget = frameTarget;
            m_frameNear = frameNear;
            m_hasFrame = true;
            goal = 1f;
        }

        if (!m_hasFrame) {
            return 0f;
        }

        Ease(
            deltaSeconds: deltaSeconds,
            dynamics: in dynamics,
            goal: goal
        );

        if (m_weight <= 0f) {
            m_hasFrame = false;

            return 0f;
        }
        if (
            (goal >= 1f) &&
            (m_weight >= 1f)
        ) {
            Framed = screen;
        }

        eye = Vector3.Lerp(
            value1: eye,
            value2: m_frameEye,
            amount: m_weight
        );
        target = Vector3.Lerp(
            value1: target,
            value2: m_frameTarget,
            amount: m_weight
        );

        return (m_weight * m_frameNear);
    }
    private void Ease(in SdfCameraDynamics dynamics, float goal, float deltaSeconds) {
        if (!dynamics.IsLive) {
            m_weight = goal;
            m_velocity = 0f;
            m_hasCachedResponse = false;

            return;
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
    }
}
