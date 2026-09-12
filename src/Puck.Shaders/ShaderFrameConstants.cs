using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.Shaders;

/// <summary>The 112-byte cross-backend frame block exposed by the Shadertoy adapter.</summary>
[StructLayout(LayoutKind.Sequential, Size = SizeBytes)]
public readonly record struct ShaderFrameConstants(
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
    float Pad2)
{
    public const int SizeBytes = 112;
    public void CopyTo(Span<byte> destination) => MemoryMarshal.Write(destination, in this);
}
