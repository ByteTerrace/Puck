using System.Numerics;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// The complete live view state of one occupied seat. It is born and dies with <see cref="SeatController"/> and is
/// the sole owner of play-camera yaw/pitch, compiled live rig, and smoothing. Input adapters mutate it; movement,
/// local rendering, away rendering, and read-back all observe it.
/// </summary>
internal sealed class WorldSeatViewState {
    private readonly Lock m_gate = new();
    private readonly WorldSeatCameraResolver.LiveOrbitCache m_orbit = new();
    private readonly WorldSeatCameraResolver.SmoothingState m_smoothing = new();

    private WorldCameraRig? m_authoredRig;
    private ISdfCameraRig? m_compiledRig;
    private float m_pitch;
    private float m_yaw;

    public float Pitch { get { lock (m_gate) { return m_pitch; } } }
    public float Yaw { get { lock (m_gate) { return m_yaw; } } }

    private static float Wrap(float radians) => (radians - (MathF.Tau * MathF.Round(x: (radians / MathF.Tau))));

    public float LogicalYaw(WorldViewDefaults views, Quaternion bodyOrientation) {
        var authoredYaw = ((views.SeatRig.Motion as WorldCameraMotion.Orbit)?.Yaw ?? 0f);
        var bodyYaw = ((views.SeatControl.YawReference == WorldSeatYawReference.Body)
            ? WorldSeatCameraResolver.BodyYaw(orientation: bodyOrientation)
            : 0f
        );

        lock (m_gate) {
            return Wrap(radians: ((authoredYaw + m_yaw) + bodyYaw));
        }
    }
    public void Nudge(Vector2 input, float yawScale, float pitchScale, WorldSeatLook preference, WorldSeatViewControl control) {
        ArgumentNullException.ThrowIfNull(argument: preference);
        ArgumentNullException.ThrowIfNull(argument: control);

        // Input is semantic look direction (+X right, +Y up). OrbitRig stores the eye's offset around the target,
        // so the corresponding orbit angles move in the opposite direction.
        lock (m_gate) {
            m_yaw = Wrap(radians: (m_yaw - ((input.X * yawScale) * (preference.InvertYaw
                ? -1f
                : 1f))));
            m_pitch = Math.Clamp(
                value: (m_pitch - ((input.Y * pitchScale) * (preference.InvertPitch
                ? -1f
                : 1f))),
                min: control.MinPitch,
                max: control.MaxPitch
            );
        }
    }
    public void Recenter() {
        lock (m_gate) {
            m_yaw = 0f;
            m_pitch = 0f;
            m_smoothing.Seeded = false;
        }
    }
    public void Reclamp(WorldSeatViewControl control) {
        ArgumentNullException.ThrowIfNull(argument: control);
        lock (m_gate) {
            m_pitch = Math.Clamp(
                value: m_pitch,
                min: control.MinPitch,
                max: control.MaxPitch
            );
        }
    }
    public ISdfCameraRig ResolveChase(WorldViewDefaults views, Quaternion bodyOrientation) {
        if (!ReferenceEquals(
            objA: m_authoredRig,
            objB: views.SeatRig
        )) {
            m_authoredRig = views.SeatRig;
            m_compiledRig = WorldCameraRigCompiler.Compile(rig: views.SeatRig);
        }

        float yaw;
        float pitch;

        lock (m_gate) {
            m_pitch = Math.Clamp(
                value: m_pitch,
                min: views.SeatControl.MinPitch,
                max: views.SeatControl.MaxPitch
            );
            yaw = m_yaw;
            pitch = m_pitch;
        }

        return WorldSeatCameraResolver.ResolveChase(
            authoredRig: views.SeatRig,
            compiledChase: m_compiledRig!,
            yawReference: views.SeatControl.YawReference,
            bodyOrientation: bodyOrientation,
            liveYaw: yaw,
            livePitch: pitch,
            cache: m_orbit
        );
    }
    public void Smooth(float rate, bool enabled, float deltaSeconds, ref Vector3 eye, ref Vector3 target) {
        lock (m_gate) {
            WorldSeatCameraResolver.Smooth(
                deltaSeconds: deltaSeconds,
                eye: ref eye,
                isPlainChase: enabled,
                smoothRate: rate,
                state: m_smoothing,
                target: ref target
            );
        }
    }
}
