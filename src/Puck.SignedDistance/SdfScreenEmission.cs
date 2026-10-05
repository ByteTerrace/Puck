namespace Puck.SignedDistance;

/// <summary>The GPU reduction of one acquired screen image: sixteen linear RGB cell averages followed by its
/// pixel-weighted whole-image average. Direct screen lighting and indirect face emission read the same allocation.</summary>
public static class SdfScreenEmission {
    /// <summary>The image cells along each axis.</summary>
    public const int Edge = 4;
    /// <summary>The record containing the whole-image average.</summary>
    public const int Mean = (Edge * Edge);
    /// <summary>The float4 records per screen. A cell's fourth component is its validity; the mean carries the direct gain.</summary>
    public const int Records = (Mean + 1);
    /// <summary>The direct room-glow gain. Face emission retains the image's physical linear radiance.</summary>
    public const float DirectGain = 2.5f;
    /// <summary>The bytes shared by every view of one residency, including unbound screen slots.</summary>
    public const int Bytes = (((SdfProgramBuilder.MaxScreenSurfaces * Records) * sizeof(float)) * 4);
}
