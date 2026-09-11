using System.Numerics;

namespace Puck.Shaders.Study;

/// <summary>
/// The per-frame values a host fills on <see cref="StudyPassNode.Input"/> before calling
/// <see cref="StudyPassNode.ProduceFrame"/> — everything <see cref="StudyPushConstants"/> needs beyond the pass's
/// own resolution and frame counter, which the node tracks itself. Presentation only: none of it is simulation
/// state.
/// </summary>
/// <param name="Seconds">The study clock, in seconds — <see cref="StudyPushConstants.ITime"/>.</param>
/// <param name="DeltaSeconds">The study clock's advance this frame, in seconds — <see cref="StudyPushConstants.ITimeDelta"/>.</param>
/// <param name="Mouse">The pointer feed in the slot's pixel space, Shadertoy sign convention — <c>xy</c> current
/// while pressed, <c>zw</c> press position, negative when released; zero when there is no pointer.</param>
/// <param name="Date">Shadertoy's <c>iDate</c> — <c>(year, month, day, seconds-since-midnight)</c>.</param>
/// <param name="CameraPos">The paired authored camera's eye position.</param>
/// <param name="CameraTarget">The paired authored camera's look-at point.</param>
/// <param name="CameraUp">The paired authored camera's up vector.</param>
/// <param name="CameraFov">The paired authored camera's vertical field of view, in radians — 0 (with the three
/// camera vectors zero) when no camera is paired, the signal a study branches on to keep its own <c>iMouse</c> orbit.</param>
public readonly record struct StudyFrameInput(
    double Seconds,
    double DeltaSeconds,
    Vector4 Mouse,
    Vector4 Date,
    Vector3 CameraPos,
    Vector3 CameraTarget,
    Vector3 CameraUp,
    float CameraFov
);
