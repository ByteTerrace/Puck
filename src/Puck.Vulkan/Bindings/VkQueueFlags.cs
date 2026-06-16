namespace Puck.Vulkan.Bindings;

/// <summary>
/// Specifies the capabilities of a queue family. Mirrors the <c>VkQueueFlagBits</c> bitmask.
/// </summary>
[Flags]
public enum VkQueueFlags : uint {
    /// <summary>No capabilities.</summary>
    None = 0,
    /// <summary>Queues in this family support graphics operations.</summary>
    Graphics = 0x00000001,
    /// <summary>Queues in this family support compute operations.</summary>
    Compute = 0x00000002,
    /// <summary>Queues in this family support transfer operations. Graphics- or compute-capable families implicitly support transfer.</summary>
    Transfer = 0x00000004,
    /// <summary>Queues in this family support sparse resource memory management operations.</summary>
    SparseBinding = 0x00000008,
    /// <summary>Queues in this family support protected memory operations.</summary>
    Protected = 0x00000010
}
