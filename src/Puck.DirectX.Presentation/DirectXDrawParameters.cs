namespace Puck.DirectX.Presentation;

/// <summary>
/// Validated instanced-draw parameters for a <see cref="DirectXDrawCommand"/>: vertex and instance counts must
/// be non-zero, matching the invariant <c>DrawInstanced</c> requires.
/// </summary>
public readonly record struct DirectXDrawParameters {
    /// <summary>Gets the index of the first instance to draw.</summary>
    public uint StartInstanceLocation { get; }
    /// <summary>Gets the index of the first vertex in the vertex buffer to draw from.</summary>
    public uint StartVertexLocation { get; }
    /// <summary>Gets the number of instances to draw.</summary>
    public uint InstanceCount { get; }
    /// <summary>Gets the number of vertices per instance.</summary>
    public uint VertexCount { get; }

    /// <summary>Initializes a new instance of the <see cref="DirectXDrawParameters"/> struct.</summary>
    /// <param name="vertexCount">The number of vertices per instance. Must be non-zero.</param>
    /// <param name="instanceCount">The number of instances. Must be non-zero.</param>
    /// <param name="startVertexLocation">The index of the first vertex to draw.</param>
    /// <param name="startInstanceLocation">The index of the first instance to draw.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="vertexCount"/> or <paramref name="instanceCount"/> is zero.</exception>
    public DirectXDrawParameters(uint vertexCount, uint instanceCount, uint startVertexLocation = 0, uint startInstanceLocation = 0) {
        if (vertexCount == 0) {
            throw new ArgumentOutOfRangeException(
                actualValue: vertexCount,
                message: "Draw vertex count must be greater than zero.",
                paramName: nameof(vertexCount)
            );
        }

        if (instanceCount == 0) {
            throw new ArgumentOutOfRangeException(
                actualValue: instanceCount,
                message: "Draw instance count must be greater than zero.",
                paramName: nameof(instanceCount)
            );
        }

        InstanceCount = instanceCount;
        StartInstanceLocation = startInstanceLocation;
        StartVertexLocation = startVertexLocation;
        VertexCount = vertexCount;
    }
}
