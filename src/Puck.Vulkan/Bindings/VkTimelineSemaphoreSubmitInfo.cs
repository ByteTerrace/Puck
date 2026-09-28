using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Chained to <see cref="VkSubmitInfo"/> to give each timeline semaphore the batch waits on or signals its value, one
/// entry per semaphore in the same order.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkTimelineSemaphoreSubmitInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkTimelineSemaphoreSubmitInfo {
    /// <summary>The <c>VK_STRUCTURE_TYPE_TIMELINE_SEMAPHORE_SUBMIT_INFO</c> structure type.</summary>
    public const uint StructureType = 1000207003;

    /// <summary>The type of this structure (<see cref="StructureType"/>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The number of entries in <see cref="PWaitSemaphoreValues"/>, equal to the batch's wait count.</summary>
    public uint WaitSemaphoreValueCount;
    /// <summary>A pointer to the values each wait semaphore must reach; a binary semaphore's entry is ignored.</summary>
    public nint PWaitSemaphoreValues;
    /// <summary>The number of entries in <see cref="PSignalSemaphoreValues"/>, equal to the batch's signal count.</summary>
    public uint SignalSemaphoreValueCount;
    /// <summary>A pointer to the values each signal semaphore is set to; a binary semaphore's entry is ignored.</summary>
    public nint PSignalSemaphoreValues;
}
