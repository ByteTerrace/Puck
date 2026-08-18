namespace Puck.Abstractions.Gpu;

/// <summary>One vertex attribute of a <see cref="GpuVertexInputLayout"/>.</summary>
/// <param name="Location">The shader input location.</param>
/// <param name="Format">The attribute's data format.</param>
/// <param name="OffsetBytes">The byte offset of the attribute within one vertex.</param>
public readonly record struct GpuVertexAttribute(uint Location, GpuVertexFormat Format, uint OffsetBytes);
