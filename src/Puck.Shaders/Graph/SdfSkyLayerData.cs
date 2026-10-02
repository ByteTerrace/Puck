using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.Shaders;

/// <summary>The common native admission and composition fields of one authored sky row. Kind parameters remain
/// in their own typed tables; the per-view admitted list indexes these rows without changing authored run storage.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct SdfSkyLayerData {
    /// <summary>The unit quaternion rotating a world direction into this layer's local sky frame.</summary>
    public Vector4 InverseRotation;
    /// <summary>The unit cone direction in this layer's local frame.</summary>
    public Vector3 ConeDirection;
    /// <summary>The cosine of the cone's angular half-width.</summary>
    public float ConeCosine;
    /// <summary>The sines of the closed elevation band's lower and upper angles.</summary>
    public Vector2 Elevation;
    /// <summary>Zero for no mask, one for an elevation band, two for a cone.</summary>
    public uint Mask;
    /// <summary>The kind tag generated from the schema's registered kind inventory.</summary>
    public uint Kind;
    /// <summary>The native row in this kind's parameter table.</summary>
    public uint ParameterIndex;
    /// <summary>The authored affine blend operation.</summary>
    public uint Blend;
    /// <summary>The structural camera/lighting visibility bits.</summary>
    public uint Visibility;
    /// <summary>The least sky quality tier that admits this row.</summary>
    public uint MinimumTier;
    /// <summary>The domain-checked common opacity.</summary>
    public float Opacity;
    /// <summary>One when authored/common/kind gates admit work, independent of per-view quality or inspection.</summary>
    public uint Enabled;
    /// <summary>The authored ordinal used to attribute evaluation counts.</summary>
    public uint AuthoredIndex;
    /// <summary>Zeroed trailing alignment.</summary>
    public uint Reserved;
}
