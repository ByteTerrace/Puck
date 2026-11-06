namespace Puck.SdfVm;

/// <summary>
/// The word layout of one draw's record in the mesh region (<c>SdfMeshRegion</c>), which the region's writer writes and
/// the mesh, surface and views passes read (<c>frame/sdf-mesh.hlsli</c>); <see cref="SdfIsaHlsl"/> writes every offset and
/// flag into the kernels. A record is <see cref="DrawWords"/> words: the row-vector object-to-world matrix row by row
/// (words 0 to 15), the material, the word the mesh's first index sits at, its index count, the word its first vertex sits
/// at, its attribute flags, the word its first triangle material sits at, the normal matrix row by row (nine words), the
/// frame slot or -1, the word the mesh's members sit at, the carry every world position and normal the draw resolves
/// goes through last (a unit rotation quaternion, a translation, then the uniform scale a position takes before the
/// translation), the world-space clip plane (<c>dot(n, x) + d</c>, a point with a negative value not drawn; all zeros
/// draws everything), the shared level table's address and count, the draw's bounds and selection metadata, and the
/// handle of the heights the draw is displaced by (<see cref="DisplacementWord"/>).
/// </summary>
public static class SdfMeshRecord {
    /// <summary>The leading object-to-world matrix words, also the compact previous-pose stride.</summary>
    public const int MatrixWords = 16;
    /// <summary>The words of one draw's record.</summary>
    public const int DrawWords = 64;
    /// <summary>The handle, in the frame's sampled table (<see cref="SdfFrameSampled"/>), of the heights a height field's
    /// draw displaces its baked reference by, or <see cref="SdfFrameSampled.None"/> for a draw drawn as it was
    /// baked.</summary>
    public const int DisplacementWord = 63;
    /// <summary>The first of four four-word rows used by GPU draw selection: center and flags; half X and stretch;
    /// half Y and staged instance generation; half Z and level count.</summary>
    public const int SelectionWord = 47;
    /// <summary>The first of two words: the absolute word of the mesh's shared level table, then its coarser level count.
    /// Each level, zero included, stores its first triangle, vertex count and error as three words.</summary>
    public const int LevelTableWord = 45;
    /// <summary>The first of the carry rotation's four words (x, y, z, w).</summary>
    public const int CarryRotationWord = 33;
    /// <summary>The first of the carry translation's three words.</summary>
    public const int CarryTranslationWord = 37;
    /// <summary>The carry's uniform scale, as a float: one for a draw drawn where it stands.</summary>
    public const int CarryScaleWord = 40;
    /// <summary>The first of the clip plane's four words (the normal, then the offset).</summary>
    public const int ClipPlaneWord = 41;
    /// <summary>The record's material.</summary>
    public const int MaterialWord = 16;
    /// <summary>The word the mesh's first index sits at.</summary>
    public const int IndexWord = 17;
    /// <summary>The mesh's index count.</summary>
    public const int IndexCountWord = 18;
    /// <summary>The word the mesh's first vertex sits at.</summary>
    public const int VertexWord = 19;
    /// <summary>The mesh's attribute flags (<see cref="NormalsFlag"/>, <see cref="MaterialsFlag"/>,
    /// <see cref="SkinFlag"/>).</summary>
    public const int FlagsWord = 20;
    /// <summary>The word the mesh's first triangle material sits at.</summary>
    public const int TriangleMaterialWord = 21;
    /// <summary>The first of the normal matrix's nine words.</summary>
    public const int NormalMatrixWord = 22;
    /// <summary>The dynamic-transform slot whose frame the mesh is stored in, or -1.</summary>
    public const int FrameSlotWord = 31;
    /// <summary>The word the mesh's members sit at.</summary>
    public const int SkinWord = 32;
    /// <summary>The words of one vertex: its position and its normal, as floats.</summary>
    public const int VertexWords = 6;
    /// <summary>The words of one member in a mesh's members: its slot offset, its position and its orientation.</summary>
    public const int MemberWords = 8;
    /// <summary>The flag a record carries when its mesh has a normal a vertex.</summary>
    public const uint NormalsFlag = 1u;
    /// <summary>The flag a record carries when its mesh has a palette entry a triangle.</summary>
    public const uint MaterialsFlag = 2u;
    /// <summary>The flag a record carries when its mesh has a member a vertex.</summary>
    public const uint SkinFlag = 4u;
    /// <summary>The flag selecting world-space material coordinates independently of the geometry root slot.</summary>
    public const uint WorldMaterialFlag = 8u;
}
