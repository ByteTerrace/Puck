using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Platform.Windows;
using Puck.Testing;
using Puck.Vulkan;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Factories;
using Puck.Vulkan.Interop;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// A Direct3D 11 writer and a reader on another API, on one adapter, ordered only by a Direct3D 12 shared fence: the
/// reader's submission waits on the GPU for the value the writer's copy signals (<see cref="Win32D3D11CompletionSignal"/>)
/// and the writer never waits on the CPU. Each law submits the reader's wait before the writer has written anything, so
/// the submission cannot retire until the signal, and then reads the pattern. The Direct3D 12 reader runs on the first
/// hardware adapter and on the software (WARP) renderer; the Vulkan reader imports the fence as a timeline semaphore
/// and skips by name on a device without <c>VK_KHR_external_semaphore_win32</c>.
/// </summary>
[SupportedOSPlatform("windows10.0.15063")]
public sealed unsafe class SharedFenceLawTests {
    private const int Extent = 64;

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ADirect3D12ReadOrderedOnlyByTheSharedFenceReadsTheDirect3D11WritersPattern(bool warp) {
        using var writer = (SharedFenceWriter.TryCreate(warp: warp) ?? Skipped<SharedFenceWriter>(reason: $"no Direct3D 11 {(warp ? "WARP" : "hardware")} device on this host"));
        using var context = Direct3D12(
            adapterLuid: writer.AdapterLuid,
            warp: warp
        );
        var services = context.Services;
        var export = new DirectXGpuSurfaceExportFactory(deviceContext: context);
        using var image = export.CreateSimultaneousAccessImage(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: Extent,
            width: Extent
        );
        using var fence = export.CreateExportableFence();
        using var pool = services.CommandPoolFactory.Create(name: default);
        using var submission = services.QueueSubmitter.CreateSubmissionFence();
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        using var signal = new Win32D3D11CompletionSignal(
            context: writer.Context,
            device: writer.Device,
            sharedFenceHandle: fence.SharedHandle
        );
        var target = writer.Open(sharedHandle: image.SharedHandle);
        var pattern = Pattern();

        Assert.True(
            condition: signal.Order.SharedFence,
            userMessage: $"the Direct3D 11 {(warp ? "WARP" : "hardware")} device keeps the CPU wait: {signal.Order}"
        );

        services.Recorder.BeginCommandBuffer(commandBufferHandle: pool.CommandBufferHandle);
        services.Recorder.EndCommandBuffer(commandBufferHandle: pool.CommandBufferHandle);
        services.QueueSubmitter.AddExternalWait(wait: new GpuExternalWait(
            Fence: fence,
            Value: 1UL
        ));
        services.QueueSubmitter.Submit(
            commandBufferHandles: [pool.CommandBufferHandle],
            fence: submission
        );

        Assert.False(condition: submission.IsSignaled);

        writer.Write(
            pixels: pattern,
            target: target,
            width: Extent
        );

        Assert.Equal(
            actual: signal.Complete(),
            expected: 1UL
        );

        submission.Wait();

        Assert.Equal(
            actual: fence.CompletedValue,
            expected: 1UL
        );
        Assert.Equal(
            actual: readback.Read(
                bytesPerPixel: 4,
                format: GpuPixelFormat.R8G8B8A8Unorm,
                height: Extent,
                sourceImageHandle: image.ImageHandle,
                sourceLayout: GpuImageLayout.External,
                width: Extent
            ).ToArray(),
            expected: pattern
        );
    }
    [Fact]
    public void AVulkanSubmissionWaitsOnTheSharedFenceImportedAsATimelineSemaphore() {
        using var writer = (SharedFenceWriter.TryCreate(warp: false) ?? Skipped<SharedFenceWriter>(reason: "no Direct3D 11 hardware device on this host"));
        using var context = Direct3D12(
            adapterLuid: writer.AdapterLuid,
            warp: false
        );
        using var fence = new DirectXGpuSurfaceExportFactory(deviceContext: context).CreateExportableFence();
        var allocator = new HeapAllocator();
        var procedures = new VulkanProcResolver();
        VulkanInstance instance;

        try {
            instance = new VulkanInstanceFactory(instanceApi: new VulkanNativeInstanceApi(
                allocator: allocator,
                procedures: procedures
            )).Create(
                applicationName: nameof(SharedFenceLawTests),
                displayKind: Puck.Abstractions.Windowing.NativeDisplayKind.Win32,
                enableValidation: false
            );
        } catch (GpuDeviceUnavailableException exception) {
            Assert.Skip(reason: $"no Vulkan loader or driver: {exception.Message}");

            return;
        }

        using (instance) {
            var physicalDeviceApi = new VulkanNativePhysicalDeviceApi(allocator: allocator);
            var physicalDevice = GraphicsDeviceOn(
                adapterLuid: writer.AdapterLuid,
                instance: instance,
                physicalDeviceApi: physicalDeviceApi
            );
            VulkanLogicalDevice created;

            try {
                created = new VulkanLogicalDeviceFactory(
                    logicalDeviceApi: new VulkanNativeLogicalDeviceApi(
                        allocator: allocator,
                        procedures: procedures
                    ),
                    physicalDeviceApi: physicalDeviceApi,
                    pipelineCacheStore: null,
                    pipelineCacheWork: new GpuPipelineCacheWork(backend: "vulkan")
                ).Create(
                    instance: instance,
                    physicalDevice: physicalDevice
                );
            } catch (GpuDeviceUnavailableException exception) {
                Assert.Skip(reason: $"no usable Vulkan device: {exception.Message}");

                return;
            }

            using var device = created;
            var commands = device.Commands;

            if (commands.ImportSemaphoreWin32HandleKhr is null) {
                Assert.Skip(reason: "the Vulkan device reports no VK_KHR_external_semaphore_win32");
            }

            Assert.True(
                condition: VulkanSharedFence.TryImport(
                    device: commands,
                    fence: out var imported,
                    refusal: out var refusal,
                    sharedHandle: fence.SharedHandle
                ),
                userMessage: refusal
            );

            using (imported) {
                using var signal = new Win32D3D11CompletionSignal(
                    context: writer.Context,
                    device: writer.Device,
                    sharedFenceHandle: fence.SharedHandle
                );
                var commandPool = CommandPool(
                    commands: commands,
                    queueFamilyIndex: device.GraphicsQueue.FamilyIndex
                );
                var submitted = CreateFence(commands: commands);

                try {
                    var commandBuffer = EmptyCommandBuffer(
                        commandPool: commandPool,
                        commands: commands
                    );

                    Assert.Equal(
                        actual: imported.CompletedValue,
                        expected: 0UL
                    );

                    new VulkanQueueSubmitter().Submit(
                        commandBufferHandles: [commandBuffer],
                        device: commands,
                        fenceHandle: submitted,
                        graphicsQueue: device.GraphicsQueue,
                        waitSemaphores: [imported.SemaphoreHandle],
                        waitValues: [1UL]
                    );

                    Assert.Equal(
                        actual: commands.GetFenceStatus(commands.Handle, submitted),
                        expected: VkResult.NotReady
                    );
                    Assert.Equal(
                        actual: signal.Complete(),
                        expected: 1UL
                    );
                    Assert.Equal(
                        actual: commands.WaitForFences(commands.Handle, 1, in submitted, 1, ulong.MaxValue),
                        expected: VkResult.Success
                    );
                    Assert.Equal(
                        actual: imported.CompletedValue,
                        expected: 1UL
                    );
                } finally {
                    _ = commands.DeviceWaitIdle(commands.Handle);
                    commands.Destroy(
                        destroy: commands.DestroyFence,
                        handle: submitted
                    );
                    commands.Destroy(
                        destroy: commands.DestroyCommandPool,
                        handle: commandPool
                    );
                }
            }
        }
    }

    private static nint CommandPool(VulkanDeviceCommands commands, uint queueFamilyIndex) {
        var info = new VkCommandPoolCreateInfo {
            QueueFamilyIndex = queueFamilyIndex,
            SType = 39,
        };

        commands.CreateCommandPool(commands.Handle, in info, 0, out var pool).ThrowIfFailed(operation: "vkCreateCommandPool");

        return pool;
    }
    private static nint CreateFence(VulkanDeviceCommands commands) {
        var info = new VkFenceCreateInfo { SType = 8 };

        commands.CreateFence(commands.Handle, in info, 0, out var fence).ThrowIfFailed(operation: "vkCreateFence");

        return fence;
    }
    private static DirectXDeviceContext Direct3D12(long adapterLuid, bool warp) {
        var context = new DirectXDeviceContext(
            adapterLuid: adapterLuid,
            deviceApi: (warp
                ? new WarpDeviceApi()
                : new DirectXNativeDeviceApi()),
            minimumFeatureLevel: DirectXFeatureLevel.Level110
        );

        try {
            _ = context.Device;
        } catch (GpuDeviceUnavailableException exception) {
            context.Dispose();
            Assert.Skip(reason: $"no Direct3D 12 device on the writer's adapter: {exception.Message}");
        }

        return context;
    }
    private static nint EmptyCommandBuffer(VulkanDeviceCommands commands, nint commandPool) {
        var allocateInfo = new VkCommandBufferAllocateInfo {
            CommandBufferCount = 1,
            CommandPool = commandPool,
            SType = 40,
        };
        nint commandBuffer;

        commands.AllocateCommandBuffers(commands.Handle, in allocateInfo, ((nint)(&commandBuffer))).ThrowIfFailed(operation: "vkAllocateCommandBuffers");

        var beginInfo = new VkCommandBufferBeginInfo { SType = 42 };

        commands.BeginCommandBuffer(commandBuffer, in beginInfo).ThrowIfFailed(operation: "vkBeginCommandBuffer");
        commands.EndCommandBuffer(commandBuffer).ThrowIfFailed(operation: "vkEndCommandBuffer");

        return commandBuffer;
    }
    // The physical device on the writer's adapter with a graphics queue family, which stands in for the present family.
    private static VkPhysicalDevice GraphicsDeviceOn(long adapterLuid, VulkanInstance instance, VulkanNativePhysicalDeviceApi physicalDeviceApi) {
        foreach (var handle in physicalDeviceApi.EnumeratePhysicalDevices(instance: instance.Commands)) {
            if (physicalDeviceApi.GetDeviceLuid(
                instance: instance.Commands,
                physicalDeviceHandle: handle
            ) != adapterLuid) {
                continue;
            }

            var graphics = physicalDeviceApi.GetQueueFamilies(
                instance: instance.Commands,
                physicalDeviceHandle: handle
            ).FirstOrDefault(predicate: static family => ((0U != family.QueueCount) && (0 != (family.Flags & VkQueueFlags.Graphics))));

            if (0U == graphics.QueueCount) {
                continue;
            }

            return new VkPhysicalDevice(
                deviceType: physicalDeviceApi.GetPhysicalDeviceType(
                    instance: instance.Commands,
                    physicalDeviceHandle: handle
                ),
                handle: handle,
                queueFamilySelection: new VulkanQueueFamilySelection(
                    graphicsFamilyIndex: graphics.Index,
                    presentFamilyIndex: graphics.Index
                )
            );
        }

        Assert.Skip(reason: "no Vulkan device with a graphics queue family is on the Direct3D 11 writer's adapter");

        return default;
    }
    // Each pixel's red and green are its column and row, blue their exclusive or, alpha opaque.
    private static byte[] Pattern() {
        var pixels = new byte[((Extent * Extent) * 4)];

        for (var y = 0; (y < Extent); y++) {
            for (var x = 0; (x < Extent); x++) {
                var offset = (((y * Extent) + x) * 4);

                pixels[offset] = ((byte)x);
                pixels[(offset + 1)] = ((byte)y);
                pixels[(offset + 2)] = ((byte)(x ^ y));
                pixels[(offset + 3)] = 255;
            }
        }

        return pixels;
    }
    private static T Skipped<T>(string reason) {
        Assert.Skip(reason: reason);

        return default!;
    }

    private sealed class HeapAllocator : Puck.Abstractions.Memory.IAllocator {
        public void* Allocate(nuint size, nuint alignment = 0) => NativeMemory.Alloc(byteCount: Math.Max(
            val1: size,
            val2: 1
        ));
        public void Free(void* ptr) => NativeMemory.Free(ptr: ptr);
        public void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0) => NativeMemory.Realloc(
            byteCount: Math.Max(
                val1: newSize,
                val2: 1
            ),
            ptr: ptr
        );
    }
}
