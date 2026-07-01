using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.DirectX;
using Puck.DirectX.Interop;
using Puck.Hosting;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Demo;

/// <summary>
/// A one-shot root render node installed only under <c>--validate-export</c>. On its first frame it exercises the
/// same-device export/import plumbing on BOTH backends, then asks the terminal to exit (0 = pass, 2 = infra-fail);
/// it never presents.
/// <list type="bullet">
///   <item>Vulkan: create an exportable image, retrieve its opaque Win32 handle, re-import it on the same device.</item>
///   <item>Direct3D 12: create an exportable shared texture, retrieve its NT handle, re-open it on the same device.</item>
/// </list>
/// <para>
/// This validates the API plumbing — the shared-handle creation/export and re-open/import paths — against the real
/// drivers. It does NOT assert pixel contents survive the share (that needs queue-family / cross-API handoff
/// semantics). A same-device round trip is intentionally artificial: real sharing crosses a device, process, or API
/// boundary; this proves both backends' producer and consumer primitives function.
/// </para>
/// </summary>
internal sealed partial class ExportRoundTripNode : IRenderNode {
    private const uint RenderSize = 64;
    private const uint VulkanColorFormat = 37; // VK_FORMAT_R8G8B8A8_UNORM (the Vulkan external-memory API takes a raw VkFormat).

    private readonly NodeDescriptor m_descriptor = new(
        Name: "export-roundtrip",
        SurfaceId: SurfaceId.New()
    );
    private readonly ParityResult m_result;
    private readonly IServiceProvider m_serviceProvider;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="ExportRoundTripNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (resolves the live Vulkan and Direct3D 12 devices and APIs).</param>
    /// <param name="result">The shared result the exit code is written to.</param>
    public ExportRoundTripNode(IServiceProvider serviceProvider, ParityResult result) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(result);

        m_result = result;
        m_serviceProvider = serviceProvider;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_done) {
            return default;
        }

        m_done = true;

        try {
            ValidateVulkan();
            ValidateDirectX();
            ValidateExportableStorageImages();
            m_result.ExitCode = 0;
        } catch (Exception exception) {
            // Fail loudly, never silently pass.
            Console.Error.WriteLine(value: $"EXPORT infra-fail | {exception.Message}");
            m_result.ExitCode = 2;
        }

        // Request exit after the verdict is recorded so the exit code is always observable.
        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    private void ValidateVulkan() {
        var deviceContext = (IVulkanDeviceContext)m_serviceProvider.GetService(serviceType: typeof(IVulkanDeviceContext))!;
        var externalMemoryApi = (IVulkanExternalMemoryApi)m_serviceProvider.GetService(serviceType: typeof(IVulkanExternalMemoryApi))!;
        var deviceHandle = deviceContext.LogicalDevice.Handle;
        var instanceHandle = deviceContext.Instance.Handle;
        var physicalDeviceHandle = deviceContext.PhysicalDevice.Handle;

        var export = externalMemoryApi.CreateExportableImage(request: new VulkanExternalImageExportRequest(
            DeviceHandle: deviceHandle,
            Format: VulkanColorFormat,
            Height: RenderSize,
            InstanceHandle: instanceHandle,
            PhysicalDeviceHandle: physicalDeviceHandle,
            UsageFlags: Puck.Vulkan.VulkanImageUsageFlags.ColorAttachment | Puck.Vulkan.VulkanImageUsageFlags.Sampled | Puck.Vulkan.VulkanImageUsageFlags.TransferSource,
            Width: RenderSize
        ));

        if (
            (0 == export.ImageHandle) ||
            (0 == export.MemoryHandle) ||
            (0 == export.SharedHandle)
        ) {
            throw new InvalidOperationException(message: "Vulkan export produced a null image, memory, or shared handle.");
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
                throw new InvalidOperationException(message: "Vulkan import of the exported handle produced a null image or memory.");
            }

            Console.Out.WriteLine(value: $"VK-EXPORT pass | {RenderSize}x{RenderSize} R8G8B8A8 | exported OPAQUE_WIN32 handle 0x{export.SharedHandle:X} re-imported zero-copy on the same Vulkan device");
        } finally {
            if (
                (0 != import.ImageHandle) ||
                (0 != import.MemoryHandle)
            ) {
                externalMemoryApi.DestroyImage(
                    deviceHandle: deviceHandle,
                    imageHandle: import.ImageHandle,
                    memoryHandle: import.MemoryHandle
                );
            }

            externalMemoryApi.DestroyImage(
                deviceHandle: deviceHandle,
                imageHandle: export.ImageHandle,
                memoryHandle: export.MemoryHandle
            );
            _ = CloseHandle(handle: export.SharedHandle);
        }
    }
    private void ValidateDirectX() {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            Console.Out.WriteLine(value: "DX-EXPORT skip | Direct3D 12 requires Windows 10.0.10240+");

            return;
        }

        ValidateDirectXCore();
    }
    [SupportedOSPlatform("windows10.0.10240")]
    private void ValidateDirectXCore() {
        var deviceContext = (DirectXDeviceContext)m_serviceProvider.GetService(serviceType: typeof(DirectXDeviceContext))!;

        // Construct the shared texture (CreateSharedHandle) and re-open it on the same device (OpenSharedHandle).
        using var exportable = new DirectXGpuSurfaceExportFactory().CreateExportableTarget(
            deviceContext: deviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: RenderSize,
            width: RenderSize
        );

        if (0 == exportable.SharedHandle) {
            throw new InvalidOperationException(message: "Direct3D 12 export produced a null shared handle.");
        }

        using var import = new DirectXGpuSurfaceTransferFactory().CreateImport(deviceContext: deviceContext);
        var importedView = import.Import(
            deviceContext: deviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: RenderSize,
            sharedHandle: exportable.SharedHandle,
            width: RenderSize
        );

        if (0 == importedView) {
            throw new InvalidOperationException(message: "Direct3D 12 import of the exported handle produced a null view.");
        }

        Console.Out.WriteLine(value: $"DX-EXPORT pass | {RenderSize}x{RenderSize} R8G8B8A8 | shared D3D12 resource handle 0x{exportable.SharedHandle:X} re-opened on the same Direct3D 12 device");
    }

    // Exercises the neutral IGpuSurfaceExportFactory.CreateExportableStorageImage on both backends — the compute
    // counterpart of the render-target export above. The Vulkan storage path is otherwise untouched by the
    // cross-backend present showcase (which always hosts on Vulkan and produces on Direct3D 12), so this is its
    // coverage: create a shared compute-writable image, assert the shared handle, and dispose cleanly.
    private void ValidateExportableStorageImages() {
        var vulkanDeviceContext = (IVulkanDeviceContext)m_serviceProvider.GetService(serviceType: typeof(IVulkanDeviceContext))!;
        var exportFactory = (IGpuSurfaceExportFactory)m_serviceProvider.GetService(serviceType: typeof(IGpuSurfaceExportFactory))!;

        using (var storageImage = exportFactory.CreateExportableStorageImage(
            deviceContext: (IGpuDeviceContext)vulkanDeviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: RenderSize,
            width: RenderSize
        )) {
            if (0 == storageImage.SharedHandle) {
                throw new InvalidOperationException(message: "Vulkan exportable storage image produced a null shared handle.");
            }

            Console.Out.WriteLine(value: $"VK-STORAGE-EXPORT pass | {RenderSize}x{RenderSize} R8G8B8A8 | exportable STORAGE image OPAQUE_WIN32 handle 0x{storageImage.SharedHandle:X}");
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return;
        }

        ValidateDirectXExportableStorageImage();
    }
    [SupportedOSPlatform("windows10.0.10240")]
    private void ValidateDirectXExportableStorageImage() {
        var deviceContext = (DirectXDeviceContext)m_serviceProvider.GetService(serviceType: typeof(DirectXDeviceContext))!;

        using var storageImage = new DirectXGpuSurfaceExportFactory().CreateExportableStorageImage(
            deviceContext: deviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: RenderSize,
            width: RenderSize
        );

        if (0 == storageImage.SharedHandle) {
            throw new InvalidOperationException(message: "Direct3D 12 exportable storage image produced a null shared handle.");
        }

        Console.Out.WriteLine(value: $"DX-STORAGE-EXPORT pass | {RenderSize}x{RenderSize} R8G8B8A8 | shared D3D12 UAV-texture handle 0x{storageImage.SharedHandle:X}");
    }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
