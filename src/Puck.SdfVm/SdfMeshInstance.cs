using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>A half-open range of ordinary local program instances shared by static mesh copies.</summary>
/// <param name="FirstInstance">The first program instance ordinal.</param>
/// <param name="InstanceCount">The number of program instances in the template.</param>
public readonly record struct SdfMeshTemplateRange(int FirstInstance, int InstanceCount) {
}

/// <summary>A copy of one local baked geometry asset: a row its producer keeps for as long as the copy lives and rewrites
/// in place when anything about the copy changes (<see cref="Update"/>), so a moving body's pose allocates nothing. Its
/// pose and host identity never enter the field program. A consumer that remembers a copy remembers its
/// <see cref="Revision"/> beside the reference: the same row at another revision is a changed copy. A moving copy's pose
/// reaches its draws through its frame slot's row alone (<see cref="SdfMeshDraw.PlacedBySlot"/>), so it moves no
/// revision.</summary>
/// <param name="AssetSlot">The local template's dynamic slot in the program.</param>
/// <param name="Identity">The host's picking identity for this copy.</param>
/// <param name="Transform">The copy's root pose.</param>
/// <param name="Scale">The uniform size of the copy.</param>
/// <param name="IsDynamic">Whether the surface can move or deform; fixed copies may enter static irradiance geometry.</param>
public sealed class SdfMeshInstance(int AssetSlot, object Identity, DynamicTransform Transform, float Scale, bool IsDynamic) {
    private int m_assetSlot = AssetSlot;
    private object m_identity = Identity;
    private DynamicTransform m_transform = Transform;
    private float m_scale = Scale;
    // The root matrix at m_transform and m_scale, composed whenever either is written, so its readers (the dresser's
    // draws, the bake's demand reading of a shared geometry's copies) never compose two products and a rotation again.
    private Matrix4x4 m_matrix = Compose(transform: Transform, scale: Scale);
    private bool m_isDynamic = IsDynamic;
    private object? m_motionIdentity;
    private SdfMeshTemplateRange? m_templateRange;
    private bool m_usesRootMaterialFrame = true;
    private IReadOnlyList<int> m_otherAssetSlots = [];
    private SdfMesh? m_mesh;
    private int m_material;
    private SdfSlotClip? m_clip;

    /// <summary>Gets the count of rewrites that changed what the copy's draws hold, zero for one never rewritten: every
    /// change but a dynamic copy's <see cref="Transform"/>, which its frame slot's row carries to its draws. A consumer
    /// that compares a copy by reference compares this too, and reads a dynamic copy's pose from its slot's row.</summary>
    public long Revision { get; private set; }
    /// <summary>Gets the local template's dynamic slot in the program.</summary>
    public int AssetSlot { get => m_assetSlot; init => m_assetSlot = value; }
    /// <summary>Gets the host's picking identity for this copy.</summary>
    public object Identity { get => m_identity; init => m_identity = value; }
    /// <summary>Gets the copy's root pose.</summary>
    public DynamicTransform Transform {
        get => m_transform;
        init {
            m_transform = value;
            m_matrix = Compose(transform: value, scale: m_scale);
        }
    }
    /// <summary>Gets the uniform size of the copy.</summary>
    public float Scale {
        get => m_scale;
        init {
            m_scale = value;
            m_matrix = Compose(transform: m_transform, scale: value);
        }
    }
    /// <summary>Gets whether the surface can move or deform; fixed copies may enter static irradiance geometry.</summary>
    public bool IsDynamic { get => m_isDynamic; init => m_isDynamic = value; }
    /// <summary>Gets the host's motion-history identity, stable through continuous motion and changed on a pose cut.
    /// Falls back to <see cref="Identity"/> when absent.</summary>
    public object? MotionIdentity { get => m_motionIdentity; init => m_motionIdentity = value; }
    /// <summary>Gets the ordinary static instance range that supplies local geometry, or null to use
    /// <see cref="AssetSlot"/> and <see cref="OtherAssetSlots"/>. The asset slot is ignored for a range template.</summary>
    public SdfMeshTemplateRange? TemplateRange { get => m_templateRange; init => m_templateRange = value; }
    /// <summary>Gets whether material layers use the copy root rather than the world frame. Geometry and static
    /// lighting membership are independent of this material-coordinate choice.</summary>
    public bool UsesRootMaterialFrame { get => m_usesRootMaterialFrame; init => m_usesRootMaterialFrame = value; }
    /// <summary>Gets additional local surfaces of the asset, such as its separate glass bake.</summary>
    public IReadOnlyList<int> OtherAssetSlots { get => m_otherAssetSlots; init => m_otherAssetSlots = value; }
    /// <summary>Gets optional authored triangle geometry, shared by all copies.</summary>
    public SdfMesh? Mesh { get => m_mesh; init => m_mesh = value; }
    /// <summary>Gets the authored mesh's shared program material.</summary>
    public int Material { get => m_material; init => m_material = value; }

    /// <summary>Gets the portal plane the copy straddles, before its frame slot is assigned.</summary>
    public SdfSlotClip? Clip { get => m_clip; init => m_clip = value; }
    /// <summary>Gets the frame's root slot, assigned after the program's articulated slots.</summary>
    public int FrameSlot { get; internal set; }
    /// <summary>Gets the copy's root matrix at its current <see cref="Transform"/>.</summary>
    public Matrix4x4 Matrix => m_matrix;

    // The root matrix of a pose at a uniform size: scaled, turned, then moved.
    private static Matrix4x4 Compose(DynamicTransform transform, float scale) => ((Matrix4x4.CreateScale(scale: scale) * Matrix4x4.CreateFromQuaternion(quaternion: transform.Orientation)) * Matrix4x4.CreateTranslation(position: transform.Position));

    /// <summary>Rewrites the copy in place, moving <see cref="Revision"/> when any value differs from the one it holds;
    /// the frame-assigned <see cref="FrameSlot"/> is not the producer's and stays. The producer calls it on the frame
    /// thread, while no consumer reads the copy.</summary>
    /// <param name="assetSlot">The local template's dynamic slot in the program.</param>
    /// <param name="identity">The host's picking identity for this copy.</param>
    /// <param name="motionIdentity">The host's motion-history identity, or <see langword="null"/> to fall back to
    /// <paramref name="identity"/>.</param>
    /// <param name="transform">The copy's root pose.</param>
    /// <param name="scale">The uniform size of the copy.</param>
    /// <param name="isDynamic">Whether the surface can move or deform.</param>
    /// <param name="clip">The portal plane the copy straddles, or <see langword="null"/>.</param>
    /// <param name="mesh">The authored triangle geometry, or <see langword="null"/>; compared by reference.</param>
    /// <param name="material">The authored mesh's shared program material.</param>
    /// <param name="otherAssetSlots">The asset's additional local surfaces, compared by value and kept by reference.</param>
    /// <param name="usesRootMaterialFrame">Whether material layers use the copy root rather than the world frame.</param>
    /// <param name="templateRange">The static instance range that supplies local geometry, or <see langword="null"/>.</param>
    /// <returns>Whether the copy changed, its pose included.</returns>
    public bool Update(int assetSlot, object identity, object? motionIdentity, DynamicTransform transform, float scale, bool isDynamic, SdfSlotClip? clip, SdfMesh? mesh, int material, IReadOnlyList<int> otherAssetSlots, bool usesRootMaterialFrame = true, SdfMeshTemplateRange? templateRange = null) {
        var moved = (m_transform != transform);

        if (Holds(assetSlot, identity, motionIdentity, scale, isDynamic, clip, mesh, material, otherAssetSlots, usesRootMaterialFrame, templateRange)) {
            if (!moved) { return false; }
            m_transform = transform;
            m_matrix = Compose(transform: transform, scale: scale);
            if (!isDynamic) { Revision++; }
            return true;
        }
        m_assetSlot = assetSlot;
        m_identity = identity;
        m_motionIdentity = motionIdentity;
        m_transform = transform;
        m_scale = scale;
        m_isDynamic = isDynamic;
        m_clip = clip;
        m_mesh = mesh;
        m_material = material;
        m_otherAssetSlots = otherAssetSlots;
        m_usesRootMaterialFrame = usesRootMaterialFrame;
        m_templateRange = templateRange;
        m_matrix = Compose(transform: transform, scale: scale);
        Revision++;
        return true;
    }
    private bool Holds(int assetSlot, object identity, object? motionIdentity, float scale, bool isDynamic, SdfSlotClip? clip, SdfMesh? mesh, int material, IReadOnlyList<int> otherAssetSlots, bool usesRootMaterialFrame, SdfMeshTemplateRange? templateRange) {
        if ((AssetSlot != assetSlot) || !Equals(objA: Identity, objB: identity) || !Equals(objA: MotionIdentity, objB: motionIdentity) || (Scale != scale) || (IsDynamic != isDynamic) || (Clip != clip) || !ReferenceEquals(objA: Mesh, objB: mesh) || (Material != material) || (OtherAssetSlots.Count != otherAssetSlots.Count) || (UsesRootMaterialFrame != usesRootMaterialFrame) || (TemplateRange != templateRange)) { return false; }
        for (var index = 0; (index < OtherAssetSlots.Count); index++) {
            if (OtherAssetSlots[index] != otherAssetSlots[index]) { return false; }
        }
        return true;
    }
}
