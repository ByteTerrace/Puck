using System.Diagnostics.CodeAnalysis;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// A Direct3D 12 shared fence imported into a Vulkan device as a timeline semaphore
/// (<c>VK_KHR_external_semaphore_win32</c>, <c>VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_D3D12_FENCE_BIT</c>): the semaphore's
/// value is the fence's, so a submission that waits for a value in its wait list starts once the producer on the other
/// device signals it. A submission waits on it through <see cref="IGpuQueueSubmitter.AddExternalWait"/>. Owns the
/// semaphore, destroyed on disposal; the import never takes the NT handle.
/// </summary>
public sealed unsafe class VulkanSharedFence : IGpuSharedFence {
    private const uint StructureTypeSemaphoreCreateInfo = 9;

    private readonly VulkanDeviceCommands m_device;

    private nint m_semaphore;

    private VulkanSharedFence(VulkanDeviceCommands device, nint semaphore) {
        m_device = device;
        m_semaphore = semaphore;
    }

    /// <summary>Imports a Direct3D 12 shared fence's NT handle into a new timeline semaphore on a device.</summary>
    /// <param name="device">The command table of the device the semaphore is created on.</param>
    /// <param name="sharedHandle">The fence's shared NT handle; stays owned by the caller.</param>
    /// <param name="fence">When this returns <see langword="true"/>, the imported fence, owned by the caller.</param>
    /// <param name="refusal">When this returns <see langword="false"/>, why the device cannot import it: the
    /// extension it was created without, or the call that failed and its result; empty otherwise.</param>
    /// <returns>Whether the fence was imported.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    public static bool TryImport(VulkanDeviceCommands device, nint sharedHandle, [NotNullWhen(true)] out VulkanSharedFence? fence, out string refusal) {
        ArgumentNullException.ThrowIfNull(argument: device);

        fence = null;

        if (device.ImportSemaphoreWin32HandleKhr is null) {
            refusal = "the Vulkan device was created without VK_KHR_external_semaphore_win32";

            return false;
        }

        var typeInfo = new VkSemaphoreTypeCreateInfo {
            InitialValue = 0,
            SemaphoreType = VkSemaphoreTypeCreateInfo.Timeline,
            SType = VkSemaphoreTypeCreateInfo.StructureType,
        };
        var createInfo = new VkSemaphoreCreateInfo {
            PNext = ((nint)(&typeInfo)),
            SType = StructureTypeSemaphoreCreateInfo,
        };
        var created = device.CreateSemaphore(
            device.Handle,
            in createInfo,
            0,
            out var semaphore
        );

        if (created != VkResult.Success) {
            refusal = $"vkCreateSemaphore refused a timeline semaphore ({created})";

            return false;
        }

        var importInfo = new VkImportSemaphoreWin32HandleInfoKhr {
            Handle = sharedHandle,
            HandleType = VkImportSemaphoreWin32HandleInfoKhr.D3D12FenceHandleType,
            Semaphore = semaphore,
            SType = VkImportSemaphoreWin32HandleInfoKhr.StructureType,
        };
        var imported = device.ImportSemaphoreWin32HandleKhr(
            device.Handle,
            in importInfo
        );

        if (imported != VkResult.Success) {
            device.Destroy(
                destroy: device.DestroySemaphore,
                handle: semaphore
            );
            refusal = $"vkImportSemaphoreWin32HandleKHR refused the Direct3D 12 fence ({imported})";

            return false;
        }

        fence = new VulkanSharedFence(
            device: device,
            semaphore: semaphore
        );
        refusal = "";

        return true;
    }

    /// <summary>Gets the native timeline <c>VkSemaphore</c> handle, or zero once disposed.</summary>
    public nint SemaphoreHandle => m_semaphore;

    /// <inheritdoc/>
    /// <remarks>Reads <c>vkGetSemaphoreCounterValue</c>; <c>VK_ERROR_DEVICE_LOST</c> surfaces as
    /// <see cref="DeviceLostException"/>.</remarks>
    public ulong CompletedValue {
        get {
            ObjectDisposedException.ThrowIf(
                condition: (0 == m_semaphore),
                instance: this
            );
            m_device.GetSemaphoreCounterValue(
                m_device.Handle,
                m_semaphore,
                out var value
            ).ThrowIfFailed(operation: "vkGetSemaphoreCounterValue");

            return value;
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        m_device.Destroy(
            destroy: m_device.DestroySemaphore,
            handle: m_semaphore
        );
        m_semaphore = 0;
    }
}
