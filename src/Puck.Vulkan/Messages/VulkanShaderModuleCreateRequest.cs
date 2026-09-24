using Puck.Vulkan.Interop;
namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a shader module to create from SPIR-V byte code.
/// </summary>
/// <param name="Device">The command table of the logical device.</param>
/// <param name="SpirVBytes">The SPIR-V byte code the module is created from.</param>
public readonly record struct VulkanShaderModuleCreateRequest(VulkanDeviceCommands Device, ReadOnlyMemory<byte> SpirVBytes);
