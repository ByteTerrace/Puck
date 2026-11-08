using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// The data a layer or the driver passes to a debug-utils messenger callback for one message: the message text, the
/// labels open when it was raised and the objects it concerns. A device-address binding report
/// (<see cref="Interop.VulkanDeviceAddressBindings"/>) reads its object and the binding its <see cref="Next"/> chain
/// carries.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkDebugUtilsMessengerCallbackDataEXT (vulkan_core.h, SDK 1.4): byte-identical layout, only ever
/// read through a pointer the loader supplies, never allocated managed-side.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkDebugUtilsMessengerCallbackDataExt {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value.</summary>
    public uint StructureType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint Next;
    /// <summary>A bitmask of <c>VkDebugUtilsMessengerCallbackDataFlagsEXT</c> (reserved; zero).</summary>
    public uint Flags;
    /// <summary>A null-terminated UTF-8 string naming the triggering message id, or <see langword="null"/>.</summary>
    public nint MessageIdName;
    /// <summary>The numeric id of the triggering message.</summary>
    public int MessageIdNumber;
    /// <summary>A null-terminated UTF-8 string with the human-readable message.</summary>
    public nint Message;
    /// <summary>The count of queue labels open when the message was raised.</summary>
    public uint QueueLabelCount;
    /// <summary>The open queue labels, or <see langword="null"/>.</summary>
    public nint QueueLabels;
    /// <summary>The count of command-buffer labels open when the message was raised.</summary>
    public uint CommandBufferLabelCount;
    /// <summary>The open command-buffer labels, or <see langword="null"/>.</summary>
    public nint CommandBufferLabels;
    /// <summary>The count of objects the message concerns.</summary>
    public uint ObjectCount;
    /// <summary>The objects the message concerns, or <see langword="null"/>.</summary>
    public VkDebugUtilsObjectNameInfoExt* Objects;
}
