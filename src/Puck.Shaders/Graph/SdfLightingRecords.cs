using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.Shaders;

/// <summary>One native light-table element. Integer tags remain integers through upload and shader reads.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct SdfLightData {
    /// <summary>Unit direction toward a directional light, or a point light's position.</summary>
    public Vector3 Direction;
    /// <summary>The light's strength.</summary>
    public float Weight;
    /// <summary>The linear RGB emission.</summary>
    public Vector3 Color;
    /// <summary>The light kind's generated numeric tag.</summary>
    public uint Kind;
    /// <summary>The kind's penumbra slope, hemisphere gradient, rim exponent or point radius.</summary>
    public float Parameter;
    /// <summary>One when this light contributes a shadowed term.</summary>
    public uint Shadows;
    /// <summary>The point light's dynamic-transform slot, or minus one for its authored position.</summary>
    public int DynamicSlot;
    /// <summary>Zeroed trailing alignment.</summary>
    public uint Reserved;
}
/// <summary>The light table's bounds and current shadow selection, read by shadow and hit shading only.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct SdfLightFrameData {
    /// <summary>The number of authored light records.</summary>
    public uint Count;
    /// <summary>The shadowed directional light, or minus one.</summary>
    public int ShadowIndex;
    /// <summary>Zeroed trailing alignment.</summary>
    public Vector2 Reserved;
}
/// <summary>One gradient stop, read directly as a generated native record.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct SdfSkyStopData {
    /// <summary>The linear RGB color.</summary>
    public Vector3 Color;
    /// <summary>The elevation from minus one to one.</summary>
    public float Elevation;
}
/// <summary>One analytic reflection panel in the current environment model.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct SdfSoftboxData {
    /// <summary>The normalized direction toward the panel.</summary>
    public Vector3 Direction;
    /// <summary>The reflection strength.</summary>
    public float Weight;
    /// <summary>The linear RGB color.</summary>
    public Vector3 Color;
    /// <summary>The additional angular softening.</summary>
    public float Blur;
    /// <summary>The angular half-extents.</summary>
    public Vector2 Size;
    /// <summary>Zeroed trailing alignment.</summary>
    public Vector2 Reserved;
}
/// <summary>The resolved sky and lighting frame, stored once per residency instead of copied into every pass block.
/// Motion fields already contain the shared resolver's integrated offsets and phase.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct SdfSkyFrameData {
    /// <summary>One when the authored sky is enabled.</summary>
    public uint SkyEnabled;
    /// <summary>The exponential fog density.</summary>
    public float FogDensity;
    /// <summary>The number of gradient stops.</summary>
    public uint StopCount;
    /// <summary>The number of reflection panels.</summary>
    public uint SoftboxCount;
    /// <summary>The cavity gain.</summary>
    public float CurvatureCavity;
    /// <summary>The rim gain.</summary>
    public float CurvatureRim;
    /// <summary>The ink gain.</summary>
    public float CurvatureInk;
    /// <summary>The ordered ink band's lower threshold.</summary>
    public float CurvatureInkLow;
    /// <summary>The ink color.</summary>
    public Vector3 CurvatureInkColor;
    /// <summary>The ordered ink band's upper threshold.</summary>
    public float CurvatureInkHigh;
    /// <summary>The disc's resolved unit direction, so the sky need not bind the light table.</summary>
    public Vector3 SunDiscDirection;
    /// <summary>The disc intensity.</summary>
    public float SunDiscIntensity;
    /// <summary>The cloud illumination's resolved unit direction.</summary>
    public Vector3 CloudLightDirection;
    /// <summary>The host-baked disc exponent.</summary>
    public float SunDiscExponent;
    /// <summary>The cloud illumination's resolved color.</summary>
    public Vector3 CloudLightColor;
    /// <summary>One when the disc names a resolved directional light.</summary>
    public uint SunDiscEnabled;
    /// <summary>The star lattice density.</summary>
    public float StarDensity;
    /// <summary>The star brightness.</summary>
    public float StarBrightness;
    /// <summary>The star lattice seed.</summary>
    public uint StarSeed;
    /// <summary>The share of stars that twinkle.</summary>
    public float TwinkleShare;
    /// <summary>The twinkle modulation depth.</summary>
    public float TwinkleDepth;
    /// <summary>The integrated unit twinkle phase.</summary>
    public float TwinklePhase;
    /// <summary>The cloud coverage.</summary>
    public float CloudCoverage;
    /// <summary>The cloud edge softness.</summary>
    public float CloudSoftness;
    /// <summary>The cloud color.</summary>
    public Vector3 CloudColor;
    /// <summary>The cloud lattice scale.</summary>
    public float CloudScale;
    /// <summary>The cloud lattice seed.</summary>
    public uint CloudSeed;
    /// <summary>The integrated cloud rotation in radians.</summary>
    public float CloudSpinAngle;
    /// <summary>The cloud curl amount.</summary>
    public float CloudCurl;
    /// <summary>Zeroed motion alignment.</summary>
    public float ReservedMotion;
    /// <summary>The integrated cloud drift, reduced by the lattice period.</summary>
    public Vector2 CloudOffset;
    /// <summary>The integrated cloud shear, reduced by the lattice period.</summary>
    public Vector2 CloudShearOffset;
    /// <summary>The ground-facing reflection horizon.</summary>
    public Vector3 HorizonLow;
    /// <summary>Zeroed horizon alignment.</summary>
    public float ReservedLow;
    /// <summary>The sky-facing reflection horizon.</summary>
    public Vector3 HorizonHigh;
    /// <summary>Zeroed horizon alignment.</summary>
    public float ReservedHigh;
}
