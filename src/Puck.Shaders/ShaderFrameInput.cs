using System.Numerics;

namespace Puck.Shaders;

/// <summary>Presentation values supplied to a Shadertoy-compatible frame.</summary>
public readonly record struct ShaderFrameInput(
    double Seconds,
    double DeltaSeconds,
    Vector4 Mouse,
    Vector4 Date,
    Vector3 CameraPos,
    Vector3 CameraTarget,
    Vector3 CameraUp,
    float CameraFov);
