using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Puck.Abstractions.Presentation;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>
/// Laws over the presenter's closed-loop present timing (<see cref="VulkanFramePresenter"/>) on a CPU driver whose
/// presents display only when the law says so: the timing sample after each present only polls, the queue wait gives a
/// present a bounded time, and after a run of presents that never display the queue wait only polls too, so the pacer
/// paces the pump, until a poll sees a present displayed and the bounded wait resumes. A hard error stops present-wait
/// for the device: later presents carry no id and nothing waits.
/// </summary>
public sealed unsafe class VulkanPresentWaitLawTests {
    private const ulong Bound = 50_000_000UL;

    [Fact]
    public void APresentThatNeverDisplaysCostsTheQueueWaitABoundedRunAndThenOnlyPolls() {
        var device = new VulkanDeviceCommands(17, Procedures(), deviceFaultEnabled: false);
        using var logical = new VulkanLogicalDevice(device, default, new VkQueue(familyIndex: 0, handle: 31), new VkQueue(familyIndex: 0, handle: 32), new LogicalApi());
        var sync = new SynchronizationApi();
        var api = new PresentationApi();
        var presenter = new VulkanFramePresenter(framePresentationApi: api, frameSynchronizationApi: sync);
        var resources = new VulkanCommandResources(device, [41], 40, new CommandResourcesApi());
        var frame = new VulkanFrameSynchronization(device, 50, 51, [52], sync);
        var swapchain = new VulkanSwapchain(60, device, default(DisplayOutput), 8, 8, 2, new SwapchainApi());

        void Frame() {
            api.Queue();
            _ = presenter.TryWaitForPresentQueue(logicalDevice: logical, swapchain: swapchain);
            Assert.True(condition: presenter.Present(resources, frame, logical, _ => { }, swapchain).Result == VulkanFramePresentationResult.Presented);
        }

        // Presents 1 and 2 have nothing two back to wait for; from present 3 on the sample polls present N−2 and the queue
        // wait before each frame gives it the bound. Four bounded waits in a row that time out make the queue wait poll.
        for (var index = 0; index < 8; index++) { Frame(); }

        Assert.Equal(expected: [1UL, 2UL, 3UL, 4UL, 5UL, 6UL, 7UL, 8UL], actual: api.Presented);
        Assert.Equal(
            expected: ["queue 1 50", "sample 1 0", "queue 2 50", "sample 2 0", "queue 3 50", "sample 3 0", "queue 4 50", "sample 4 0", "queue 5 0", "sample 5 0", "queue 6 0", "sample 6 0"],
            actual: api.Waits
        );
        Assert.False(condition: presenter.TryGetPresentTiming(out _, out _));

        // Once a poll sees a present displayed the swapchain paces again, and the next queue wait gives the bound.
        api.Waits.Clear();
        api.Displayed = 7UL;
        api.Queue();
        Assert.True(condition: presenter.TryWaitForPresentQueue(logicalDevice: logical, swapchain: swapchain));
        Assert.False(condition: presenter.TryWaitForPresentQueue(logicalDevice: logical, swapchain: swapchain));
        Assert.True(condition: presenter.Present(resources, frame, logical, _ => { }, swapchain).Result == VulkanFramePresentationResult.Presented);
        api.Queue();
        Assert.False(condition: presenter.TryWaitForPresentQueue(logicalDevice: logical, swapchain: swapchain));
        Assert.Equal(expected: ["queue 7 0", "sample 7 0", "queue 8 50"], actual: api.Waits);
        Assert.True(condition: presenter.TryGetPresentTiming(out var count, out _));
        Assert.Equal(expected: 1U, actual: count);

        // A hard error stops present-wait for the device: the next present carries no id and nothing waits again.
        api.Waits.Clear();
        api.Hard = true;
        Assert.True(condition: presenter.Present(resources, frame, logical, _ => { }, swapchain).Result == VulkanFramePresentationResult.Presented);
        Frame();
        Assert.Equal(expected: ["sample 8 0"], actual: api.Waits);
        Assert.Equal(expected: 0UL, actual: api.Presented[^1]);
    }

    private static VulkanProcResolver Procedures() => new(getDeviceProcAddr: &Resolve, getInstanceProcAddr: &Resolve);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Resolve(nint handle, byte* name) => ((nint)((delegate* unmanaged[Cdecl]<void>)&Trap));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Trap() => Environment.FailFast(message: "The present-wait law reached an unwired native entry point.");

    // Presents display up to Displayed; a wait on a later present times out, or fails hard once the law says so. A wait
    // after a present is the timing sample, and one after Queue is the queue wait.
    private sealed class PresentationApi : IVulkanFramePresentationApi {
        private bool m_presented;

        public void Queue() => m_presented = false;
        public ulong Displayed { get; set; }
        public bool Hard { get; set; }
        public List<ulong> Presented { get; } = [];
        public List<string> Waits { get; } = [];

        public VkResult AcquireNextImage(VulkanFrameAcquireRequest request, out uint imageIndex) {
            imageIndex = 0U;

            return VkResult.Success;
        }
        public VkResult Present(VulkanPresentRequest request) {
            Presented.Add(item: request.PresentId);
            m_presented = true;

            return VkResult.Success;
        }
        public VkResult Submit(VulkanFrameSubmitRequest request) => VkResult.Success;
        public VkResult Submit(VulkanDeviceCommands device, nint graphicsQueueHandle, nint commandBufferHandle, nint fenceHandle) => throw new NotSupportedException();
        public bool SupportsPresentWait(VulkanDeviceCommands device) => true;
        public VkResult WaitForPresent(VulkanDeviceCommands device, nint swapchainHandle, ulong presentId, ulong timeoutNanoseconds) {
            Waits.Add(item: $"{(m_presented ? "sample" : "queue")} {presentId} {timeoutNanoseconds / 1_000_000UL}");
            Assert.True(condition: (timeoutNanoseconds is 0UL or Bound));

            if (Hard) {
                return VkResult.ErrorUnknown;
            }

            return ((presentId <= Displayed) ? VkResult.Success : VkResult.Timeout);
        }
    }
    private sealed class SynchronizationApi : IVulkanFrameSynchronizationApi {
        public VkResult CreateFence(VulkanFrameSynchronizationCreateRequest request, out nint fenceHandle) => throw new NotSupportedException();
        public VkResult CreateSemaphore(VulkanFrameSynchronizationCreateRequest request, out nint semaphoreHandle) => throw new NotSupportedException();
        public void DestroyFence(VulkanDeviceCommands device, nint fenceHandle) { }
        public void DestroySemaphore(VulkanDeviceCommands device, nint semaphoreHandle) { }
        public VkResult GetFenceStatus(VulkanDeviceCommands device, nint fenceHandle) => VkResult.Success;
        public VkResult ResetFence(VulkanDeviceCommands device, nint fenceHandle) => VkResult.Success;
        public VkResult WaitForFence(VulkanDeviceCommands device, nint fenceHandle, ulong timeout) => VkResult.Success;
    }
    private sealed class LogicalApi : IVulkanLogicalDeviceApi {
        public VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out VulkanDeviceCommands? device) => throw new NotSupportedException();
        public void DestroyDevice(VulkanDeviceCommands device) { }
        public nint GetDeviceQueue(VulkanDeviceCommands device, uint queueFamilyIndex, uint queueIndex) => throw new NotSupportedException();
        public VkResult WaitIdle(VulkanDeviceCommands device) => VkResult.Success;
    }
    private sealed class CommandResourcesApi : IVulkanCommandResourcesApi {
        public VkResult AllocateCommandBuffers(VulkanCommandBufferAllocateRequest request, nint buffer, uint commandBufferCount) => throw new NotSupportedException();
        public VkResult CreateCommandPool(VulkanCommandPoolCreateRequest request, out nint commandPoolHandle) => throw new NotSupportedException();
        public void DestroyCommandPool(VulkanDeviceCommands device, nint commandPoolHandle) { }
    }
    private sealed class SwapchainApi : IVulkanSwapchainApi {
        public VkResult CreateSwapchain(VulkanSwapchainCreateRequest request, out nint swapchainHandle) => throw new NotSupportedException();
        public void DestroySwapchain(VulkanDeviceCommands device, nint swapchainHandle) { }
    }
}
