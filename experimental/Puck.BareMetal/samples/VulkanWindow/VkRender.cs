// Puck.BareMetal VulkanWindow — swapchain + clear-to-color present loop (puck).
//
// Builds the presentation chain on the device from Vk.cs: swapchain -> image views ->
// render pass (loadOp=CLEAR) -> framebuffers -> command buffers that just begin/end the
// render pass (the clear) -> per-frame acquire/submit/present. Managed arrays (GC statics +
// heap allocation) hold the per-image objects.
using System;
using Puck.Vulkan.Bindings;

namespace Puck.BareMetal.VulkanWindow;

internal static unsafe partial class Vk {
    private const uint VK_ATTACHMENT_LOAD_OP_CLEAR = 1;
    private const uint VK_ATTACHMENT_LOAD_OP_DONT_CARE = 2;
    private const uint VK_ATTACHMENT_STORE_OP_DONT_CARE = 1;
    private const uint VK_ATTACHMENT_STORE_OP_STORE = 0;
    private const uint VK_COLOR_SPACE_SRGB_NONLINEAR_KHR = 0;
    private const uint VK_COMMAND_BUFFER_LEVEL_PRIMARY = 0;
    private const uint VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR = 0x1;
    private const uint VK_FORMAT_B8G8R8A8_UNORM = 44;
    private const uint VK_IMAGE_ASPECT_COLOR_BIT = 0x1;
    private const uint VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL = 2;
    private const uint VK_IMAGE_LAYOUT_PRESENT_SRC_KHR = 1000001002;
    private const uint VK_IMAGE_LAYOUT_UNDEFINED = 0;
    private const uint VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT = 0x10;
    private const uint VK_IMAGE_VIEW_TYPE_2D = 1;
    private const uint VK_PIPELINE_BIND_POINT_GRAPHICS = 0;
    private const uint VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT = 0x400;
    private const uint VK_PRESENT_MODE_FIFO_KHR = 2;
    private const uint VK_SAMPLE_COUNT_1_BIT = 0x1;
    private const uint VK_SHARING_MODE_EXCLUSIVE = 0;
    private const uint VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO = 40;
    private const uint VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO = 42;
    private const uint VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO = 39;
    private const uint VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO = 37;
    private const uint VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO = 15;
    private const uint VK_STRUCTURE_TYPE_PRESENT_INFO_KHR = 1000001001;
    private const uint VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO = 43;
    private const uint VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO = 38;
    private const uint VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO = 9;
    private const uint VK_STRUCTURE_TYPE_SUBMIT_INFO = 4;
    private const uint VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR = 1000001000;
    private const uint VK_SUBPASS_CONTENTS_INLINE = 0;
    private const uint VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR = 0x1;
    private const ulong VK_WHOLE_TIMEOUT = 0xFFFFFFFFFFFFFFFFUL;

    private static ulong s_swapchain;
    private static uint s_format;
    private static uint s_width;
    private static uint s_height;
    private static uint s_imageCount;
    private static ulong[] s_imageViews;
    private static ulong[] s_framebuffers;
    private static nint[] s_commandBuffers;
    private static ulong s_renderPass;
    private static ulong s_commandPool;
    private static ulong s_acquireSemaphore;
    private static ulong s_renderSemaphore;
    private static delegate* unmanaged<nint, ulong, VkSurfaceCapabilitiesKhr*, int> s_getSurfaceCaps;
    private static delegate* unmanaged<nint, ulong, uint*, VkSurfaceFormatKhr*, int> s_getSurfaceFormats;
    private static delegate* unmanaged<nint, VkSwapchainCreateInfoKhr*, nint, ulong*, int> s_createSwapchain;
    private static delegate* unmanaged<nint, ulong, uint*, ulong*, int> s_getSwapchainImages;
    private static delegate* unmanaged<nint, VkImageViewCreateInfo*, nint, ulong*, int> s_createImageView;
    private static delegate* unmanaged<nint, VkRenderPassCreateInfo*, nint, ulong*, int> s_createRenderPass;
    private static delegate* unmanaged<nint, VkFramebufferCreateInfo*, nint, ulong*, int> s_createFramebuffer;
    private static delegate* unmanaged<nint, VkCommandPoolCreateInfo*, nint, ulong*, int> s_createCommandPool;
    private static delegate* unmanaged<nint, VkCommandBufferAllocateInfo*, nint*, int> s_allocateCommandBuffers;
    private static delegate* unmanaged<nint, VkCommandBufferBeginInfo*, int> s_beginCommandBuffer;
    private static delegate* unmanaged<nint, VkRenderPassBeginInfo*, uint, void> s_cmdBeginRenderPass;
    private static delegate* unmanaged<nint, void> s_cmdEndRenderPass;
    private static delegate* unmanaged<nint, int> s_endCommandBuffer;
    private static delegate* unmanaged<nint, VkSemaphoreCreateInfo*, nint, ulong*, int> s_createSemaphore;
    private static delegate* unmanaged<nint, ulong, ulong, ulong, ulong, uint*, int> s_acquireNextImage;
    private static delegate* unmanaged<nint, uint, VkSubmitInfo*, ulong, int> s_queueSubmit;
    private static delegate* unmanaged<nint, VkPresentInfoKhr*, int> s_queuePresent;
    private static delegate* unmanaged<nint, int> s_queueWaitIdle;
    private static delegate* unmanaged<nint, int> s_deviceWaitIdle;
    private static delegate* unmanaged<nint, ulong, nint, void> s_destroySemaphore;
    private static delegate* unmanaged<nint, ulong, nint, void> s_destroyCommandPool;
    private static delegate* unmanaged<nint, ulong, nint, void> s_destroyFramebuffer;
    private static delegate* unmanaged<nint, ulong, nint, void> s_destroyImageView;
    private static delegate* unmanaged<nint, ulong, nint, void> s_destroyRenderPass;
    private static delegate* unmanaged<nint, ulong, nint, void> s_destroySwapchain;
    private static delegate* unmanaged<nint, nint, void> s_destroyDevice;
    private static delegate* unmanaged<nint, ulong, nint, void> s_destroySurface;
    private static delegate* unmanaged<nint, nint, void> s_destroyInstance;

    private static void LoadRenderProcs() {
        s_getSurfaceCaps = (delegate* unmanaged<nint, ulong, VkSurfaceCapabilitiesKhr*, int>)InstanceProc(instance: Instance, name: "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
        s_getSurfaceFormats = (delegate* unmanaged<nint, ulong, uint*, VkSurfaceFormatKhr*, int>)InstanceProc(instance: Instance, name: "vkGetPhysicalDeviceSurfaceFormatsKHR");
        s_createSwapchain = (delegate* unmanaged<nint, VkSwapchainCreateInfoKhr*, nint, ulong*, int>)DeviceProc(name: "vkCreateSwapchainKHR");
        s_getSwapchainImages = (delegate* unmanaged<nint, ulong, uint*, ulong*, int>)DeviceProc(name: "vkGetSwapchainImagesKHR");
        s_createImageView = (delegate* unmanaged<nint, VkImageViewCreateInfo*, nint, ulong*, int>)DeviceProc(name: "vkCreateImageView");
        s_createRenderPass = (delegate* unmanaged<nint, VkRenderPassCreateInfo*, nint, ulong*, int>)DeviceProc(name: "vkCreateRenderPass");
        s_createFramebuffer = (delegate* unmanaged<nint, VkFramebufferCreateInfo*, nint, ulong*, int>)DeviceProc(name: "vkCreateFramebuffer");
        s_createCommandPool = (delegate* unmanaged<nint, VkCommandPoolCreateInfo*, nint, ulong*, int>)DeviceProc(name: "vkCreateCommandPool");
        s_allocateCommandBuffers = (delegate* unmanaged<nint, VkCommandBufferAllocateInfo*, nint*, int>)DeviceProc(name: "vkAllocateCommandBuffers");
        s_beginCommandBuffer = (delegate* unmanaged<nint, VkCommandBufferBeginInfo*, int>)DeviceProc(name: "vkBeginCommandBuffer");
        s_cmdBeginRenderPass = (delegate* unmanaged<nint, VkRenderPassBeginInfo*, uint, void>)DeviceProc(name: "vkCmdBeginRenderPass");
        s_cmdEndRenderPass = (delegate* unmanaged<nint, void>)DeviceProc(name: "vkCmdEndRenderPass");
        s_endCommandBuffer = (delegate* unmanaged<nint, int>)DeviceProc(name: "vkEndCommandBuffer");
        s_createSemaphore = (delegate* unmanaged<nint, VkSemaphoreCreateInfo*, nint, ulong*, int>)DeviceProc(name: "vkCreateSemaphore");
        s_acquireNextImage = (delegate* unmanaged<nint, ulong, ulong, ulong, ulong, uint*, int>)DeviceProc(name: "vkAcquireNextImageKHR");
        s_queueSubmit = (delegate* unmanaged<nint, uint, VkSubmitInfo*, ulong, int>)DeviceProc(name: "vkQueueSubmit");
        s_queuePresent = (delegate* unmanaged<nint, VkPresentInfoKhr*, int>)DeviceProc(name: "vkQueuePresentKHR");
        s_queueWaitIdle = (delegate* unmanaged<nint, int>)DeviceProc(name: "vkQueueWaitIdle");
        s_deviceWaitIdle = (delegate* unmanaged<nint, int>)DeviceProc(name: "vkDeviceWaitIdle");

        s_destroySemaphore = (delegate* unmanaged<nint, ulong, nint, void>)DeviceProc(name: "vkDestroySemaphore");
        s_destroyCommandPool = (delegate* unmanaged<nint, ulong, nint, void>)DeviceProc(name: "vkDestroyCommandPool");
        s_destroyFramebuffer = (delegate* unmanaged<nint, ulong, nint, void>)DeviceProc(name: "vkDestroyFramebuffer");
        s_destroyImageView = (delegate* unmanaged<nint, ulong, nint, void>)DeviceProc(name: "vkDestroyImageView");
        s_destroyRenderPass = (delegate* unmanaged<nint, ulong, nint, void>)DeviceProc(name: "vkDestroyRenderPass");
        s_destroySwapchain = (delegate* unmanaged<nint, ulong, nint, void>)DeviceProc(name: "vkDestroySwapchainKHR");
        s_destroyDevice = (delegate* unmanaged<nint, nint, void>)InstanceProc(instance: Instance, name: "vkDestroyDevice");
        s_destroySurface = (delegate* unmanaged<nint, ulong, nint, void>)InstanceProc(instance: Instance, name: "vkDestroySurfaceKHR");
        s_destroyInstance = (delegate* unmanaged<nint, nint, void>)InstanceProc(instance: Instance, name: "vkDestroyInstance");
    }

    // Tear down every Vulkan object this renderer owns, in reverse creation order. Called by
    // the disposable that owns the renderer; the GPU/driver resources are released here rather
    // than only at process exit.
    public static void Shutdown() {
        if ((Device != 0) && (s_deviceWaitIdle is not null))
            s_deviceWaitIdle(Device);

        if (s_renderSemaphore != 0) s_destroySemaphore(Device, s_renderSemaphore, 0);
        if (s_acquireSemaphore != 0) s_destroySemaphore(Device, s_acquireSemaphore, 0);
        if (s_commandPool != 0) s_destroyCommandPool(Device, s_commandPool, 0); // frees its command buffers

        if (s_framebuffers is not null)
            for (uint i = 0; (i < s_imageCount); i++)
                if (s_framebuffers[i] != 0) s_destroyFramebuffer(Device, s_framebuffers[i], 0);

        if (s_imageViews is not null)
            for (uint i = 0; (i < s_imageCount); i++)
                if (s_imageViews[i] != 0) s_destroyImageView(Device, s_imageViews[i], 0);

        if (s_renderPass != 0) s_destroyRenderPass(Device, s_renderPass, 0);
        if (s_swapchain != 0) s_destroySwapchain(Device, s_swapchain, 0);
        if (Device != 0) s_destroyDevice(Device, 0);
        if (Surface != 0) s_destroySurface(Instance, Surface, 0);
        if (Instance != 0) s_destroyInstance(Instance, 0);

        Diag.Log(message: "vulkan teardown complete");
    }
    public static bool InitializeRendering(uint width, uint height) {
        LoadRenderProcs();

        // Surface capabilities + format.
        VkSurfaceCapabilitiesKhr caps;

        s_getSurfaceCaps(PhysicalDevice, Surface, &caps);
        s_width = ((caps.CurrentExtent.Width != 0xFFFFFFFF) ? caps.CurrentExtent.Width : width);
        s_height = ((caps.CurrentExtent.Height != 0xFFFFFFFF) ? caps.CurrentExtent.Height : height);

        var formatCount = 0U;

        s_getSurfaceFormats(PhysicalDevice, Surface, &formatCount, null);
        if (formatCount == 0) { Diag.Log(message: "no surface formats"); return false; }
        if (formatCount > 32) formatCount = 32;
        VkSurfaceFormatKhr* formats = stackalloc VkSurfaceFormatKhr[32];

        s_getSurfaceFormats(PhysicalDevice, Surface, &formatCount, formats);
        s_format = formats[0].Format;
        uint colorSpace = formats[0].ColorSpace;

        for (uint i = 0; (i < formatCount); i++) {
            if (formats[i].Format == VK_FORMAT_B8G8R8A8_UNORM) {
                s_format = VK_FORMAT_B8G8R8A8_UNORM;
                colorSpace = formats[i].ColorSpace;
                break;
            }
        }

        uint imageCount = (caps.MinImageCount + 1);

        if ((caps.MaxImageCount != 0) && (imageCount > caps.MaxImageCount))
            imageCount = caps.MaxImageCount;

        VkSwapchainCreateInfoKhr swapchainInfo = default;

        swapchainInfo.SType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR;
        swapchainInfo.Surface = (nint)Surface;
        swapchainInfo.MinImageCount = imageCount;
        swapchainInfo.ImageFormat = s_format;
        swapchainInfo.ImageColorSpace = colorSpace;
        swapchainInfo.ImageExtent = new VkExtent2D(s_width, s_height);
        swapchainInfo.ImageArrayLayers = 1;
        swapchainInfo.ImageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
        swapchainInfo.ImageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
        swapchainInfo.PreTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
        swapchainInfo.CompositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
        swapchainInfo.PresentMode = VK_PRESENT_MODE_FIFO_KHR;
        swapchainInfo.Clipped = 1;

        ulong swapchain;
        int r = s_createSwapchain(Device, &swapchainInfo, 0, &swapchain);

        if (r != 0) { Diag.LogNum(label: "vkCreateSwapchainKHR failed", value: r); return false; }
        s_swapchain = swapchain;

        // Swapchain images.
        var actualImages = 0U;

        s_getSwapchainImages(Device, swapchain, &actualImages, null);
        if (actualImages > 8) actualImages = 8;
        s_imageCount = actualImages;
        ulong* images = stackalloc ulong[8];

        s_getSwapchainImages(Device, swapchain, &actualImages, images);
        Diag.LogNum(label: "swapchain images:", value: s_imageCount);

        // Render pass: single color attachment, cleared on load, ready to present.
        VkAttachmentDescription attachment = default;

        attachment.Format = s_format;
        attachment.Samples = VK_SAMPLE_COUNT_1_BIT;
        attachment.LoadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
        attachment.StoreOp = VK_ATTACHMENT_STORE_OP_STORE;
        attachment.StencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
        attachment.StencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
        attachment.InitialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        attachment.FinalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;

        VkAttachmentReference colorRef = default;

        colorRef.Attachment = 0;
        colorRef.Layout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;

        VkSubpassDescription subpass = default;

        subpass.PipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
        subpass.ColorAttachmentCount = 1;
        subpass.PColorAttachments = (nint)(&colorRef);

        VkRenderPassCreateInfo renderPassInfo = default;

        renderPassInfo.SType = VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO;
        renderPassInfo.AttachmentCount = 1;
        renderPassInfo.PAttachments = (nint)(&attachment);
        renderPassInfo.SubpassCount = 1;
        renderPassInfo.PSubpasses = (nint)(&subpass);

        ulong renderPass;

        r = s_createRenderPass(Device, &renderPassInfo, 0, &renderPass);
        if (r != 0) { Diag.LogNum(label: "vkCreateRenderPass failed", value: r); return false; }
        s_renderPass = renderPass;

        // Image views + framebuffers.
        s_imageViews = new ulong[s_imageCount];
        s_framebuffers = new ulong[s_imageCount];
        for (uint i = 0; (i < s_imageCount); i++) {
            VkImageViewCreateInfo viewInfo = default;

            viewInfo.SType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
            viewInfo.Image = (nint)images[i];
            viewInfo.ViewType = VK_IMAGE_VIEW_TYPE_2D;
            viewInfo.Format = s_format;
            viewInfo.SubresourceRange.AspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
            viewInfo.SubresourceRange.LevelCount = 1;
            viewInfo.SubresourceRange.LayerCount = 1;

            ulong view;

            r = s_createImageView(Device, &viewInfo, 0, &view);
            if (r != 0) { Diag.LogNum(label: "vkCreateImageView failed", value: r); return false; }
            s_imageViews[i] = view;

            ulong attachmentView = view;
            VkFramebufferCreateInfo fbInfo = default;

            fbInfo.SType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO;
            fbInfo.RenderPass = (nint)renderPass;
            fbInfo.AttachmentCount = 1;
            fbInfo.PAttachments = (nint)(&attachmentView);
            fbInfo.Width = s_width;
            fbInfo.Height = s_height;
            fbInfo.Layers = 1;

            ulong framebuffer;

            r = s_createFramebuffer(Device, &fbInfo, 0, &framebuffer);
            if (r != 0) { Diag.LogNum(label: "vkCreateFramebuffer failed", value: r); return false; }
            s_framebuffers[i] = framebuffer;
        }

        // Command pool + per-image command buffers recording the clear.
        VkCommandPoolCreateInfo poolInfo = default;

        poolInfo.SType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
        poolInfo.QueueFamilyIndex = QueueFamily;
        ulong pool;

        r = s_createCommandPool(Device, &poolInfo, 0, &pool);
        if (r != 0) { Diag.LogNum(label: "vkCreateCommandPool failed", value: r); return false; }
        s_commandPool = pool;

        s_commandBuffers = new nint[s_imageCount];
        VkCommandBufferAllocateInfo allocInfo = default;

        allocInfo.SType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
        allocInfo.CommandPool = (nint)pool;
        allocInfo.Level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
        allocInfo.CommandBufferCount = s_imageCount;
        fixed (nint* cmdBufs = s_commandBuffers) {
            r = s_allocateCommandBuffers(Device, &allocInfo, cmdBufs);
        }
        if (r != 0) { Diag.LogNum(label: "vkAllocateCommandBuffers failed", value: r); return false; }

        for (uint i = 0; (i < s_imageCount); i++) {
            nint cmd = s_commandBuffers[i];

            VkCommandBufferBeginInfo beginInfo = default;

            beginInfo.SType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
            s_beginCommandBuffer(cmd, &beginInfo);

            VkClearValue clear = default;

            clear.Color = new VkClearColorValue(0.16f, 0.10f, 0.28f, 1.0f); // Puck-ish purple

            VkRenderPassBeginInfo rpBegin = default;

            rpBegin.SType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO;
            rpBegin.RenderPass = (nint)renderPass;
            rpBegin.Framebuffer = (nint)s_framebuffers[i];
            rpBegin.RenderArea = new VkRect2D(new VkOffset2D(0, 0), new VkExtent2D(s_width, s_height));
            rpBegin.ClearValueCount = 1;
            rpBegin.PClearValues = (nint)(&clear);

            s_cmdBeginRenderPass(cmd, &rpBegin, VK_SUBPASS_CONTENTS_INLINE);
            s_cmdEndRenderPass(cmd);
            s_endCommandBuffer(cmd);
        }

        // Sync objects.
        VkSemaphoreCreateInfo semInfo = default;

        semInfo.SType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
        ulong acquire, render;

        s_createSemaphore(Device, &semInfo, 0, &acquire);
        s_createSemaphore(Device, &semInfo, 0, &render);
        s_acquireSemaphore = acquire;
        s_renderSemaphore = render;

        Diag.Log(message: "swapchain + render pass ready");
        return true;
    }

    // Acquire -> submit (clear) -> present, serialized with vkQueueWaitIdle.
    public static void DrawFrame() {
        var imageIndex = 0U;
        int r = s_acquireNextImage(Device, s_swapchain, VK_WHOLE_TIMEOUT, s_acquireSemaphore, 0, &imageIndex);

        if (r != 0) return;

        ulong waitSemaphore = s_acquireSemaphore;
        ulong signalSemaphore = s_renderSemaphore;
        uint waitStage = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
        nint commandBuffer = s_commandBuffers[imageIndex];

        VkSubmitInfo submit = default;

        submit.SType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
        submit.WaitSemaphoreCount = 1;
        submit.PWaitSemaphores = (nint)(&waitSemaphore);
        submit.PWaitDstStageMask = (nint)(&waitStage);
        submit.CommandBufferCount = 1;
        submit.PCommandBuffers = (nint)(&commandBuffer);
        submit.SignalSemaphoreCount = 1;
        submit.PSignalSemaphores = (nint)(&signalSemaphore);
        s_queueSubmit(Queue, 1, &submit, 0);

        ulong presentSwapchain = s_swapchain;
        VkPresentInfoKhr present = default;

        present.SType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR;
        present.WaitSemaphoreCount = 1;
        present.PWaitSemaphores = (nint)(&signalSemaphore);
        present.SwapchainCount = 1;
        present.PSwapchains = (nint)(&presentSwapchain);
        present.PImageIndices = (nint)(&imageIndex);
        s_queuePresent(Queue, &present);

        s_queueWaitIdle(Queue);
    }
}
