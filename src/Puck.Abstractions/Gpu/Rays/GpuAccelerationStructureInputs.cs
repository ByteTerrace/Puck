namespace Puck.Abstractions.Gpu;

/// <summary>Which level of the two-level hierarchy a GPU's ray hardware walks an acceleration structure is.</summary>
public enum GpuAccelerationStructureLevel {
    /// <summary>A bottom level: one geometry's triangles, in the geometry's own frame.</summary>
    Bottom,
    /// <summary>A top level: instances, each a bottom level under a transform.</summary>
    Top,
}

/// <summary>
/// What one acceleration-structure build reads, as both backends describe it to their drivers: one opaque triangle
/// geometry for a bottom level, or an array of instance records for a top level. Every bottom level Puck builds holds
/// one geometry of 32-bit float positions and 32-bit indices, read in place from buffers created with
/// <see cref="GpuBufferUsage.AccelerationStructureInput"/>, and is built for fast tracing and compaction, since a baked
/// geometry never changes. A top level is built for fast tracing from instance records
/// <see cref="IGpuAccelerationStructures.WriteInstance"/> wrote, rebuilt whole when its scene changes.
/// </summary>
/// <param name="Level">The level built.</param>
/// <param name="Source">The buffer the build reads: the vertices and indices of a bottom level, the instance records of
/// a top level.</param>
/// <param name="VertexOffsetBytes">The byte offset of a bottom level's first vertex in <paramref name="Source"/>; each
/// vertex starts with three 32-bit floats.</param>
/// <param name="VertexStrideBytes">The bytes from one vertex to the next; a multiple of four.</param>
/// <param name="VertexCount">The vertices an index may name.</param>
/// <param name="IndexOffsetBytes">The byte offset of a bottom level's first 32-bit index in <paramref name="Source"/>,
/// a multiple of four, three a triangle, each naming a vertex from <paramref name="VertexOffsetBytes"/>.</param>
/// <param name="Count">The triangles of a bottom level, or the instances of a top level.</param>
/// <param name="InstanceOffsetBytes">The byte offset of a top level's first instance record in
/// <paramref name="Source"/>, a multiple of 16.</param>
public readonly record struct GpuAccelerationStructureInputs(
    GpuAccelerationStructureLevel Level,
    IGpuBuffer Source,
    uint Count,
    ulong VertexOffsetBytes = 0UL,
    uint VertexStrideBytes = 0U,
    uint VertexCount = 0U,
    ulong IndexOffsetBytes = 0UL,
    ulong InstanceOffsetBytes = 0UL
) {
    /// <summary>Returns a bottom level's inputs over one triangle geometry.</summary>
    /// <param name="source">The buffer holding the vertices and indices.</param>
    /// <param name="vertexOffsetBytes">The byte offset of the first vertex.</param>
    /// <param name="vertexStrideBytes">The bytes from one vertex to the next.</param>
    /// <param name="vertexCount">The vertices an index may name.</param>
    /// <param name="indexOffsetBytes">The byte offset of the first index.</param>
    /// <param name="triangleCount">The triangles, three indices each.</param>
    /// <returns>The inputs.</returns>
    public static GpuAccelerationStructureInputs Triangles(IGpuBuffer source, ulong vertexOffsetBytes, uint vertexStrideBytes, uint vertexCount, ulong indexOffsetBytes, uint triangleCount) =>
        new(Count: triangleCount, IndexOffsetBytes: indexOffsetBytes, Level: GpuAccelerationStructureLevel.Bottom, Source: source,
            VertexCount: vertexCount, VertexOffsetBytes: vertexOffsetBytes, VertexStrideBytes: vertexStrideBytes);
    /// <summary>Returns a top level's inputs over an array of instance records.</summary>
    /// <param name="source">The buffer holding the records.</param>
    /// <param name="instanceOffsetBytes">The byte offset of the first record.</param>
    /// <param name="instanceCount">The records.</param>
    /// <returns>The inputs.</returns>
    public static GpuAccelerationStructureInputs Instances(IGpuBuffer source, ulong instanceOffsetBytes, uint instanceCount) =>
        new(Count: instanceCount, InstanceOffsetBytes: instanceOffsetBytes, Level: GpuAccelerationStructureLevel.Top, Source: source);
}

/// <summary>What a build needs, as its driver reports it before the build: the structure's size and the scratch the
/// build works in.</summary>
/// <param name="StructureBytes">The bytes the built structure may occupy; a compacted copy is usually smaller.</param>
/// <param name="ScratchBytes">The bytes of scratch the build writes.</param>
public readonly record struct GpuAccelerationStructureSizes(ulong StructureBytes, ulong ScratchBytes);
