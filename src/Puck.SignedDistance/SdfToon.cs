using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>Presentation-only cel shading. Colours are linear RGB, widths are output pixels, distances are metres.</summary>
public readonly record struct SdfToon {
    /// <summary>Whether opaque materials use the cel response.</summary>
    public bool Enabled { get; init; }
    /// <summary>The diffuse cosine separating the shadow and lit bands.</summary>
    public float Threshold { get; init; }
    /// <summary>The full width of the soft band transition.</summary>
    public float Softness { get; init; }
    /// <summary>The shadow band's RGB reflectance relative to the lit band.</summary>
    public Vector3 ShadowTint { get; init; }
    /// <summary>The outline radius at close range, in output pixels.</summary>
    public float OutlineWidth { get; init; }
    /// <summary>The distance at which the outline radius halves.</summary>
    public float OutlineDistance { get; init; }
    /// <summary>The normal cosine below which a crease is inked.</summary>
    public float OutlineNormal { get; init; }
    /// <summary>The linear colour multiplying an inked surface.</summary>
    public Vector3 OutlineTint { get; init; }
    /// <summary>The highlight's angular radius, as one minus its half-vector cosine.</summary>
    public float SpecularSize { get; init; }
    /// <summary>The exposure gain applied to reflected material light, in stops.</summary>
    public float Exposure { get; init; }
    /// <summary>The peak-preserving shoulder strength on reflected material light; zero disables compression.</summary>
    public float Shoulder { get; init; }

    /// <summary>The optional mode's initial look; off until a document or live lever enables it.</summary>
    public static SdfToon Default { get; } = new() {
        Threshold = .45f, Softness = .08f, ShadowTint = new(.38f, .48f, .65f),
        OutlineWidth = 1.5f, OutlineDistance = 12f, OutlineNormal = .65f,
        OutlineTint = new(.08f, .10f, .16f), SpecularSize = .025f, Exposure = .5f, Shoulder = .65f,
    };
}
