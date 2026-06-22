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
    private const uint StructureTypePresentIdKhr = 1000294001;
    private const uint StructureTypeSubmitInfo = 4;

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;

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
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.PresentQueueHandle) {
            throw new ArgumentException(
                message: "Vulkan present-queue handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.RenderFinishedSemaphoreHandle) {
            throw new ArgumentException(
                message: "Vulkan render-finished semaphore handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.SwapchainHandle) {
            throw new ArgumentException(
                message: "Vulkan swapchain handle must be non-zero.",
                paramName: nameof(request)
            );
        }

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
        return (GetPointers(deviceHandle: deviceHandle).WaitForPresentKhr != null);
    }
    /// <inheritdoc/>
    public VkResult WaitForPresent(nint deviceHandle, nint swapchainHandle, ulong presentId, ulong timeoutNanoseconds) {
        var waitForPresent = GetPointers(deviceHandle: deviceHandle).WaitForPresentKhr;

        // Null only if the function pointer never loaded (extension absent); callers gate on SupportsPresentWait, so this
        // is purely defensive — report a benign timeout rather than dereferencing null.
        return ((waitForPresent == null)
            ? VkResult.Timeout
            : waitForPresent(deviceHandle, swapchainHandle, presentId, timeoutNanoseconds));
    }
    /// <inheritdoc/>
    public VkResult Submit(VulkanFrameSubmitRequest request) {
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.CommandBufferHandle) {
            throw new ArgumentException(
                message: "Vulkan command-buffer handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.FenceHandle) {
            throw new ArgumentException(
                message: "Vulkan fence handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.GraphicsQueueHandle) {
            throw new ArgumentException(
                message: "Vulkan graphics-queue handle must be non-zero.",
                paramName: nameof(request)
            );
        }

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
            // This submission IS the present-path graphics blit, so the acquired image is first touched at
            // COLOR_ATTACHMENT_OUTPUT — the correct wait stage for this path. (A compute-first submission would wait at
            // a different stage; the presenter never issues one, so this stage is fixed by the path's purpose, not an
            // assumption that could silently break.)
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
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == graphicsQueueHandle) {
            throw new ArgumentException(
                message: "Vulkan graphics-queue handle must be non-zero.",
                paramName: nameof(graphicsQueueHandle)
            );
        }

        if (0 == commandBufferHandle) {
            throw new ArgumentException(
                message: "Vulkan command-buffer handle must be non-zero.",
                paramName: nameof(commandBufferHandle)
            );
        }

        if (0 == fenceHandle) {
            throw new ArgumentException(
                message: "Vulkan fence handle must be non-zero.",
                paramName: nameof(fenceHandle)
            );
        }

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

    private unsafe DevicePointers GetPointers(nint deviceHandle) {
        if (m_pointers.TryGetValue(
            key: deviceHandle,
            value: out var pointers
        )) {
            return pointers;
        }
        var getAddr = GetDeviceProcAddr();
        DevicePointers pNew = default;

        fixed (byte* pName = "vkAcquireNextImageKHR"u8) {
            pNew.AcquireNextImageKhr = (delegate* unmanaged[Cdecl]<nint, nint, ulong, nint, nint, out uint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkQueuePresentKHR"u8) {
            pNew.QueuePresentKhr = (delegate* unmanaged[Cdecl]<nint, in VkPresentInfoKhr, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkQueueSubmit"u8) {
            pNew.QueueSubmit = (delegate* unmanaged[Cdecl]<nint, uint, in VkSubmitInfo, nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        // Optional: present (VK_KHR_present_wait). vkGetDeviceProcAddr returns null when the extension was not enabled,
        // which the present-timing path treats as "unsupported" and falls back to open-loop pacing.
        fixed (byte* pName = "vkWaitForPresentKHR"u8) {
            pNew.WaitForPresentKhr = (delegate* unmanaged[Cdecl]<nint, nint, ulong, ulong, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        m_pointers[deviceHandle] = pNew;
        return pNew;
    }
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
        lock (m_syncRoot) {
            if (m_getDeviceProcAddr is not null) {
                return m_getDeviceProcAddr;
            }
            var export = VulkanNativeLibrary.GetExport(functionName: "vkGetDeviceProcAddr");

            m_getDeviceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)export;
            return m_getDeviceProcAddr;
        }
    }
    private static unsafe void ValidateAcquireRequest(VulkanFrameAcquireRequest request) {
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.ImageAvailableSemaphoreHandle) {
            throw new ArgumentException(
                message: "Vulkan image-available semaphore handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.SwapchainHandle) {
            throw new ArgumentException(
                message: "Vulkan swapchain handle must be non-zero.",
                paramName: nameof(request)
            );
        }
    }
}
