namespace Puck.Abstractions.Gpu;

/// <summary>The comparison a graphics pipeline's depth test passes a fragment by, comparing the fragment's depth with
/// the depth attachment's (Vulkan <c>VkCompareOp</c>; Direct3D 12 <c>D3D12_COMPARISON_FUNC</c>).</summary>
public enum GpuDepthCompare : uint {
    /// <summary>The fragment's depth is less.</summary>
    Less = 0,
    /// <summary>The fragment's depth is less or equal.</summary>
    LessOrEqual = 1,
    /// <summary>The fragment's depth is greater.</summary>
    Greater = 2,
    /// <summary>The fragment's depth is greater or equal.</summary>
    GreaterOrEqual = 3,
    /// <summary>The fragment's depth is equal.</summary>
    Equal = 4,
    /// <summary>Every fragment passes.</summary>
    Always = 5,
}
