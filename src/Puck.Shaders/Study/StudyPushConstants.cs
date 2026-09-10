using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.Shaders.Study;

/// <summary>
/// The 112-byte push-constant block every study kernel reads, identical on both backends — the C# mirror
/// of the study prelude's <c>PuckStudy</c> block (<c>#define PUCK_STUDY 1</c>): <c>layout(push_constant) uniform
/// PuckStudy { ... } puck;</c> on the GLSL/SPIR-V side, a root-constant <c>cbuffer</c> on the Direct3D 12 side.
/// Field order and byte offsets follow the same packing rule as <see cref="ShaderPushConstantLayout"/> — std430 /
/// HLSL constant-buffer packing, every row 16 bytes, a field padded to keep a vector from straddling a row:
/// declaration order below IS the wire layout (a plain <see cref="LayoutKind.Sequential"/> struct of only
/// 4-byte-aligned members reproduces it with no inserted padding), so reordering or removing a field here changes
/// what every study shader reads — coordinate with the GLSL prelude before doing either.
/// </summary>
/// <param name="IResolution">The pass's pixel size, <c>(width, height, 1)</c> — Shadertoy's <c>iResolution</c>.</param>
/// <param name="ITime">The study clock in seconds — Shadertoy's <c>iTime</c>. Presentation only; never simulation state.</param>
/// <param name="ITimeDelta">The study clock's advance this frame, in seconds — Shadertoy's <c>iTimeDelta</c>.</param>
/// <param name="IFrame">The node's own produced-frame counter — Shadertoy's <c>iFrame</c>.</param>
/// <param name="Pad0">Row padding after <paramref name="IFrame"/>; unread by any shader.</param>
/// <param name="IMouse">The pointer feed in the slot's pixel space, Shadertoy sign convention — <c>xy</c> current
/// while pressed, <c>zw</c> press position, negative when released; zero when there is no pointer.</param>
/// <param name="IDate">Shadertoy's <c>iDate</c> — <c>(year, month, day, seconds-since-midnight)</c>.</param>
/// <param name="ICameraPos">The paired authored camera's eye position — <c>PUCK_STUDY</c>'s <c>iCameraPos</c>.</param>
/// <param name="ICameraFov">The paired authored camera's vertical field of view, in radians — <c>iCameraFov</c>; 0
/// (with the three camera vectors zero) when the host pairs no camera, the signal a study branches on to keep its own
/// <c>iMouse</c> orbit.</param>
/// <param name="ICameraTarget">The paired authored camera's look-at point — <c>iCameraTarget</c>.</param>
/// <param name="Pad1">Row padding after <paramref name="ICameraTarget"/>; unread by any shader.</param>
/// <param name="ICameraUp">The paired authored camera's up vector — <c>iCameraUp</c>.</param>
/// <param name="Pad2">Row padding after <paramref name="ICameraUp"/>; unread by any shader.</param>
[StructLayout(layoutKind: LayoutKind.Sequential, Size = StudyPushConstants.SizeBytes)]
public readonly record struct StudyPushConstants(
    Vector3 IResolution,
    float ITime,
    float ITimeDelta,
    int IFrame,
    Vector2 Pad0,
    Vector4 IMouse,
    Vector4 IDate,
    Vector3 ICameraPos,
    float ICameraFov,
    Vector3 ICameraTarget,
    float Pad1,
    Vector3 ICameraUp,
    float Pad2
) {
    /// <summary>The block's fixed byte size, identical on both backends.</summary>
    public const int SizeBytes = 112;

    /// <summary>Writes this value's raw bytes — exactly <see cref="SizeBytes"/> of them, in wire layout — to
    /// <paramref name="destination"/>.</summary>
    /// <param name="destination">A span at least <see cref="SizeBytes"/> long.</param>
    public void CopyTo(Span<byte> destination) =>
        MemoryMarshal.Write(destination: destination, value: in this);
}
