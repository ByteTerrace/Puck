namespace Puck.SignedDistance;

/// <summary>The GPU reduction of one acquired screen image: its pixel-weighted linear RGB average, one float4 a screen,
/// which direct screen lighting reads.</summary>
public static class SdfScreenEmission {
    /// <summary>The image cells along each axis the reduction sums in a fixed order before it totals them.</summary>
    public const int Edge = 4;
    /// <summary>The direct room-glow gain, carried in the average's fourth component.</summary>
    public const float DirectGain = 2.5f;
    /// <summary>The bytes shared by every view of one residency, including unbound screen slots.</summary>
    public const int Bytes = ((SdfProgramBuilder.MaxScreenSurfaces * sizeof(float)) * 4);
}
