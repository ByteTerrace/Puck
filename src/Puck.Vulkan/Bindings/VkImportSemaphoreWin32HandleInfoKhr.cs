using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a Win32 handle <c>vkImportSemaphoreWin32HandleKHR</c> imports into a semaphore's payload.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkImportSemaphoreWin32HandleInfoKHR (vulkan_win32.h, SDK 1.4): byte-identical layout, C#-idiomatic
/// field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkImportSemaphoreWin32HandleInfoKhr {
    /// <summary>The <c>VK_STRUCTURE_TYPE_IMPORT_SEMAPHORE_WIN32_HANDLE_INFO_KHR</c> structure type.</summary>
    public const uint StructureType = 1000078000;
    /// <summary>The <c>VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_D3D12_FENCE_BIT</c> handle type: an NT handle to a Direct3D 12
    /// shared fence, imported into a timeline semaphore whose value is the fence's.</summary>
    public const uint D3D12FenceHandleType = 0x00000008;

    /// <summary>The type of this structure (<see cref="StructureType"/>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The <c>VkSemaphore</c> whose payload is replaced.</summary>
    public nint Semaphore;
    /// <summary>The <c>VkSemaphoreImportFlags</c>; zero for a permanent import.</summary>
    public uint Flags;
    /// <summary>The <c>VkExternalSemaphoreHandleTypeFlagBits</c> of <see cref="Handle"/>.</summary>
    public uint HandleType;
    /// <summary>The Win32 handle imported; the import does not take ownership of an NT handle.</summary>
    public nint Handle;
    /// <summary>A null-terminated UTF-16 name of the payload, or <see langword="null"/> when a handle is given.</summary>
    public nint Name;
}