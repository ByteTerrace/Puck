namespace Puck.Vulkan.Presentation;

public readonly record struct VulkanDrawParameters {
    public uint FirstInstance { get; }
    public uint FirstVertex { get; }
    public uint InstanceCount { get; }
    public uint VertexCount { get; }

    public VulkanDrawParameters(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance) {
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

        VertexCount = vertexCount;
        InstanceCount = instanceCount;
        FirstVertex = firstVertex;
        FirstInstance = firstInstance;
    }
}
