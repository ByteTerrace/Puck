using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Chained to <see cref="VkSemaphoreCreateInfo"/> to choose a binary or timeline semaphore and a timeline's initial
/// value.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkSemaphoreTypeCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkSemaphoreTypeCreateInfo {
    /// <summary>The <c>VK_STRUCTURE_TYPE_SEMAPHORE_TYPE_CREATE_INFO</c> structure type.</summary>
    public const uint StructureType = 1000207002;
    /// <summary>The <c>VK_SEMAPHORE_TYPE_TIMELINE</c> semaphore type.</summary>
    public const uint Timeline = 1;

    /// <summary>The type of this structure (<see cref="StructureType"/>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The <c>VkSemaphoreType</c>: binary (0) or <see cref="Timeline"/>.</summary>
    public uint SemaphoreType;
    /// <summary>A timeline semaphore's initial value; zero for a binary one.</summary>
    public ulong InitialValue;
}
