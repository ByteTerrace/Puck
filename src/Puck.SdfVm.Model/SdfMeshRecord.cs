namespace Puck.SdfVm;

/// <summary>
/// The word layout of one draw's record in the mesh region (<c>SdfMeshRegion</c>), which the region's writer writes and
/// the mesh, surface and views passes read (<c>frame/sdf-mesh.hlsli</c>); <see cref="SdfIsaHlsl"/> writes every offset and
/// flag into the kernels. A record is <see cref="DrawWords"/> words: its pose (<see cref="PoseWords"/> words), the
/// material, the word the mesh's first index sits at, its index count, the word its first vertex sits at, its attribute
/// flags, the word its first triangle material sits at, the frame slot or -1, the word the mesh's members sit at, the
/// carry every world position and normal the draw resolves goes through last (a unit rotation quaternion, a translation,
/// then the uniform scale a position takes before the translation), the world-space clip plane (<c>dot(n, x) + d</c>, a
/// point with a negative value not drawn; all zeros draws everything), the shared level table's address and count, the
/// draw's bounds and selection metadata, the handle of the heights the draw is displaced by
/// (<see cref="DisplacementWord"/>), and the detail band the draw stands in (<see cref="BandWord"/>).
/// <para>
/// A draw with a frame slot rides it: its object-to-world transform is the slot's row of the frame's dynamic transforms
/// (position and orientation) at the scale its record holds (<see cref="ScaleWord"/>), so a body that moves rewrites its
/// slot's row and no record, and the kernels derive its matrix, its normal matrix and its world box from the row. A draw
/// with no frame slot holds its affine matrix in its pose words, and the kernels derive its normal matrix from it.
/// </para>
/// </summary>
public static class SdfMeshRecord {
    /// <summary>The leading pose words, also the compact previous-pose stride: a draw with no frame slot holds its
    /// row-vector object-to-world matrix's three basis rows and then its translation (each three words, the matrix's
    /// fourth column being 0, 0, 0, 1); a draw riding its frame slot holds its scale (<see cref="ScaleWord"/>) and
    /// zeros.</summary>
    public const int PoseWords = 12;
    /// <summary>The pose word a draw riding its frame slot holds its uniform scale in, as a float: the length its
    /// object-space unit takes in the world before the carry.</summary>
    public const int ScaleWord = 0;
    /// <summary>The words of one draw's record.</summary>
    public const int DrawWords = 58;
    /// <summary>The handle, in the frame's sampled table (<see cref="SdfFrameSampled"/>), of the heights a height field's
    /// draw displaces its baked reference by, or <see cref="SdfFrameSampled.None"/> for a draw drawn as it was
    /// baked.</summary>
    public const int DisplacementWord = 50;
    /// <summary>The first of the detail band's words (<c>SdfMeshBand</c>): the sphere's object-space center and radius
    /// as floats, the pixel the group's floor stands at as a float (positive for a fine draw, negative for a floor, zero
    /// for a draw every pass draws), then, for a fine draw, the first staged row of its placement's floor and how many
    /// rows the floor takes.</summary>
    public const int BandWord = 51;
    /// <summary>The words of the detail band (<see cref="BandWord"/>).</summary>
    public const int BandWords = 7;
    /// <summary>The first of four four-word rows used by GPU draw selection: center and flags; half X and stretch;
    /// half Y and staged instance generation; half Z and level count. The box is in world space, or, for a record
    /// carrying <see cref="SlotBoxFlag"/>, in its frame slot's frame before the slot's pose, the record's scale and its
    /// carry.</summary>
    public const int SelectionWord = 34;
    /// <summary>The selection flag for a row with no standing mesh. Its other words, including generation and
    /// placement, are zero; selection rejects it before reading them and the hit resolver rejects its empty indices.</summary>
    public const uint AbsentFlag = 8u;
    /// <summary>The selection flag for a draw outside the static scene (<c>SdfMeshDraw.IsStatic</c>): one that
    /// moves or animates, or glass. A pass drawing static casters alone (the distant shadow map) rejects it.</summary>
    public const uint DynamicFlag = 16u;
    /// <summary>The selection flag for a box held in the draw's frame slot's frame: a draw riding its slot, whose world
    /// box every reader derives from the slot's row as its vertices are (<c>sdfSelectionBox</c>, <c>SdfMeshBox.Ridden</c>).</summary>
    public const uint SlotBoxFlag = 32u;
    /// <summary>The first of two words: the absolute word of the mesh's shared level table, then its coarser level count.
    /// Each level, zero included, stores its first triangle, vertex count and error as three words.</summary>
    public const int LevelTableWord = 32;
    /// <summary>The first of the carry rotation's four words (x, y, z, w).</summary>
    public const int CarryRotationWord = 20;
    /// <summary>The first of the carry translation's three words.</summary>
    public const int CarryTranslationWord = 24;
    /// <summary>The carry's uniform scale, as a float: one for a draw drawn where it stands.</summary>
    public const int CarryScaleWord = 27;
    /// <summary>The first of the clip plane's four words (the normal, then the offset).</summary>
    public const int ClipPlaneWord = 28;
    /// <summary>The record's material.</summary>
    public const int MaterialWord = 12;
    /// <summary>The word the mesh's first index sits at.</summary>
    public const int IndexWord = 13;
    /// <summary>The mesh's index count.</summary>
    public const int IndexCountWord = 14;
    /// <summary>The word the mesh's first vertex sits at.</summary>
    public const int VertexWord = 15;
    /// <summary>The mesh's attribute flags (<see cref="NormalsFlag"/>, <see cref="MaterialsFlag"/>,
    /// <see cref="SkinFlag"/>, <see cref="WorldMaterialFlag"/>, <see cref="ClipFlag"/>) in its low bits, and the draw's
    /// object key from <see cref="ObjectKeyShift"/> up.</summary>
    public const int FlagsWord = 16;
    /// <summary>The bit of <see cref="FlagsWord"/> the draw's object key starts at (<c>SdfMeshDraw.ObjectKey</c>): what a
    /// view's edge search compares to find where two objects meet, the same for every draw of one object, a chunk of its
    /// mesh included, so the search can see no draw.</summary>
    public const int ObjectKeyShift = 8;
    /// <summary>The object key's largest value: the bits a visibility record keeps of it
    /// (<see cref="SdfVisibility.ObjectKeyBits"/>), which the flags word holds above <see cref="ObjectKeyShift"/>.</summary>
    public const uint ObjectKeyMask = ((1u << SdfVisibility.ObjectKeyBits) - 1u);
    /// <summary>The flag a record carries when its draw is clipped by a plane (a nonzero <see cref="ClipPlaneWord"/>),
    /// so a reader that only asks whether a point lies beside the cut reads the plane for such a draw alone.</summary>
    public const uint ClipFlag = 16u;
    /// <summary>The word the mesh's first triangle material sits at.</summary>
    public const int TriangleMaterialWord = 17;
    /// <summary>The dynamic-transform slot the draw rides and whose frame the mesh is stored in, or -1.</summary>
    public const int FrameSlotWord = 18;
    /// <summary>The word the mesh's members sit at.</summary>
    public const int SkinWord = 19;
    /// <summary>The words of one vertex: its position and its normal, as floats.</summary>
    public const int VertexWords = 6;
    /// <summary>The words of one member in a mesh's members: its slot offset, its position and its orientation.</summary>
    public const int MemberWords = 8;
    /// <summary>The words of one skinned vertex: first member, second member and share, and three half gradients with padding.</summary>
    public const int SkinVertexWords = 4;
    /// <summary>The flag a record carries when its mesh has a normal a vertex.</summary>
    public const uint NormalsFlag = 1u;
    /// <summary>The flag a record carries when its mesh has a palette entry a triangle.</summary>
    public const uint MaterialsFlag = 2u;
    /// <summary>The flag a record carries when its mesh has a member a vertex.</summary>
    public const uint SkinFlag = 4u;
    /// <summary>The flag selecting world-space material coordinates independently of the geometry root slot.</summary>
    public const uint WorldMaterialFlag = 8u;
}
