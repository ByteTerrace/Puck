using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanRenderPassApi"/>, marshaling to the
/// <c>vkCreateRenderPass</c> and <c>vkDestroyRenderPass</c> entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeRenderPassApi : IVulkanRenderPassApi {
    private readonly IAllocator m_allocator;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativeRenderPassApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public VulkanNativeRenderPassApi(IAllocator allocator) {
        ArgumentNullException.ThrowIfNull(argument: allocator);

        m_allocator = allocator;
    }

    // Color attachments are referenced in COLOR_ATTACHMENT_OPTIMAL during the subpass; this
    // is structural to "these are color attachments", not policy — the per-attachment
    // initial/final layouts come from the caller's VkAttachmentDescription.
    private const uint ColorAttachmentOptimalLayout = 2;
    private const uint GraphicsPipelineBindPoint = 0;
    private const uint StructureTypeRenderPassCreateInfo = 38;

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;

    /// <inheritdoc/>
    public VkResult CreateRenderPass(VulkanRenderPassCreateRequest request, out nint renderPassHandle) {
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        var colorAttachments = request.ColorAttachments;

        if (
            (colorAttachments is null) ||
            (0 == colorAttachments.Count)
        ) {
            throw new ArgumentException(
                message: "A render pass requires at least one color attachment.",
                paramName: nameof(request)
            );
        }

        var dependencies = (request.Dependencies ?? []);
        var createRenderPass = GetPointers(deviceHandle: request.DeviceHandle).CreateRenderPass;

        var attachmentCount = colorAttachments.Count;
        var attachmentStride = Marshal.SizeOf<VkAttachmentDescription>();
        var referenceStride = Marshal.SizeOf<VkAttachmentReference>();
        var dependencyCount = dependencies.Count;
        var dependencyStride = Marshal.SizeOf<VkSubpassDependency>();

        var attachmentsPointer = m_allocator.Alloc(size: (attachmentStride * attachmentCount));
        var referencesPointer = m_allocator.Alloc(size: (referenceStride * attachmentCount));
        var subpassPointer = m_allocator.Alloc(size: Marshal.SizeOf<VkSubpassDescription>());
        var dependencyPointer = ((dependencyCount > 0)
            ? m_allocator.Alloc(size: (dependencyStride * dependencyCount))
            : nint.Zero);

        try {
            for (var index = 0; (index < attachmentCount); index++) {
                Marshal.StructureToPtr(
                    fDeleteOld: false,
                    ptr: (attachmentsPointer + (index * attachmentStride)),
                    structure: colorAttachments[index]
                );
                Marshal.StructureToPtr(
                    fDeleteOld: false,
                    ptr: (referencesPointer + (index * referenceStride)),
                    structure: new VkAttachmentReference {
                        Attachment = (uint)index,
                        Layout = ColorAttachmentOptimalLayout,
                    }
                );
            }

            Marshal.StructureToPtr(
                fDeleteOld: false,
                ptr: subpassPointer,
                structure: new VkSubpassDescription {
                    ColorAttachmentCount = (uint)attachmentCount,
                    PColorAttachments = referencesPointer,
                    PipelineBindPoint = GraphicsPipelineBindPoint,
                }
            );

            for (var index = 0; (index < dependencyCount); index++) {
                Marshal.StructureToPtr(
                    fDeleteOld: false,
                    ptr: (dependencyPointer + (index * dependencyStride)),
                    structure: dependencies[index]
                );
            }

            var createInfo = new VkRenderPassCreateInfo {
                AttachmentCount = (uint)attachmentCount,
                DependencyCount = (uint)dependencyCount,
                PAttachments = attachmentsPointer,
                PDependencies = dependencyPointer,
                PSubpasses = subpassPointer,
                SType = StructureTypeRenderPassCreateInfo,
                SubpassCount = 1,
            };

            return createRenderPass(
                request.DeviceHandle,
                in createInfo,
                0,
                out renderPassHandle
            );
        } finally {
            m_allocator.Free(ptr: attachmentsPointer);
            m_allocator.Free(ptr: referencesPointer);
            m_allocator.Free(ptr: subpassPointer);
            if (0 != dependencyPointer) {
                m_allocator.Free(ptr: dependencyPointer);
            }
        }
    }
    /// <inheritdoc/>
    public void DestroyRenderPass(nint deviceHandle, nint renderPassHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == renderPassHandle)
        ) {
            return;
        }

        var destroyRenderPass = GetPointers(deviceHandle: deviceHandle).DestroyRenderPass;

        destroyRenderPass(
            deviceHandle,
            renderPassHandle,
            0
        );
    }

    private unsafe struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkRenderPassCreateInfo, nint, out nint, VkResult> CreateRenderPass;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyRenderPass;
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

        fixed (byte* pName = "vkCreateRenderPass"u8) {
            pNew.CreateRenderPass = (delegate* unmanaged[Cdecl]<nint, in VkRenderPassCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroyRenderPass"u8) {
            pNew.DestroyRenderPass = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
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
}
