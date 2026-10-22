namespace Puck.SignedDistance;

/// <summary>The GPU reduction of one acquired screen image: its pixel-weighted linear RGB average times the luminance a
/// picture value of one shows at, one float4 a screen, the mean luminance in nits that direct screen lighting
/// reads.</summary>
public static class SdfScreenEmission {
    /// <summary>The image cells along each axis the reduction sums in a fixed order before it totals them.</summary>
    public const int Edge = 4;
    /// <summary>The bytes shared by every view of one residency, including unbound screen slots.</summary>
    public const int Bytes = ((SdfProgramBuilder.MaxScreenSurfaces * sizeof(float)) * 4);
    /// <summary>The peak luminance of a display that authors none, in nits: a white picture shows at it.</summary>
    public const float DefaultLuminance = 200f;
    /// <summary>The largest peak luminance a display admits, in nits.</summary>
    public const float MaxLuminance = 10000f;
}
