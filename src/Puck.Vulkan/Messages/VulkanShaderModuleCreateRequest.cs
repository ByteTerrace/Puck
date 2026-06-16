namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a shader module to create from SPIR-V byte code.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="SpirVBytes">The SPIR-V byte code the module is created from.</param>
public readonly record struct VulkanShaderModuleCreateRequest(nint DeviceHandle, ReadOnlyMemory<byte> SpirVBytes);
