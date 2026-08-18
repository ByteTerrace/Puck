namespace Puck.Abstractions.Gpu;

/// <summary>The per-vertex-buffer input layout of a graphics pipeline.</summary>
/// <param name="StrideBytes">The byte stride between consecutive vertices.</param>
/// <param name="Attributes">The vertex attributes, in binding order.</param>
public sealed record GpuVertexInputLayout(uint StrideBytes, IReadOnlyList<GpuVertexAttribute> Attributes);
