namespace Puck.SdfVm;

/// <summary>What a visibility identity says a pixel sees.</summary>
public enum SdfVisibilityKind : uint {
    /// <summary>Nothing: the sky.</summary>
    Background = 0,
    /// <summary>A program instance; the source is its ordinal plus one. No visibility record carries it: the host's pick
    /// map names the program's instances by it.</summary>
    Sdf = 1,
    /// <summary>A baked mesh; the source is its draw ordinal.</summary>
    Mesh = 2,
}
/// <summary>
/// The host's statement of the visibility record's shared words: the identity's kind and source fields and the edge of
/// the square workgroup the passes that write and shade the records dispatch over the render extent.
/// <see cref="SdfIsaHlsl"/> generates the kernels' spellings of every one of them into <c>sdf-isa.hlsli</c>, so a kernel
/// and a pick read one definition. The surface pass writes every record of the render extent each frame it runs, misses
/// included, so every record inside the extent is current.
/// </summary>
public static partial class SdfVisibility {
    /// <summary>The bit the identity's kind starts at; the source fills the bits below it.</summary>
    public const int KindShift = 30;
    /// <summary>The identity's source field.</summary>
    public const uint SourceMask = ((1U << KindShift) - 1U);
    /// <summary>The edge, in pixels, of the square workgroup the surface and views passes dispatch over the render
    /// extent.</summary>
    public const uint GroupEdgePixels = 8;
    /// <summary>The scale of the record's normal word: two signed-normalized 16-bit octahedral coordinates
    /// (<c>SdfOctahedral</c>, the kernels' <c>sdfOctEncode</c>), X in bits 0 to 15 and Y in bits 16 to 31, each its
    /// coordinate times this, rounded.</summary>
    public const float NormalScale = 32767f;
    /// <summary>The normal word of a record with no normal: the reserved coordinate pair (-32768, -32768), which no
    /// encoded direction produces.</summary>
    public const uint ZeroNormal = 0x8000_8000u;

    /// <summary>Packs an identity.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="source">The source; only its <see cref="SourceMask"/> bits are kept, as the kernels keep them.</param>
    /// <returns>The identity.</returns>
    public static uint IdentityOf(SdfVisibilityKind kind, uint source) => (((uint)kind) << KindShift) | (source & SourceMask);
    /// <summary>Reads an identity's kind.</summary>
    /// <param name="identity">The identity.</param>
    /// <returns>The kind.</returns>
    public static SdfVisibilityKind KindOf(uint identity) => ((SdfVisibilityKind)(identity >> KindShift));
    /// <summary>Reads an identity's source.</summary>
    /// <param name="identity">The identity.</param>
    /// <returns>The source.</returns>
    public static uint SourceOf(uint identity) => identity & SourceMask;
}
