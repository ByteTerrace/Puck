using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>
/// The mesh region's words read as slabs: the streaming plan's indexed read, the prototype that prices backing the meshes
/// with fixed-size buffers before the region's allocator changes. A mesh word's slab is its word index over
/// <see cref="Words"/> and its place in the slab the remainder; the region is bound once more as <see cref="Count"/>
/// views (<see cref="SdfWorldPackage.MeshSlabs"/>), slab <c>k</c> the region's <see cref="Bytes"/> from <c>k</c> times
/// that, and a kernel reads a mesh word through the view its slab names. The HLSL twin is
/// <c>frame/sdf-mesh-words.hlsli</c>'s <c>sdfMeshWord</c>, the one door every kernel reads a mesh's vertices, indices,
/// triangle materials, level table and members through; the draw records stay one direct table, as they would beside
/// real slabs.
/// <para><see cref="IndexedRead"/> chooses the read: off, <c>sdfMeshWord</c> reads the one region binding directly, as
/// the frame always has; on, it indexes the views by slab with <c>NonUniformResourceIndex</c>, a different slab in each
/// lane of a wave that shades several draws. The views are bound either way, so the two builds differ only in the
/// kernels' reads, which is what the comparison prices.</para>
/// </summary>
public static class SdfMeshSlabs {
    /// <summary>Whether the kernels read mesh words through the slab views (<see langword="true"/>) or the one region
    /// binding (<see langword="false"/>), generated into the kernels as <c>SDF_MESH_SLAB_INDEX</c>. A build of each is
    /// the comparison the backing choice rests on.</summary>
    public const bool IndexedRead = false;
    /// <summary>The base-two logarithm of <see cref="Words"/>, generated as <c>SDF_MESH_SLAB_WORD_SHIFT</c>.</summary>
    public const int WordShift = 24;
    /// <summary>The words of one slab: 16 Mi words, 64 MiB.</summary>
    public const long Words = (1L << WordShift);
    /// <summary>The bytes of one slab.</summary>
    public const long Bytes = (Words * sizeof(uint));
    /// <summary>The views the region is bound as: enough slabs to cover the largest region
    /// (<see cref="GpuRegion.MaxByteCount"/>), the descriptor count every set that binds the region pays for them.</summary>
    public const int Count = ((int)((GpuRegion.MaxByteCount + (Bytes - 1L)) / Bytes));

    /// <summary>Returns the slab a mesh word lies in.</summary>
    /// <param name="word">The word's index in the region.</param>
    /// <returns>The slab: <paramref name="word"/> over <see cref="Words"/>.</returns>
    public static int SlabOf(long word) => ((int)(word >> WordShift));
    /// <summary>Returns a mesh word's index within its slab.</summary>
    /// <param name="word">The word's index in the region.</param>
    /// <returns>The remainder of <paramref name="word"/> over <see cref="Words"/>.</returns>
    public static long LocalOf(long word) => (word & (Words - 1L));
    /// <summary>Returns the part of a region a slab's view covers.</summary>
    /// <param name="slab">The slab, below <see cref="Count"/>.</param>
    /// <param name="regionBytes">The region buffer's size in bytes.</param>
    /// <param name="offsetBytes">The view's first byte in the region; a multiple of <see cref="Bytes"/>, so of every
    /// device's storage-buffer offset alignment.</param>
    /// <param name="bytes">The view's size in bytes: a whole slab, or what remains of the region past
    /// <paramref name="offsetBytes"/>.</param>
    /// <returns><see langword="true"/> when the slab starts inside the region; <see langword="false"/> for a slab past
    /// its end, which a set binds a filler at.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slab"/> is negative or not below
    /// <see cref="Count"/>.</exception>
    public static bool TryView(int slab, ulong regionBytes, out ulong offsetBytes, out ulong bytes) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: slab);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: slab, other: Count);

        offsetBytes = (((ulong)slab) * ((ulong)Bytes));
        bytes = ((offsetBytes < regionBytes) ? Math.Min(val1: (ulong)Bytes, val2: (regionBytes - offsetBytes)) : 0UL);

        return (bytes != 0UL);
    }
}
