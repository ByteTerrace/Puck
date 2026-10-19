namespace Puck.Vulkan.Bindings;

/// <summary>
/// One checkpoint a queue reached (<c>VkCheckpointDataNV</c>): the pipeline stage it reached it at and the marker
/// <c>vkCmdSetCheckpointNV</c> recorded.
/// </summary>
public unsafe struct VkCheckpointDataNv {
    /// <summary>The structure type, <c>VK_STRUCTURE_TYPE_CHECKPOINT_DATA_NV</c>.</summary>
    public uint SType;
    /// <summary>The extension chain; null.</summary>
    public void* PNext;
    /// <summary>The single pipeline stage the checkpoint was reached at.</summary>
    public uint Stage;
    /// <summary>The marker the checkpoint recorded.</summary>
    public nint PCheckpointMarker;
}
