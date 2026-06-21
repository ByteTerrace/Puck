namespace Puck.Abstractions;

/// <summary>
/// Validated instanced-draw parameters: the vertex and instance counts must be non-zero, with optional first
/// vertex and instance offsets. Backend-neutral; each backend maps these onto its native draw call.
/// </summary>
public readonly record struct GpuDrawParameters {
    /// <summary>Gets the index of the first instance to draw.</summary>
    public uint FirstInstance { get; }
    /// <summary>Gets the index of the first vertex to draw from.</summary>
    public uint FirstVertex { get; }
    /// <summary>Gets the number of instances to draw.</summary>
    public uint InstanceCount { get; }
    /// <summary>Gets the number of vertices per instance.</summary>
    public uint VertexCount { get; }

    /// <summary>Initializes a new instance of the <see cref="GpuDrawParameters"/> struct.</summary>
    /// <param name="vertexCount">The number of vertices per instance. Must be non-zero.</param>
    /// <param name="instanceCount">The number of instances. Must be non-zero.</param>
    /// <param name="firstVertex">The index of the first vertex to draw.</param>
    /// <param name="firstInstance">The index of the first instance to draw.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="vertexCount"/> or <paramref name="instanceCount"/> is zero.</exception>
    public GpuDrawParameters(uint vertexCount, uint instanceCount, uint firstVertex = 0, uint firstInstance = 0) {
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

        FirstInstance = firstInstance;
        FirstVertex = firstVertex;
        InstanceCount = instanceCount;
        VertexCount = vertexCount;
    }
}
