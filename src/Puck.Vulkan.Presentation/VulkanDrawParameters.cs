using Puck.Abstractions.Presentation;

namespace Puck.Vulkan.Presentation;

public readonly record struct VulkanDrawParameters {
    public uint FirstInstance { get; }
    public uint FirstVertex { get; }
    public uint InstanceCount { get; }
    public uint VertexCount { get; }

    public VulkanDrawParameters(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance) {
        DrawCounts.RequireNonZero(
            instanceCount: instanceCount,
            vertexCount: vertexCount
        );

        VertexCount = vertexCount;
        InstanceCount = instanceCount;
        FirstVertex = firstVertex;
        FirstInstance = firstInstance;
    }
}
