using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports or requests the acceleration-structure features a physical device supports; chained into the
/// feature/device-creation query via <c>pNext</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPhysicalDeviceAccelerationStructureFeaturesKHR (vulkan_core.h, SDK 1.4): byte-identical layout,
/// C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPhysicalDeviceAccelerationStructureFeaturesKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ACCELERATION_STRUCTURE_FEATURES_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A <c>VkBool32</c>; whether acceleration structure functionality is supported.</summary>
    public uint AccelerationStructure;
    /// <summary>A <c>VkBool32</c>; whether saving and reusing acceleration structure device addresses for capture/replay is supported.</summary>
    public uint AccelerationStructureCaptureReplay;
    /// <summary>A <c>VkBool32</c>; whether indirect acceleration structure builds are supported.</summary>
    public uint AccelerationStructureIndirectBuild;
    /// <summary>A <c>VkBool32</c>; whether host-side acceleration structure build commands are supported.</summary>
    public uint AccelerationStructureHostCommands;
    /// <summary>A <c>VkBool32</c>; whether the update-after-bind descriptor binding flag is supported for acceleration structure descriptors.</summary>
    public uint DescriptorBindingAccelerationStructureUpdateAfterBind;
}
