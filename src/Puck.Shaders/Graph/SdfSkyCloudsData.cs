using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.Shaders;

/// <summary>Resolved parameters for one procedural cloud layer. Motion is already integrated by the shared
/// timeline resolver; lighting is supplied separately through the ordinary light response.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct SdfSkyCloudsData {
    /// <summary>The linear surface color.</summary>
    public Vector3 Color;
    /// <summary>The covered fraction of the noise range.</summary>
    public float Coverage;
    /// <summary>The integrated drift in periodic lattice cells.</summary>
    public Vector2 Offset;
    /// <summary>The integrated offset of the shaping field in periodic lattice cells.</summary>
    public Vector2 ShearOffset;
    /// <summary>The admitted finite positive normal reciprocal of the density transition width.</summary>
    public float InverseSoftness;
    /// <summary>The admitted finite positive normal reciprocal of the cell size in dome units.</summary>
    public float InverseScale;
    /// <summary>The exact integer lattice seed.</summary>
    public uint Seed;
    /// <summary>The integrated rotation about the local zenith, in radians.</summary>
    public float SpinAngle;
    /// <summary>The additional radial rotation strength.</summary>
    public float Curl;
    /// <summary>The dome center's depth below the viewer, with unit height overhead.</summary>
    public float DomeRadius;
    /// <summary>The displacement of the density field by its shaping field, in cells.</summary>
    public float Warp;
    /// <summary>The heightfield rise per unit thickness, in cells.</summary>
    public float Height;
    /// <summary>The normal and self-shadow sample spacing, in cells.</summary>
    public float NormalTap;
    /// <summary>The darkening caused by a taller neighbor toward a light.</summary>
    public float SelfShadow;
    /// <summary>The strength of a light's thin-edge highlight.</summary>
    public float SilverLining;
    /// <summary>The extinction per unit thickness in Beer's law.</summary>
    public float Extinction;
    /// <summary>The admitted finite positive normal reciprocal of the horizon fade elevation.</summary>
    public float InverseHorizonFade;
    /// <summary>The fraction of cloud color lit by the supplied ambient field.</summary>
    public float AmbientFloor;
    /// <summary>The exponent of the forward light highlight.</summary>
    public float SilverExponent;
    /// <summary>The admitted finite positive normal reciprocal of the authored normal-tap spacing.</summary>
    public float InverseNormalTap;
}
