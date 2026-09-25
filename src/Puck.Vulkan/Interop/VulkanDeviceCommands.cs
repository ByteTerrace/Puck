using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Interop;

/// <summary>
/// The command table of one Vulkan logical device: every device-level entry point Puck calls, resolved once through
/// <c>vkGetDeviceProcAddr</c> when the device is created. Device-level resolution returns the driver's own entry points,
/// so a call through this table skips the loader's dispatch trampoline and costs one field load plus one indirect call.
/// Core entry points are required and resolved eagerly; an extension entry point is <see langword="null"/> when the
/// device was created without its extension, and every caller that can reach it without the extension checks it first.
/// The table lives exactly as long as its device: <see cref="VulkanLogicalDevice"/> owns it, and it is never resolved
/// again or reused for another device.
/// </summary>
public sealed unsafe class VulkanDeviceCommands {
    /// <summary>The <c>vkAcquireNextImageKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_swapchain</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, ulong, nint, nint, out uint, VkResult> AcquireNextImageKhr;
    /// <summary>The <c>vkAllocateCommandBuffers</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkCommandBufferAllocateInfo, nint, VkResult> AllocateCommandBuffers;
    /// <summary>The <c>vkAllocateDescriptorSets</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkDescriptorSetAllocateInfo, nint, VkResult> AllocateDescriptorSets;
    /// <summary>The <c>vkAllocateMemory</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult> AllocateMemory;
    /// <summary>The <c>vkBeginCommandBuffer</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkCommandBufferBeginInfo, VkResult> BeginCommandBuffer;
    /// <summary>The <c>vkBindBufferMemory</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult> BindBufferMemory;
    /// <summary>The <c>vkBindImageMemory</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult> BindImageMemory;
    /// <summary>The <c>vkCmdBeginDebugUtilsLabelEXT</c> entry point; <see langword="null"/> unless <c>VK_EXT_debug_utils</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkDebugUtilsLabelExt, void> CmdBeginDebugUtilsLabelExt;
    /// <summary>The <c>vkCmdBeginRenderPass</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkRenderPassBeginInfo, uint, void> CmdBeginRenderPass;
    /// <summary>The <c>vkCmdBindDescriptorSets</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, nint, uint, uint, nint, uint, nint, void> CmdBindDescriptorSets;
    /// <summary>The <c>vkCmdBindIndexBuffer</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, ulong, uint, void> CmdBindIndexBuffer;
    /// <summary>The <c>vkCmdBindPipeline</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, nint, void> CmdBindPipeline;
    /// <summary>The <c>vkCmdBindVertexBuffers</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, uint, nint, nint, void> CmdBindVertexBuffers;
    /// <summary>The <c>vkCmdBlitImage</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, uint, nint, uint, void> CmdBlitImage;
    /// <summary>The <c>vkCmdClearColorImage</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, nint, void> CmdClearColorImage;
    /// <summary>The <c>vkCmdCopyBufferToImage</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, uint, uint, nint, void> CmdCopyBufferToImage;
    /// <summary>The <c>vkCmdCopyImage</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, uint, nint, void> CmdCopyImage;
    /// <summary>The <c>vkCmdCopyImageToBuffer</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, nint, void> CmdCopyImageToBuffer;
    /// <summary>The <c>vkCmdDispatch</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, uint, uint, void> CmdDispatch;
    /// <summary>The <c>vkCmdDispatchIndirect</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, ulong, void> CmdDispatchIndirect;
    /// <summary>The <c>vkCmdDraw</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, void> CmdDraw;
    /// <summary>The <c>vkCmdDrawIndexed</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, uint, uint, int, uint, void> CmdDrawIndexed;
    /// <summary>The <c>vkCmdEndDebugUtilsLabelEXT</c> entry point; <see langword="null"/> unless <c>VK_EXT_debug_utils</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, void> CmdEndDebugUtilsLabelExt;
    /// <summary>The <c>vkCmdEndRenderPass</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, void> CmdEndRenderPass;
    /// <summary>The <c>vkCmdFillBuffer</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, ulong, ulong, uint, void> CmdFillBuffer;
    /// <summary>The <c>vkCmdPipelineBarrier</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, nint, uint, nint, uint, nint, void> CmdPipelineBarrier;
    /// <summary>The <c>vkCmdPushConstants</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, uint, uint, uint, nint, void> CmdPushConstants;
    /// <summary>The <c>vkCmdSetScissor</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, uint, nint, void> CmdSetScissor;
    /// <summary>The <c>vkCmdSetViewport</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, uint, nint, void> CmdSetViewport;
    /// <summary>The <c>vkCreateBuffer</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkBufferCreateInfo, nint, out nint, VkResult> CreateBuffer;
    /// <summary>The <c>vkCreateCommandPool</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkCommandPoolCreateInfo, nint, out nint, VkResult> CreateCommandPool;
    /// <summary>The <c>vkCreateComputePipelines</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, uint, nint, nint, out nint, VkResult> CreateComputePipelines;
    /// <summary>The <c>vkCreateDescriptorPool</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkDescriptorPoolCreateInfo, nint, out nint, VkResult> CreateDescriptorPool;
    /// <summary>The <c>vkCreateDescriptorSetLayout</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkDescriptorSetLayoutCreateInfo, nint, out nint, VkResult> CreateDescriptorSetLayout;
    /// <summary>The <c>vkCreateFence</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkFenceCreateInfo, nint, out nint, VkResult> CreateFence;
    /// <summary>The <c>vkCreateFramebuffer</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkFramebufferCreateInfo, nint, out nint, VkResult> CreateFramebuffer;
    /// <summary>The <c>vkCreateGraphicsPipelines</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, uint, nint, nint, out nint, VkResult> CreateGraphicsPipelines;
    /// <summary>The <c>vkCreateImage</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkImageCreateInfo, nint, out nint, VkResult> CreateImage;
    /// <summary>The <c>vkCreateImageView</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkImageViewCreateInfo, nint, out nint, VkResult> CreateImageView;
    /// <summary>The <c>vkCreatePipelineCache</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkPipelineCacheCreateInfo, nint, out nint, VkResult> CreatePipelineCache;
    /// <summary>The <c>vkCreatePipelineLayout</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkPipelineLayoutCreateInfo, nint, out nint, VkResult> CreatePipelineLayout;
    /// <summary>The <c>vkCreateRenderPass</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkRenderPassCreateInfo, nint, out nint, VkResult> CreateRenderPass;
    /// <summary>The <c>vkCreateSampler</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkSamplerCreateInfo, nint, out nint, VkResult> CreateSampler;
    /// <summary>The <c>vkCreateSemaphore</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkSemaphoreCreateInfo, nint, out nint, VkResult> CreateSemaphore;
    /// <summary>The <c>vkCreateShaderModule</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkShaderModuleCreateInfo, nint, out nint, VkResult> CreateShaderModule;
    /// <summary>The <c>vkCreateSwapchainKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_swapchain</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkSwapchainCreateInfoKhr, nint, out nint, VkResult> CreateSwapchainKhr;
    /// <summary>The <c>vkDestroyBuffer</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyBuffer;
    /// <summary>The <c>vkDestroyCommandPool</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyCommandPool;
    /// <summary>The <c>vkDestroyDescriptorPool</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyDescriptorPool;
    /// <summary>The <c>vkDestroyDescriptorSetLayout</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyDescriptorSetLayout;
    /// <summary>The <c>vkDestroyDevice</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, void> DestroyDevice;
    /// <summary>The <c>vkDestroyFence</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyFence;
    /// <summary>The <c>vkDestroyFramebuffer</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyFramebuffer;
    /// <summary>The <c>vkDestroyImage</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyImage;
    /// <summary>The <c>vkDestroyImageView</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyImageView;
    /// <summary>The <c>vkDestroyPipeline</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyPipeline;
    /// <summary>The <c>vkDestroyPipelineCache</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyPipelineCache;
    /// <summary>The <c>vkDestroyPipelineLayout</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyPipelineLayout;
    /// <summary>The <c>vkDestroyRenderPass</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyRenderPass;
    /// <summary>The <c>vkDestroySampler</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroySampler;
    /// <summary>The <c>vkDestroySemaphore</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroySemaphore;
    /// <summary>The <c>vkDestroyShaderModule</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyShaderModule;
    /// <summary>The <c>vkDestroySwapchainKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_swapchain</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroySwapchainKhr;
    /// <summary>The <c>vkDeviceWaitIdle</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, VkResult> DeviceWaitIdle;
    /// <summary>The <c>vkEndCommandBuffer</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, VkResult> EndCommandBuffer;
    /// <summary>The <c>vkFreeMemory</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> FreeMemory;
    /// <summary>The <c>vkGetBufferMemoryRequirements</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void> GetBufferMemoryRequirements;
    /// <summary>The <c>vkGetDeviceQueue</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, uint, out nint, void> GetDeviceQueue;
    /// <summary>The <c>vkGetFenceStatus</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, VkResult> GetFenceStatus;
    /// <summary>The <c>vkGetImageMemoryRequirements</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void> GetImageMemoryRequirements;
    /// <summary>The <c>vkGetMemoryWin32HandleKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_external_memory_win32</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkMemoryGetWin32HandleInfoKHR, out nint, VkResult> GetMemoryWin32HandleKhr;
    /// <summary>The <c>vkGetMemoryWin32HandlePropertiesKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_external_memory_win32</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, nint, out VkMemoryWin32HandlePropertiesKHR, VkResult> GetMemoryWin32HandlePropertiesKhr;
    /// <summary>The <c>vkGetPipelineCacheData</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nuint*, void*, VkResult> GetPipelineCacheData;
    /// <summary>The <c>vkGetPipelineExecutablePropertiesKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_pipeline_executable_properties</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, VkPipelineInfoKhr*, uint*, VkPipelineExecutablePropertiesKhr*, VkResult> GetPipelineExecutablePropertiesKhr;
    /// <summary>The <c>vkGetPipelineExecutableStatisticsKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_pipeline_executable_properties</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, VkPipelineExecutableInfoKhr*, uint*, VkPipelineExecutableStatisticKhr*, VkResult> GetPipelineExecutableStatisticsKhr;
    /// <summary>The <c>vkGetSwapchainImagesKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_swapchain</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, ref uint, nint, VkResult> GetSwapchainImagesKhr;
    /// <summary>The <c>vkMapMemory</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, ulong, nuint, uint, out nint, VkResult> MapMemory;
    /// <summary>The <c>vkQueuePresentKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_swapchain</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkPresentInfoKhr, VkResult> QueuePresentKhr;
    /// <summary>The <c>vkQueueSubmit</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, in VkSubmitInfo, nint, VkResult> QueueSubmit;
    /// <summary>The <c>vkQueueWaitIdle</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, VkResult> QueueWaitIdle;
    /// <summary>The <c>vkResetFences</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, in nint, VkResult> ResetFences;
    /// <summary>The <c>vkUnmapMemory</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, void> UnmapMemory;
    /// <summary>The <c>vkUpdateDescriptorSets</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, nint, uint, nint, void> UpdateDescriptorSets;
    /// <summary>The <c>vkWaitForFences</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, in nint, uint, ulong, VkResult> WaitForFences;
    /// <summary>The <c>vkWaitForPresentKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_present_wait</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, ulong, ulong, VkResult> WaitForPresentKhr;

    /// <summary>Resolves every entry point of the device identified by <paramref name="deviceHandle"/> through
    /// <paramref name="procedures"/>.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle; must be non-zero and must outlive the table.</param>
    /// <param name="procedures">The resolver the entry points are resolved and counted through: the host's, or one
    /// standing in for the driver.</param>
    /// <exception cref="ArgumentException"><paramref name="deviceHandle"/> is zero.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="procedures"/> is <see langword="null"/>.</exception>
    /// <param name="memory">The device-local memory counts every allocation made through this table joins, or
    /// <see langword="null"/> to count none.</param>
    /// <exception cref="InvalidOperationException">The device does not expose a required core entry point.</exception>
    public VulkanDeviceCommands(nint deviceHandle, VulkanProcResolver procedures, GpuDeviceMemoryWork? memory = null) {
        ArgumentNullException.ThrowIfNull(argument: procedures);

        VulkanArgument.RequireHandle(
            handle: deviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(deviceHandle)
        );

        Handle = deviceHandle;
        Memory = memory;
        AcquireNextImageKhr = ((delegate* unmanaged[Cdecl]<nint, nint, ulong, nint, nint, out uint, VkResult>)procedures.ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkAcquireNextImageKHR"u8
        ));
        AllocateCommandBuffers = ((delegate* unmanaged[Cdecl]<nint, in VkCommandBufferAllocateInfo, nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkAllocateCommandBuffers"u8
        ));
        AllocateDescriptorSets = ((delegate* unmanaged[Cdecl]<nint, in VkDescriptorSetAllocateInfo, nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkAllocateDescriptorSets"u8
        ));
        AllocateMemory = ((delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkAllocateMemory"u8
        ));
        BeginCommandBuffer = ((delegate* unmanaged[Cdecl]<nint, in VkCommandBufferBeginInfo, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkBeginCommandBuffer"u8
        ));
        BindBufferMemory = ((delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkBindBufferMemory"u8
        ));
        BindImageMemory = ((delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkBindImageMemory"u8
        ));
        CmdBeginDebugUtilsLabelExt = ((delegate* unmanaged[Cdecl]<nint, in VkDebugUtilsLabelExt, void>)procedures.ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdBeginDebugUtilsLabelEXT"u8
        ));
        CmdBeginRenderPass = ((delegate* unmanaged[Cdecl]<nint, in VkRenderPassBeginInfo, uint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdBeginRenderPass"u8
        ));
        CmdBindDescriptorSets = ((delegate* unmanaged[Cdecl]<nint, uint, nint, uint, uint, nint, uint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdBindDescriptorSets"u8
        ));
        CmdBindIndexBuffer = ((delegate* unmanaged[Cdecl]<nint, nint, ulong, uint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdBindIndexBuffer"u8
        ));
        CmdBindPipeline = ((delegate* unmanaged[Cdecl]<nint, uint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdBindPipeline"u8
        ));
        CmdBindVertexBuffers = ((delegate* unmanaged[Cdecl]<nint, uint, uint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdBindVertexBuffers"u8
        ));
        CmdBlitImage = ((delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, uint, nint, uint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdBlitImage"u8
        ));
        CmdClearColorImage = ((delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdClearColorImage"u8
        ));
        CmdCopyBufferToImage = ((delegate* unmanaged[Cdecl]<nint, nint, nint, uint, uint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdCopyBufferToImage"u8
        ));
        CmdCopyImage = ((delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, uint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdCopyImage"u8
        ));
        CmdCopyImageToBuffer = ((delegate* unmanaged[Cdecl]<nint, nint, uint, nint, uint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdCopyImageToBuffer"u8
        ));
        CmdDispatch = ((delegate* unmanaged[Cdecl]<nint, uint, uint, uint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdDispatch"u8
        ));
        CmdDispatchIndirect = ((delegate* unmanaged[Cdecl]<nint, nint, ulong, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdDispatchIndirect"u8
        ));
        CmdDraw = ((delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdDraw"u8
        ));
        CmdDrawIndexed = ((delegate* unmanaged[Cdecl]<nint, uint, uint, uint, int, uint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdDrawIndexed"u8
        ));
        CmdEndDebugUtilsLabelExt = ((delegate* unmanaged[Cdecl]<nint, void>)procedures.ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdEndDebugUtilsLabelEXT"u8
        ));
        CmdEndRenderPass = ((delegate* unmanaged[Cdecl]<nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdEndRenderPass"u8
        ));
        CmdFillBuffer = ((delegate* unmanaged[Cdecl]<nint, nint, ulong, ulong, uint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdFillBuffer"u8
        ));
        CmdPipelineBarrier = ((delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, nint, uint, nint, uint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdPipelineBarrier"u8
        ));
        CmdPushConstants = ((delegate* unmanaged[Cdecl]<nint, nint, uint, uint, uint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdPushConstants"u8
        ));
        CmdSetScissor = ((delegate* unmanaged[Cdecl]<nint, uint, uint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdSetScissor"u8
        ));
        CmdSetViewport = ((delegate* unmanaged[Cdecl]<nint, uint, uint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCmdSetViewport"u8
        ));
        CreateBuffer = ((delegate* unmanaged[Cdecl]<nint, in VkBufferCreateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreateBuffer"u8
        ));
        CreateCommandPool = ((delegate* unmanaged[Cdecl]<nint, in VkCommandPoolCreateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreateCommandPool"u8
        ));
        CreateComputePipelines = ((delegate* unmanaged[Cdecl]<nint, nint, uint, nint, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreateComputePipelines"u8
        ));
        CreateDescriptorPool = ((delegate* unmanaged[Cdecl]<nint, in VkDescriptorPoolCreateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreateDescriptorPool"u8
        ));
        CreateDescriptorSetLayout = ((delegate* unmanaged[Cdecl]<nint, in VkDescriptorSetLayoutCreateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreateDescriptorSetLayout"u8
        ));
        CreateFence = ((delegate* unmanaged[Cdecl]<nint, in VkFenceCreateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreateFence"u8
        ));
        CreateFramebuffer = ((delegate* unmanaged[Cdecl]<nint, in VkFramebufferCreateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreateFramebuffer"u8
        ));
        CreateGraphicsPipelines = ((delegate* unmanaged[Cdecl]<nint, nint, uint, nint, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreateGraphicsPipelines"u8
        ));
        CreateImage = ((delegate* unmanaged[Cdecl]<nint, in VkImageCreateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreateImage"u8
        ));
        CreateImageView = ((delegate* unmanaged[Cdecl]<nint, in VkImageViewCreateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreateImageView"u8
        ));
        CreatePipelineCache = ((delegate* unmanaged[Cdecl]<nint, in VkPipelineCacheCreateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreatePipelineCache"u8
        ));
        CreatePipelineLayout = ((delegate* unmanaged[Cdecl]<nint, in VkPipelineLayoutCreateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreatePipelineLayout"u8
        ));
        CreateRenderPass = ((delegate* unmanaged[Cdecl]<nint, in VkRenderPassCreateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreateRenderPass"u8
        ));
        CreateSampler = ((delegate* unmanaged[Cdecl]<nint, in VkSamplerCreateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreateSampler"u8
        ));
        CreateSemaphore = ((delegate* unmanaged[Cdecl]<nint, in VkSemaphoreCreateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreateSemaphore"u8
        ));
        CreateShaderModule = ((delegate* unmanaged[Cdecl]<nint, in VkShaderModuleCreateInfo, nint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreateShaderModule"u8
        ));
        CreateSwapchainKhr = ((delegate* unmanaged[Cdecl]<nint, in VkSwapchainCreateInfoKhr, nint, out nint, VkResult>)procedures.ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkCreateSwapchainKHR"u8
        ));
        DestroyBuffer = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroyBuffer"u8
        ));
        DestroyCommandPool = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroyCommandPool"u8
        ));
        DestroyDescriptorPool = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroyDescriptorPool"u8
        ));
        DestroyDescriptorSetLayout = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroyDescriptorSetLayout"u8
        ));
        DestroyDevice = ((delegate* unmanaged[Cdecl]<nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroyDevice"u8
        ));
        DestroyFence = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroyFence"u8
        ));
        DestroyFramebuffer = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroyFramebuffer"u8
        ));
        DestroyImage = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroyImage"u8
        ));
        DestroyImageView = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroyImageView"u8
        ));
        DestroyPipeline = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroyPipeline"u8
        ));
        DestroyPipelineCache = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroyPipelineCache"u8
        ));
        DestroyPipelineLayout = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroyPipelineLayout"u8
        ));
        DestroyRenderPass = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroyRenderPass"u8
        ));
        DestroySampler = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroySampler"u8
        ));
        DestroySemaphore = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroySemaphore"u8
        ));
        DestroyShaderModule = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroyShaderModule"u8
        ));
        DestroySwapchainKhr = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroySwapchainKHR"u8
        ));
        DeviceWaitIdle = ((delegate* unmanaged[Cdecl]<nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDeviceWaitIdle"u8
        ));
        EndCommandBuffer = ((delegate* unmanaged[Cdecl]<nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkEndCommandBuffer"u8
        ));
        FreeMemory = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkFreeMemory"u8
        ));
        GetBufferMemoryRequirements = ((delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkGetBufferMemoryRequirements"u8
        ));
        GetDeviceQueue = ((delegate* unmanaged[Cdecl]<nint, uint, uint, out nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkGetDeviceQueue"u8
        ));
        GetFenceStatus = ((delegate* unmanaged[Cdecl]<nint, nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkGetFenceStatus"u8
        ));
        GetImageMemoryRequirements = ((delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkGetImageMemoryRequirements"u8
        ));
        GetMemoryWin32HandleKhr = ((delegate* unmanaged[Cdecl]<nint, in VkMemoryGetWin32HandleInfoKHR, out nint, VkResult>)procedures.ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkGetMemoryWin32HandleKHR"u8
        ));
        GetMemoryWin32HandlePropertiesKhr = ((delegate* unmanaged[Cdecl]<nint, uint, nint, out VkMemoryWin32HandlePropertiesKHR, VkResult>)procedures.ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkGetMemoryWin32HandlePropertiesKHR"u8
        ));
        GetPipelineCacheData = ((delegate* unmanaged[Cdecl]<nint, nint, nuint*, void*, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkGetPipelineCacheData"u8
        ));
        GetPipelineExecutablePropertiesKhr = ((delegate* unmanaged[Cdecl]<nint, VkPipelineInfoKhr*, uint*, VkPipelineExecutablePropertiesKhr*, VkResult>)procedures.ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkGetPipelineExecutablePropertiesKHR"u8
        ));
        GetPipelineExecutableStatisticsKhr = ((delegate* unmanaged[Cdecl]<nint, VkPipelineExecutableInfoKhr*, uint*, VkPipelineExecutableStatisticKhr*, VkResult>)procedures.ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkGetPipelineExecutableStatisticsKHR"u8
        ));
        GetSwapchainImagesKhr = ((delegate* unmanaged[Cdecl]<nint, nint, ref uint, nint, VkResult>)procedures.ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkGetSwapchainImagesKHR"u8
        ));
        MapMemory = ((delegate* unmanaged[Cdecl]<nint, nint, ulong, nuint, uint, out nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkMapMemory"u8
        ));
        QueuePresentKhr = ((delegate* unmanaged[Cdecl]<nint, in VkPresentInfoKhr, VkResult>)procedures.ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkQueuePresentKHR"u8
        ));
        QueueSubmit = ((delegate* unmanaged[Cdecl]<nint, uint, in VkSubmitInfo, nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkQueueSubmit"u8
        ));
        QueueWaitIdle = ((delegate* unmanaged[Cdecl]<nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkQueueWaitIdle"u8
        ));
        ResetFences = ((delegate* unmanaged[Cdecl]<nint, uint, in nint, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkResetFences"u8
        ));
        UnmapMemory = ((delegate* unmanaged[Cdecl]<nint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkUnmapMemory"u8
        ));
        UpdateDescriptorSets = ((delegate* unmanaged[Cdecl]<nint, uint, nint, uint, nint, void>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkUpdateDescriptorSets"u8
        ));
        WaitForFences = ((delegate* unmanaged[Cdecl]<nint, uint, in nint, uint, ulong, VkResult>)procedures.ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkWaitForFences"u8
        ));
        WaitForPresentKhr = ((delegate* unmanaged[Cdecl]<nint, nint, ulong, ulong, VkResult>)procedures.ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkWaitForPresentKHR"u8
        ));
    }

    /// <summary>Gets the native <c>VkDevice</c> handle whose entry points the table holds.</summary>
    public nint Handle { get; }
    /// <summary>Gets the device-local memory counts this table's allocations and frees join, or <see langword="null"/>
    /// when it counts none.</summary>
    public GpuDeviceMemoryWork? Memory { get; }

    /// <summary>Destroys or frees one object this device owns through the object type's entry point, skipping a zero
    /// handle. Every <c>vkDestroy*</c> child-object entry point and <c>vkFreeMemory</c> share this shape, and this is
    /// the only zero-handle guard on them: every device-level destroy path calls it without checking first.</summary>
    /// <param name="destroy">The entry point from this table: <see cref="DestroyPipeline"/>, <see cref="FreeMemory"/>, and so on.</param>
    /// <param name="handle">The native handle to release, or zero.</param>
    public void Destroy(delegate* unmanaged[Cdecl]<nint, nint, nint, void> destroy, nint handle) {
        if (0 != handle) {
            // The same field value this table resolved, so an address comparison identifies vkFreeMemory.
            if (((nint)destroy) == ((nint)FreeMemory)) {
                _ = Memory?.CountReleased(
                    allocation: handle,
                    device: Handle
                );
            }

            destroy(
                Handle,
                handle,
                0
            );
        }
    }
    /// <summary>Counts one successful <c>vkAllocateMemory</c> into <see cref="Memory"/> at its allocation size, keyed by
    /// this device, when its role counts (<see cref="GpuDeviceMemoryWork.IsCounted"/>); the memory type it chose never
    /// decides. Swapchain images are never allocated through this table, so they are never counted.</summary>
    /// <param name="memoryHandle">The <c>VkDeviceMemory</c> the allocation returned; freeing it through
    /// <see cref="FreeMemory"/> through <c>Destroy</c> counts its release.</param>
    /// <param name="allocationSize">The allocation's size, in bytes, as <c>VkMemoryAllocateInfo.allocationSize</c>.</param>
    /// <param name="role">What the allocation is for.</param>
    public void CountAllocated(nint memoryHandle, ulong allocationSize, GpuMemoryRole role) =>
        _ = Memory?.CountAllocated(
            allocation: memoryHandle,
            bytes: checked((long)allocationSize),
            device: Handle,
            role: role
        );
    /// <summary>Destroys one buffer or image this device owns and then frees the memory bound to it, skipping either
    /// handle when it is zero.</summary>
    /// <param name="destroy">The object type's entry point from this table: <see cref="DestroyBuffer"/> or <see cref="DestroyImage"/>.</param>
    /// <param name="handle">The native handle to destroy, or zero.</param>
    /// <param name="memoryHandle">The native <c>VkDeviceMemory</c> handle bound to it, or zero.</param>
    public void Destroy(delegate* unmanaged[Cdecl]<nint, nint, nint, void> destroy, nint handle, nint memoryHandle) {
        Destroy(
            destroy: destroy,
            handle: handle
        );
        Destroy(
            destroy: FreeMemory,
            handle: memoryHandle
        );
    }
}
