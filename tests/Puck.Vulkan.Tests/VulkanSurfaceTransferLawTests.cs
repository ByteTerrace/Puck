using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Abstractions.Windowing;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Pins, without a device, how the surface transfer objects hold resources on their device context's device:
/// an upload or an import whose view creation is refused after its image exists destroys that image rather than leaking
/// it, and a readback (<see cref="VulkanSurfaceReadback"/>) refuses a device other than the one it first read on and
/// refuses its release after that device is destroyed (<see cref="VulkanDeviceOwnership"/>). The command tables resolve
/// every entry point to a trap no law reaches; the image, view and command-resource APIs are fakes that record or
/// refuse.</summary>
public sealed unsafe class VulkanSurfaceTransferLawTests {
    private const nint FirstDeviceHandle = 0x0D00;
    private const nint ImageHandle = 0x0100;
    private const nint MemoryHandle = 0x0200;
    private const uint R8G8B8A8Unorm = 37U;
    private const nint SecondDeviceHandle = 0x0E00;

    [Fact]
    public void AnUploadWhoseViewIsRefusedDestroysItsImage() {
        var context = new SwitchableDeviceContext(device: LogicalDevice(handle: FirstDeviceHandle));
        var images = new RecordingImageApi();
        var views = new RefusingViewApi();
        using var upload = new VulkanSurfaceUpload(
            bufferApi: new VulkanNativeBufferApi(),
            commandBufferRecordingApi: new VulkanNativeCommandBufferRecordingApi(allocator: new RefusingAllocator()),
            commandResourcesFactory: new RefusingCommandResourcesFactory(),
            deviceContext: context,
            framebufferSetApi: views,
            offscreenImageApi: images,
            queueSubmitter: new VulkanQueueSubmitter()
        );

        _ = Assert.Throws<VulkanException>(testCode: () => upload.Upload(
            height: 1U,
            pixels: new byte[4],
            vulkanFormat: R8G8B8A8Unorm,
            width: 1U
        ));

        Assert.Equal(
            actual: Assert.Single(collection: images.Destroyed),
            expected: (ImageHandle, MemoryHandle)
        );
        Assert.DoesNotContain(
            collection: views.Destroyed,
            filter: static view => (0 != view)
        );

        upload.Dispose();

        _ = Assert.Single(collection: images.Destroyed);
    }
    [Fact]
    public void AnImportWhoseViewIsRefusedDestroysItsImage() {
        var context = new SwitchableDeviceContext(device: LogicalDevice(handle: FirstDeviceHandle));
        var memory = new RecordingExternalMemoryApi();
        var views = new RefusingViewApi();
        using var import = new VulkanSurfaceImport(
            commandBufferRecordingApi: new VulkanNativeCommandBufferRecordingApi(allocator: new RefusingAllocator()),
            commandResourcesFactory: new RefusingCommandResourcesFactory(),
            deviceContext: context,
            externalMemoryApi: memory,
            framebufferSetApi: views,
            queueSubmitter: new VulkanQueueSubmitter()
        );

        _ = Assert.Throws<VulkanException>(testCode: () => import.Import(
            height: 1U,
            sharedHandle: 0x0F00,
            vulkanFormat: R8G8B8A8Unorm,
            width: 1U
        ));

        Assert.Equal(
            actual: Assert.Single(collection: memory.Destroyed),
            expected: (ImageHandle, MemoryHandle)
        );
        Assert.Equal(
            actual: import.ImageHandle,
            expected: 0
        );

        import.Dispose();

        _ = Assert.Single(collection: memory.Destroyed);
    }
    [Fact]
    public void AReadbackRefusesADeviceOtherThanTheOneItFirstReadOn() {
        var first = LogicalDevice(handle: FirstDeviceHandle);
        var context = new SwitchableDeviceContext(device: first);
        using var readback = Readback(context: context);

        var refusedResources = Assert.Throws<InvalidOperationException>(testCode: () => Read(readback: readback));

        Assert.Equal(
            actual: refusedResources.Message,
            expected: RefusingCommandResourcesFactory.Refusal
        );

        context.LogicalDevice = LogicalDevice(handle: SecondDeviceHandle);

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => Read(readback: readback));

        Assert.StartsWith(
            actualString: refusal.Message,
            expectedStartString: $"A {nameof(VulkanSurfaceReadback)} was handed a device other"
        );
        context.LogicalDevice = first;
    }
    [Fact]
    public void AReadbackReleasedAfterItsDeviceIsDestroyedIsRefusedByName() {
        var device = LogicalDevice(handle: FirstDeviceHandle);
        var readback = Readback(context: new SwitchableDeviceContext(device: device));

        _ = Assert.Throws<InvalidOperationException>(testCode: () => Read(readback: readback));
        device.Dispose();

        var refusal = Assert.Throws<InvalidOperationException>(testCode: readback.Dispose);

        Assert.StartsWith(
            actualString: refusal.Message,
            expectedStartString: $"A {nameof(VulkanSurfaceReadback)} was released after its device was destroyed"
        );
    }

    private static VulkanLogicalDevice LogicalDevice(nint handle) =>
        new(
            device: new VulkanDeviceCommands(
                deviceHandle: handle,
                memory: null,
                procedures: Procedures()
            ),
            graphicsQueue: default,
            logicalDeviceApi: new IdleDeviceApi(),
            physicalDevice: default,
            presentQueue: default
        );
    private static VulkanProcResolver Procedures() =>
        new(
            getDeviceProcAddr: &ResolveTrap,
            getInstanceProcAddr: &ResolveTrap
        );
    private static ReadOnlyMemory<byte> Read(VulkanSurfaceReadback readback) =>
        readback.Read(
            bytesPerPixel: 4U,
            height: 1U,
            sourceImageHandle: 0x0F00,
            sourceLayout: GpuImageLayout.ShaderReadOnly,
            vulkanFormat: R8G8B8A8Unorm,
            width: 1U
        );
    private static VulkanSurfaceReadback Readback(IVulkanDeviceContext context) =>
        new(
            bufferApi: new VulkanNativeBufferApi(),
            commandBufferRecordingApi: new VulkanNativeCommandBufferRecordingApi(allocator: new RefusingAllocator()),
            commandResourcesFactory: new RefusingCommandResourcesFactory(),
            deviceContext: context,
            queueSubmitter: new VulkanQueueSubmitter()
        );
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint ResolveTrap(nint handle, byte* name) =>
        ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)&Trap));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Trap(nint first, nint second, nint third, nint fourth) => 0;

    private sealed class SwitchableDeviceContext(VulkanLogicalDevice device) : IVulkanDeviceContext {
        public VulkanInstance Instance { get; } = new(
            displayKind: default(NativeDisplayKind),
            enabledExtensions: [],
            enabledLayers: [],
            instance: new VulkanInstanceCommands(
                instanceHandle: 0x0A00,
                procedures: Procedures()
            ),
            instanceApi: new VulkanNativeInstanceApi(
                allocator: new RefusingAllocator(),
                procedures: new VulkanProcResolver()
            )
        );
        public VulkanLogicalDevice LogicalDevice { get; set; } = device;

        public VkPhysicalDevice PhysicalDevice => default;
        public VulkanSurface Surface => throw new NotSupportedException();
    }
    private sealed class IdleDeviceApi : IVulkanLogicalDeviceApi {
        public VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out VulkanDeviceCommands? device) =>
            throw new NotSupportedException();
        public void DestroyDevice(VulkanDeviceCommands device) { }
        public nint GetDeviceQueue(VulkanDeviceCommands device, uint queueFamilyIndex, uint queueIndex) =>
            throw new NotSupportedException();
        public VkResult WaitIdle(VulkanDeviceCommands device) =>
            VkResult.Success;
    }
    // Creates every image at the same handles and records each destroy.
    private sealed class RecordingImageApi : IVulkanOffscreenImageApi {
        public List<(nint Image, nint Memory)> Destroyed { get; } = [];

        public VulkanOffscreenImageCreateResult CreateColorImage(VulkanOffscreenImageCreateRequest request) =>
            new(
                ImageHandle: ImageHandle,
                MemoryHandle: MemoryHandle
            );
        public void DestroyColorImage(VulkanDeviceCommands device, nint imageHandle, nint memoryHandle) {
            if (0 != imageHandle) {
                Destroyed.Add(item: (imageHandle, memoryHandle));
            }
        }
    }
    private sealed class RecordingExternalMemoryApi : IVulkanExternalMemoryApi {
        public List<(nint Image, nint Memory)> Destroyed { get; } = [];

        public VulkanExternalImageExportResult CreateExportableImage(VulkanExternalImageExportRequest request) =>
            throw new NotSupportedException();
        public void DestroyImage(VulkanDeviceCommands device, nint imageHandle, nint memoryHandle) {
            if (0 != imageHandle) {
                Destroyed.Add(item: (imageHandle, memoryHandle));
            }
        }
        public VulkanExternalImageImportResult ImportImage(VulkanExternalImageImportRequest request) =>
            new(
                ImageHandle: ImageHandle,
                MemoryHandle: MemoryHandle
            );
        public VulkanExternalImageImportResult ImportOpaqueImage(VulkanExternalImageImportRequest request) =>
            throw new NotSupportedException();
    }
    // Refuses every view, as a device out of memory would, and records each destroy.
    private sealed class RefusingViewApi : IVulkanFramebufferSetApi {
        public List<nint> Destroyed { get; } = [];

        public VkResult CreateFramebuffer(VulkanFramebufferCreateRequest request, out nint framebufferHandle) =>
            throw new NotSupportedException();
        public VkResult CreateImageView(VulkanImageViewCreateRequest request, out nint imageViewHandle) {
            imageViewHandle = 0;

            return VkResult.ErrorOutOfDeviceMemory;
        }
        public void DestroyFramebuffer(VulkanDeviceCommands device, nint framebufferHandle) =>
            throw new NotSupportedException();
        public void DestroyImageView(VulkanDeviceCommands device, nint imageViewHandle) =>
            Destroyed.Add(item: imageViewHandle);
        public IReadOnlyList<nint> GetSwapchainImages(VulkanDeviceCommands device, nint swapchainHandle) =>
            throw new NotSupportedException();
    }
    private sealed class RefusingCommandResourcesFactory : IVulkanCommandResourcesFactory {
        public const string Refusal = "command resources refused";

        public VulkanCommandResources Create(VulkanLogicalDevice logicalDevice, uint commandBufferCount) =>
            throw new InvalidOperationException(message: Refusal);
    }
    private sealed class RefusingAllocator : IAllocator {
        public void* Allocate(nuint size, nuint alignment = 0) => throw new NotSupportedException();
        public void Free(void* ptr) => throw new NotSupportedException();
        public void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0) => throw new NotSupportedException();
    }
}
