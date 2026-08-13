using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanFramePresentationApi"/>, marshaling to the acquire,
/// submit, and present entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeFramePresentationApi : IVulkanFramePresentationApi {
    private readonly IAllocator m_allocator;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativeFramePresentationApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public VulkanNativeFramePresentationApi(IAllocator allocator) {
        ArgumentNullException.ThrowIfNull(argument: allocator);

        m_allocator = allocator;
    }

    private const uint PipelineStageColorAttachmentOutputBit = 0x00000400;
    private const uint StructureTypePresentInfoKhr = 1000001001;
    // VK_STRUCTURE_TYPE_PRESENT_ID_KHR — the present-info struct chained into VkPresentInfoKhr.PNext.
    // Adjacent value 1000294001 is VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PRESENT_ID_FEATURES_KHR (the feature
    // struct, used by the device factory) — the two are trivially transposed; do not swap them.
    private const uint StructureTypePresentIdKhr = 1000294000;
    private const uint StructureTypeSubmitInfo = 4;

    /// <inheritdoc/>
    public VkResult AcquireNextImage(VulkanFrameAcquireRequest request, out uint imageIndex) {
        ValidateAcquireRequest(request: request);

        var acquireNextImage = GetPointers(deviceHandle: request.DeviceHandle).AcquireNextImageKhr;

        return acquireNextImage(
            request.DeviceHandle,
            request.SwapchainHandle,
            request.TimeoutNanoseconds,
            request.ImageAvailableSemaphoreHandle,
            request.InFlightFenceHandle,
            out imageIndex
        );
    }
    /// <inheritdoc/>
    public VkResult Present(VulkanPresentRequest request) {
        VulkanArgument.RequireHandle(
            handle: request.DeviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.PresentQueueHandle,
            handleDescription: "present-queue",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.RenderFinishedSemaphoreHandle,
            handleDescription: "render-finished semaphore",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.SwapchainHandle,
            handleDescription: "swapchain",
            paramName: nameof(request)
        );

        var queuePresent = GetPointers(deviceHandle: request.DeviceHandle).QueuePresentKhr;
        var waitSemaphorePointer = m_allocator.Alloc(size: IntPtr.Size);
        var swapchainPointer = m_allocator.Alloc(size: IntPtr.Size);
        var imageIndexPointer = m_allocator.Alloc(size: sizeof(uint));
        // Closed-loop present timing: chain a VkPresentIdKHR (the present id plus its single-element id array) ONLY when
        // a non-zero id is requested — i.e. only when VK_KHR_present_id/present_wait are enabled. A zero id leaves the
        // present byte-for-byte unchanged (PNext stays 0).
        var hasPresentId = (request.PresentId != 0UL);
        var presentIdValuePointer = (hasPresentId ? m_allocator.Alloc(size: sizeof(ulong)) : 0);
        var presentIdPointer = (hasPresentId ? m_allocator.Alloc(size: Marshal.SizeOf<VkPresentIdKhr>()) : 0);

        try {
            Marshal.WriteIntPtr(
                ptr: waitSemaphorePointer,
                val: request.RenderFinishedSemaphoreHandle
            );
            Marshal.WriteIntPtr(
                ptr: swapchainPointer,
                val: request.SwapchainHandle
            );
            Marshal.WriteInt32(
                ptr: imageIndexPointer,
                val: unchecked((int)request.ImageIndex)
            );

            var presentInfo = new VkPresentInfoKhr {
                PImageIndices = imageIndexPointer,
                PSwapchains = swapchainPointer,
                PWaitSemaphores = waitSemaphorePointer,
                SType = StructureTypePresentInfoKhr,
                SwapchainCount = 1,
                WaitSemaphoreCount = 1,
            };

            if (hasPresentId) {
                Marshal.WriteInt64(
                    ptr: presentIdValuePointer,
                    val: unchecked((long)request.PresentId)
                );
                Marshal.StructureToPtr(
                    fDeleteOld: false,
                    ptr: presentIdPointer,
                    structure: new VkPresentIdKhr {
                        PNext = 0,
                        PPresentIds = presentIdValuePointer,
                        SType = StructureTypePresentIdKhr,
                        SwapchainCount = 1,
                    }
                );

                presentInfo.PNext = presentIdPointer;
            }

            return queuePresent(
                request.PresentQueueHandle,
                in presentInfo
            );
        } finally {
            m_allocator.Free(ptr: waitSemaphorePointer);
            m_allocator.Free(ptr: swapchainPointer);
            m_allocator.Free(ptr: imageIndexPointer);

            if (hasPresentId) {
                m_allocator.Free(ptr: presentIdValuePointer);
                m_allocator.Free(ptr: presentIdPointer);
            }
        }
    }
    /// <inheritdoc/>
    public bool SupportsPresentWait(nint deviceHandle) {
        return (GetPointers(deviceHandle: deviceHandle).WaitForPresentKhr is not null);
    }
    /// <inheritdoc/>
    public void InvalidateDevice(nint deviceHandle) {
        m_pointers.TryRemove(key: deviceHandle, value: out _);
    }
    /// <inheritdoc/>
    public VkResult WaitForPresent(nint deviceHandle, nint swapchainHandle, ulong presentId, ulong timeoutNanoseconds) {
        var waitForPresent = GetPointers(deviceHandle: deviceHandle).WaitForPresentKhr;

        // Null only if the function pointer never loaded (extension absent); callers gate on SupportsPresentWait, so this
        // is purely defensive — report a benign timeout rather than dereferencing null.
        return ((waitForPresent is null)
            ? VkResult.Timeout
            : waitForPresent(deviceHandle, swapchainHandle, presentId, timeoutNanoseconds));
    }
    /// <inheritdoc/>
    public VkResult Submit(VulkanFrameSubmitRequest request) {
        VulkanArgument.RequireHandle(
            handle: request.DeviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.CommandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.FenceHandle,
            handleDescription: "fence",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.GraphicsQueueHandle,
            handleDescription: "graphics-queue",
            paramName: nameof(request)
        );

        var queueSubmit = GetPointers(deviceHandle: request.DeviceHandle).QueueSubmit;
        var waitSemaphorePointer = m_allocator.Alloc(size: IntPtr.Size);
        var waitStagePointer = m_allocator.Alloc(size: sizeof(uint));
        var commandBufferPointer = m_allocator.Alloc(size: IntPtr.Size);
        var signalSemaphorePointer = m_allocator.Alloc(size: IntPtr.Size);

        try {
            Marshal.WriteIntPtr(
                ptr: waitSemaphorePointer,
                val: request.ImageAvailableSemaphoreHandle
            );
            // This submission is the present-path graphics blit, so the acquired image is first touched at
            // COLOR_ATTACHMENT_OUTPUT — the correct wait stage here. A compute-first submission would wait at a
            // different stage, but the presenter never issues one.
            Marshal.WriteInt32(
                ptr: waitStagePointer,
                val: unchecked((int)PipelineStageColorAttachmentOutputBit)
            );
            Marshal.WriteIntPtr(
                ptr: commandBufferPointer,
                val: request.CommandBufferHandle
            );
            Marshal.WriteIntPtr(
                ptr: signalSemaphorePointer,
                val: request.RenderFinishedSemaphoreHandle
            );

            var submitInfo = new VkSubmitInfo {
                CommandBufferCount = 1,
                PCommandBuffers = commandBufferPointer,
                PSignalSemaphores = signalSemaphorePointer,
                PWaitDstStageMask = waitStagePointer,
                PWaitSemaphores = waitSemaphorePointer,
                SType = StructureTypeSubmitInfo,
                SignalSemaphoreCount = 1,
                WaitSemaphoreCount = 1,
            };

            return queueSubmit(
                request.GraphicsQueueHandle,
                1,
                in submitInfo,
                request.FenceHandle
            );
        } finally {
            m_allocator.Free(ptr: waitSemaphorePointer);
            m_allocator.Free(ptr: waitStagePointer);
            m_allocator.Free(ptr: commandBufferPointer);
            m_allocator.Free(ptr: signalSemaphorePointer);
        }
    }
    /// <inheritdoc/>
    public VkResult Submit(nint deviceHandle, nint graphicsQueueHandle, nint commandBufferHandle, nint fenceHandle) {
        VulkanArgument.RequireHandle(
            handle: deviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(deviceHandle)
        );

        VulkanArgument.RequireHandle(
            handle: graphicsQueueHandle,
            handleDescription: "graphics-queue",
            paramName: nameof(graphicsQueueHandle)
        );

        VulkanArgument.RequireHandle(
            handle: commandBufferHandle,
            handleDescription: "command-buffer",
            paramName: nameof(commandBufferHandle)
        );

        VulkanArgument.RequireHandle(
            handle: fenceHandle,
            handleDescription: "fence",
            paramName: nameof(fenceHandle)
        );

        var queueSubmit = GetPointers(deviceHandle: deviceHandle).QueueSubmit;
        var commandBufferPointer = m_allocator.Alloc(size: IntPtr.Size);

        try {
            Marshal.WriteIntPtr(
                ptr: commandBufferPointer,
                val: commandBufferHandle
            );

            var submitInfo = new VkSubmitInfo {
                CommandBufferCount = 1,
                PCommandBuffers = commandBufferPointer,
                PSignalSemaphores = 0,
                PWaitDstStageMask = 0,
                PWaitSemaphores = 0,
                SType = StructureTypeSubmitInfo,
                SignalSemaphoreCount = 0,
                WaitSemaphoreCount = 0,
            };

            return queueSubmit(
                graphicsQueueHandle,
                1,
                in submitInfo,
                fenceHandle
            );
        } finally {
            m_allocator.Free(ptr: commandBufferPointer);
        }
    }

    private unsafe struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, nint, ulong, nint, nint, out uint, VkResult> AcquireNextImageKhr;
        public delegate* unmanaged[Cdecl]<nint, in VkPresentInfoKhr, VkResult> QueuePresentKhr;
        public delegate* unmanaged[Cdecl]<nint, uint, in VkSubmitInfo, nint, VkResult> QueueSubmit;
        // Null when VK_KHR_present_wait was not enabled — the closed-loop present-timing path stays off in that case.
        public delegate* unmanaged[Cdecl]<nint, nint, ulong, ulong, VkResult> WaitForPresentKhr;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, DevicePointers> m_pointers = new();

    private DevicePointers GetPointers(nint deviceHandle) {
        return m_pointers.GetOrAdd(
            key: deviceHandle,
            valueFactory: static handle => new DevicePointers {
                AcquireNextImageKhr = (delegate* unmanaged[Cdecl]<nint, nint, ulong, nint, nint, out uint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkAcquireNextImageKHR"u8),
                QueuePresentKhr = (delegate* unmanaged[Cdecl]<nint, in VkPresentInfoKhr, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkQueuePresentKHR"u8),
                QueueSubmit = (delegate* unmanaged[Cdecl]<nint, uint, in VkSubmitInfo, nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkQueueSubmit"u8),
                // Optional: present (VK_KHR_present_wait). Resolves to null when the extension was not enabled, which the
                // present-timing path treats as "unsupported" and falls back to open-loop pacing.
                WaitForPresentKhr = (delegate* unmanaged[Cdecl]<nint, nint, ulong, ulong, VkResult>)VulkanProcResolver.ResolveOptionalDeviceProc(deviceHandle: handle, functionName: "vkWaitForPresentKHR"u8),
            }
        );
    }
    private static unsafe void ValidateAcquireRequest(VulkanFrameAcquireRequest request) {
        VulkanArgument.RequireHandle(
            handle: request.DeviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.ImageAvailableSemaphoreHandle,
            handleDescription: "image-available semaphore",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.SwapchainHandle,
            handleDescription: "swapchain",
            paramName: nameof(request)
        );
    }
}
