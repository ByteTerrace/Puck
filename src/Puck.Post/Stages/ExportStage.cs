using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.DirectX;
using Puck.Vulkan;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Post;

/// <summary>
/// Tier-C stage C2. The same-adapter export/import round trip — the producer and consumer primitives every zero-copy
/// share rides — on BOTH backends:
/// <list type="bullet">
///   <item>Vulkan: create an exportable image, retrieve its opaque Win32 handle, re-import it on the same device.</item>
///   <item>Direct3D 12 (the shared LUID-matched Tier-C device): create an exportable shared render target, retrieve
///   its NT handle, re-open it on the same device.</item>
///   <item>Both: create an exportable STORAGE image (the compute counterpart) and assert its shared handle.</item>
/// </list>
/// This validates the API plumbing — shared-handle creation/export and re-open/import — against the real drivers. It
/// does NOT assert pixel contents survive (the reverse-share and camera-share stages carry that); a same-device round
/// trip is intentionally artificial, proving both backends' producer and consumer primitives function.
/// </summary>
internal sealed partial class ExportStage : IPostStage {
    private const uint RenderSize = 64;
    private const uint VulkanColorFormat = 37; // VK_FORMAT_R8G8B8A8_UNORM (the Vulkan external-memory API takes a raw VkFormat).

    /// <inheritdoc/>
    public string Name => "export";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12 + Win32 shared handles) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        var vulkanFailure = ValidateVulkan(context: context);

        if (vulkanFailure is not null) {
            return PostStageOutcome.Fail(detail: vulkanFailure);
        }

        var directXFailure = ValidateDirectX(context: context);

        if (directXFailure is not null) {
            return PostStageOutcome.Fail(detail: directXFailure);
        }

        var storageFailure = ValidateExportableStorageImages(context: context);

        if (storageFailure is not null) {
            return PostStageOutcome.Fail(detail: storageFailure);
        }

        return PostStageOutcome.Pass(detail: $"{RenderSize}x{RenderSize} R8G8B8A8 | Vulkan OPAQUE_WIN32 export re-imported; shared D3D12 target re-opened on the shared Tier-C device; exportable STORAGE images produced shared handles on both backends");
    }

    // Vulkan: export an image's opaque Win32 handle and re-import it on the same device.
    private static string? ValidateVulkan(PostContext context) {
        var deviceContext = context.Resolve<IVulkanDeviceContext>();
        var externalMemoryApi = context.Resolve<IVulkanExternalMemoryApi>();
        var deviceHandle = deviceContext.LogicalDevice.Handle;
        var instanceHandle = deviceContext.Instance.Handle;
        var physicalDeviceHandle = deviceContext.PhysicalDevice.Handle;

        var export = externalMemoryApi.CreateExportableImage(request: new VulkanExternalImageExportRequest(
            DeviceHandle: deviceHandle,
            Format: VulkanColorFormat,
            Height: RenderSize,
            InstanceHandle: instanceHandle,
            PhysicalDeviceHandle: physicalDeviceHandle,
            UsageFlags: VulkanImageUsageFlags.ColorAttachment | VulkanImageUsageFlags.Sampled | VulkanImageUsageFlags.TransferSource,
            Width: RenderSize
        ));

        if (
            (0 == export.ImageHandle) ||
            (0 == export.MemoryHandle) ||
            (0 == export.SharedHandle)
        ) {
            return "Vulkan export produced a null image, memory, or shared handle";
        }

        var import = default(VulkanExternalImageImportResult);

        try {
            import = externalMemoryApi.ImportOpaqueImage(request: new VulkanExternalImageImportRequest(
                DeviceHandle: deviceHandle,
                Format: VulkanColorFormat,
                Height: RenderSize,
                InstanceHandle: instanceHandle,
                PhysicalDeviceHandle: physicalDeviceHandle,
                SharedHandle: export.SharedHandle,
                Width: RenderSize
            ));

            if (
                (0 == import.ImageHandle) ||
                (0 == import.MemoryHandle)
            ) {
                return "Vulkan import of the exported opaque handle produced a null image or memory";
            }

            return null;
        } finally {
            if (
                (0 != import.ImageHandle) ||
                (0 != import.MemoryHandle)
            ) {
                externalMemoryApi.DestroyImage(deviceHandle: deviceHandle, imageHandle: import.ImageHandle, memoryHandle: import.MemoryHandle);
            }

            externalMemoryApi.DestroyImage(deviceHandle: deviceHandle, imageHandle: export.ImageHandle, memoryHandle: export.MemoryHandle);
            _ = CloseHandle(handle: export.SharedHandle);
        }
    }

    // Direct3D 12 (the SHARED Tier-C device, not a per-gate bespoke one): create a shared texture (CreateSharedHandle)
    // and re-open it on the same device (OpenSharedHandle).
    [SupportedOSPlatform("windows10.0.10240")]
    private static string? ValidateDirectX(PostContext context) {
        var directX = context.RequireDirectXDevice();

        using var exportable = new DirectXGpuSurfaceExportFactory().CreateExportableTarget(
            deviceContext: directX.DeviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: RenderSize,
            width: RenderSize
        );

        if (0 == exportable.SharedHandle) {
            return "Direct3D 12 export produced a null shared handle";
        }

        using var import = new DirectXGpuSurfaceTransferFactory().CreateImport(deviceContext: directX.DeviceContext);
        var importedView = import.Import(
            deviceContext: directX.DeviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: RenderSize,
            sharedHandle: exportable.SharedHandle,
            width: RenderSize
        );

        if (0 == importedView) {
            return "Direct3D 12 import of the exported handle produced a null view";
        }

        return null;
    }

    // The neutral IGpuSurfaceExportFactory.CreateExportableStorageImage on both backends — the compute counterpart of
    // the render-target export above: create a shared compute-writable image, assert the handle, dispose cleanly.
    [SupportedOSPlatform("windows10.0.10240")]
    private static string? ValidateExportableStorageImages(PostContext context) {
        var vulkanDevice = context.RequireGpuDevice();
        var vulkanExportFactory = context.Resolve<IGpuSurfaceExportFactory>();

        using (var vulkanStorageImage = vulkanExportFactory.CreateExportableStorageImage(
            deviceContext: vulkanDevice,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: RenderSize,
            width: RenderSize
        )) {
            if (0 == vulkanStorageImage.SharedHandle) {
                return "Vulkan exportable storage image produced a null shared handle";
            }
        }

        var directX = context.RequireDirectXDevice();

        using var directXStorageImage = new DirectXGpuSurfaceExportFactory().CreateExportableStorageImage(
            deviceContext: directX.DeviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: RenderSize,
            width: RenderSize
        );

        if (0 == directXStorageImage.SharedHandle) {
            return "Direct3D 12 exportable storage image produced a null shared handle";
        }

        return null;
    }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
