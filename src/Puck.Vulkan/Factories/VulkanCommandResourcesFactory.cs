using System.Runtime.InteropServices;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanCommandResourcesFactory"/>: it creates a command pool for the graphics
/// queue family, allocates the requested number of command buffers, and returns an owning
/// <see cref="VulkanCommandResources"/>.
/// </summary>
public sealed class VulkanCommandResourcesFactory : IVulkanCommandResourcesFactory {
    private readonly IVulkanCommandResourcesApi m_commandResourcesApi;

    /// <summary>Initializes a new instance of the <see cref="VulkanCommandResourcesFactory"/> class.</summary>
    /// <param name="commandResourcesApi">The command-resources API used to create the pool and allocate buffers.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commandResourcesApi"/> is <see langword="null"/>.</exception>
    public VulkanCommandResourcesFactory(IVulkanCommandResourcesApi commandResourcesApi) {
        ArgumentNullException.ThrowIfNull(argument: commandResourcesApi);

        m_commandResourcesApi = commandResourcesApi;
    }

    /// <inheritdoc/>
    public VulkanCommandResources Create(
        VulkanLogicalDevice logicalDevice,
        uint commandBufferCount
    ) {
        ArgumentNullException.ThrowIfNull(argument: logicalDevice);

        var poolRequest = new VulkanCommandPoolCreateRequest(
            DeviceHandle: logicalDevice.Handle,
            QueueFamilyIndex: logicalDevice.GraphicsQueue.FamilyIndex
        );
        var createPoolResult = m_commandResourcesApi.CreateCommandPool(
            commandPoolHandle: out var commandPoolHandle,
            request: poolRequest
        );

        createPoolResult.ThrowIfFailed(operation: "vkCreateCommandPool");

        if (0 == commandPoolHandle) {
            throw new InvalidOperationException(message: "vkCreateCommandPool returned success without a valid command-pool handle.");
        }

        var buffer = IntPtr.Zero;

        try {
            buffer = Memory.Allocator.Alloc(size: (IntPtr.Size * checked((int)commandBufferCount)));

            var allocateRequest = new VulkanCommandBufferAllocateRequest(
                CommandBufferCount: commandBufferCount,
                CommandPoolHandle: commandPoolHandle,
                DeviceHandle: logicalDevice.Handle
            );
            var allocateResult = m_commandResourcesApi.AllocateCommandBuffers(
                buffer: buffer,
                commandBufferCount: commandBufferCount,
                request: allocateRequest
            );

            allocateResult.ThrowIfFailed(operation: "vkAllocateCommandBuffers");

            var commandBufferHandles = new nint[commandBufferCount];

            for (var index = 0; (index < commandBufferHandles.Length); ++index) {
                commandBufferHandles[index] = Marshal.ReadIntPtr(
                    ofs: (index * IntPtr.Size),
                    ptr: buffer
                );

                if (0 == commandBufferHandles[index]) {
                    throw new InvalidOperationException(message: "vkAllocateCommandBuffers returned success without a valid command-buffer handle.");
                }
            }

            return new(
                commandBufferHandles: commandBufferHandles,
                commandPoolHandle: commandPoolHandle,
                commandResourcesApi: m_commandResourcesApi,
                deviceHandle: logicalDevice.Handle
            );
        } catch {
            m_commandResourcesApi.DestroyCommandPool(
                commandPoolHandle: commandPoolHandle,
                deviceHandle: logicalDevice.Handle
            );
            throw;
        } finally {
            if (0 != buffer) {
                Memory.Allocator.Free(ptr: buffer);
            }
        }
    }
}
