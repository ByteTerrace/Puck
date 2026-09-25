using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanBufferApi"/>: exclusive buffer creation with its requirements query,
/// memory-type selection, allocation and bind, host mapping, and the matching destroy, through the device's command table.
/// </summary>
public sealed unsafe class VulkanNativeBufferApi : IVulkanBufferApi {
    private const uint MemoryPropertyDeviceLocalBit = 0x00000001;
    private const uint MemoryPropertyHostCoherentBit = 0x00000004;
    private const uint MemoryPropertyHostVisibleBit = 0x00000002;
    private const uint SharingModeExclusive = 0;
    private const uint StructureTypeBufferCreateInfo = 12;
    private const uint StructureTypeMemoryAllocateInfo = 5;

    /// <summary>Returns the <c>VkMemoryPropertyFlagBits</c> a memory type must carry for <paramref name="memory"/>,
    /// whether a type without them fails the creation rather than falling back to the first permitted type, and the
    /// role the allocation is counted by, which the memory type the driver chooses never changes.</summary>
    /// <param name="memory">The memory a buffer is allocated from.</param>
    /// <returns>The preferred properties, whether they are required, and the allocation's role.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="memory"/> is not a defined value.</exception>
    public static (uint PreferredProperties, bool RequireProperties, GpuMemoryRole Role) MemoryProperties(VulkanBufferMemory memory) => memory switch {
        VulkanBufferMemory.HostCoherent => (MemoryPropertyHostVisibleBit | MemoryPropertyHostCoherentBit, true, GpuMemoryRole.HostVisible),
        VulkanBufferMemory.DeviceLocal => (MemoryPropertyDeviceLocalBit, true, GpuMemoryRole.DeviceLocal),
        VulkanBufferMemory.PreferDeviceLocal => (MemoryPropertyDeviceLocalBit, false, GpuMemoryRole.DeviceLocal),
        _ => throw new ArgumentOutOfRangeException(
            actualValue: memory,
            message: "The buffer memory is not a defined value.",
            paramName: nameof(memory)
        ),
    };
    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sizeBytes"/> is zero, or <paramref name="memory"/> is not a defined value.</exception>
    /// <exception cref="VulkanException">A native creation, allocation, or bind call failed.</exception>
    /// <exception cref="InvalidOperationException">The buffer came back without a handle, or no compatible memory type was found.</exception>
    public VulkanBufferHandles Create(IVulkanDeviceContext device, uint usage, VulkanBufferMemory memory, ulong sizeBytes) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentOutOfRangeException.ThrowIfZero(value: sizeBytes);

        var (preferredProperties, requireProperties, role) = MemoryProperties(memory: memory);
        var commands = device.LogicalDevice.Commands;
        var instance = device.Instance.Commands;
        var createInfo = new VkBufferCreateInfo {
            SType = StructureTypeBufferCreateInfo,
            SharingMode = SharingModeExclusive,
            Size = sizeBytes,
            Usage = usage,
        };

        commands.CreateBuffer(
            commands.Handle,
            in createInfo,
            0,
            out var bufferHandle
        ).ThrowIfFailed(operation: "vkCreateBuffer");

        if (0 == bufferHandle) {
            throw new InvalidOperationException(message: $"vkCreateBuffer returned success without a valid handle for a buffer of usage 0x{usage:X8}.");
        }

        var memoryHandle = ((nint)0);

        try {
            commands.GetBufferMemoryRequirements(
                commands.Handle,
                bufferHandle,
                out var memoryRequirements
            );
            instance.GetPhysicalDeviceMemoryProperties(
                device.LogicalDevice.PhysicalDevice.Handle,
                out var memoryProperties
            );

            var allocateInfo = new VkMemoryAllocateInfo {
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = VulkanMemoryTypes.FindIndex(
                    memoryProperties: in memoryProperties,
                    memoryTypeBits: memoryRequirements.MemoryTypeBits,
                    preferredProperties: preferredProperties,
                    requireProperties: requireProperties,
                    resourceDescription: $"a buffer of usage 0x{usage:X8}"
                ),
                SType = StructureTypeMemoryAllocateInfo,
            };

            commands.AllocateMemory(
                commands.Handle,
                in allocateInfo,
                0,
                out memoryHandle
            ).ThrowIfFailed(operation: "vkAllocateMemory");
            commands.CountAllocated(
                allocationSize: allocateInfo.AllocationSize,
                memoryHandle: memoryHandle,
                role: role
            );
            commands.BindBufferMemory(
                commands.Handle,
                bufferHandle,
                memoryHandle,
                0
            ).ThrowIfFailed(operation: "vkBindBufferMemory");
            return new VulkanBufferHandles(
                Buffer: bufferHandle,
                Device: commands,
                Memory: memoryHandle
            );
        } catch {
            commands.Destroy(
                destroy: commands.DestroyBuffer,
                handle: bufferHandle,
                memoryHandle: memoryHandle
            );
            throw;
        }
    }
    /// <inheritdoc/>
    public void Destroy(VulkanBufferHandles handles) =>
        handles.Device.Destroy(
            destroy: handles.Device.DestroyBuffer,
            handle: handles.Buffer,
            memoryHandle: handles.Memory
        );
    /// <inheritdoc/>
    /// <exception cref="VulkanException">The native memory mapping call failed.</exception>
    public nint Map(VulkanBufferHandles handles, ulong sizeBytes) {
        handles.Device.MapMemory(
            handles.Device.Handle,
            handles.Memory,
            0,
            checked((nuint)sizeBytes),
            0,
            out var mappedMemory
        ).ThrowIfFailed(operation: "vkMapMemory");

        return mappedMemory;
    }
    /// <inheritdoc/>
    public void Unmap(VulkanBufferHandles handles) =>
        handles.Device.UnmapMemory(
            handles.Device.Handle,
            handles.Memory
        );
}
