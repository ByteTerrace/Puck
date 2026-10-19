using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>
/// Laws over a device's submission batch (<see cref="VulkanSubmissionBatch"/>) on a CPU recording driver: an open batch
/// defers submissions and flushes them in one <c>vkQueueSubmit</c>, a submit info each, its last fenced submission's fence
/// signaling them all; a wait on a deferred fence, any other submission to the queue and the device's wait for idle flush
/// it first.
/// </summary>
public sealed unsafe class VulkanSubmissionBatchLawTests {
    // The fences the law creates take these handles, in creation order: first, last, third.
    private const nint First = 100;
    private const nint Last = 101;

    [ThreadStatic] private static List<string>? Calls;

    [Fact]
    public void AnOpenBatchReachesTheQueueInOneSubmitWhoseLastFenceSignalsEveryFenceInIt() {
        Calls = [];
        var device = new VulkanDeviceCommands(17, Procedures(), deviceFaultEnabled: false);
        using var logical = new VulkanLogicalDevice(device, default, new VkQueue(familyIndex: 0, handle: 31), default, new LogicalApi());
        var sync = new SynchronizationApi();
        var low = new VulkanQueueSubmitter();
        var queue = new VulkanGpuQueueSubmitter(new Context(logical: logical), low, sync);
        using var first = queue.CreateSubmissionFence();
        using var last = queue.CreateSubmissionFence();
        using var third = queue.CreateSubmissionFence();

        // Nothing reaches the queue while the batch is open, and a deferred fence is not signaled.
        queue.BeginBatch();
        queue.Submit(commandBufferHandles: [1], fence: first);
        queue.Submit(commandBufferHandles: [2, 3]);
        queue.Submit(commandBufferHandles: [4], fence: last);
        queue.Submit(commandBufferHandles: []);
        Assert.Empty(collection: Calls);
        Assert.False(condition: first.IsSignaled);
        Assert.Throws<InvalidOperationException>(testCode: () => queue.Submit(commandBufferHandles: [5], fence: first));

        // One submit, three submit infos in order, armed with the last fenced submission's fence.
        queue.FlushBatch();
        queue.FlushBatch();
        Assert.Equal(expected: [$"submit fence={Last} infos=1:1,2:2,1:4"], actual: Calls);

        // The earlier fence answers from the last one's VkFence and waits on it without resetting it.
        Calls.Clear();
        Assert.False(condition: first.IsSignaled);
        sync.Signal(handle: Last);
        Assert.True(condition: first.IsSignaled);
        first.Wait();
        Assert.Equal(expected: [$"wait {Last}"], actual: Calls);
        last.Wait();
        Assert.Equal(expected: [$"wait {Last}", $"wait {Last}", $"reset {Last}"], actual: Calls);

        // Once the owner has drained and reset its fence, a fence answering from it is signaled without asking the driver.
        Calls.Clear();
        queue.BeginBatch();
        queue.Submit(commandBufferHandles: [6], fence: third);
        queue.Submit(commandBufferHandles: [7], fence: last);
        queue.FlushBatch();
        sync.Signal(handle: Last);
        last.Wait();
        Calls.Clear();
        Assert.True(condition: third.IsSignaled);
        third.Wait();
        Assert.Empty(collection: Calls);

        // A wait on a deferred fence flushes the batch first.
        queue.BeginBatch();
        queue.Submit(commandBufferHandles: [8], fence: first);
        sync.Signal(handle: First);
        first.Wait();
        Assert.Equal(expected: [$"submit fence={First} infos=1:8", $"wait {First}", $"reset {First}"], actual: Calls);

        // Any other submission to the queue, and the device's wait for idle, reach the queue behind the deferred work.
        Calls.Clear();
        queue.BeginBatch();
        queue.Submit(commandBufferHandles: [9]);
        low.Submit(commandBufferHandles: [10], device: device, graphicsQueue: logical.GraphicsQueue);
        queue.BeginBatch();
        queue.Submit(commandBufferHandles: [11]);
        queue.SubmitAndWait(commandBufferHandles: [12]);
        queue.BeginBatch();
        queue.Submit(commandBufferHandles: [13]);
        logical.WaitIdle();
        queue.BeginBatch();
        queue.Submit(commandBufferHandles: [14]);
        low.Signal(commandBufferHandles: [], device: device, graphicsQueue: logical.GraphicsQueue, semaphore: 55, value: 1UL);
        Assert.Equal(expected: ["submit fence=0 infos=1:9", "submit fence=0 infos=1:10", "submit fence=0 infos=1:11", "submit fence=0 infos=1:12", "queue idle", "submit fence=0 infos=1:13", "device idle", "submit fence=0 infos=1:14", "submit fence=0 infos=0:0"], actual: Calls);
    }

    private static VulkanProcResolver Procedures() => new(getDeviceProcAddr: &Resolve, getInstanceProcAddr: &Resolve);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Resolve(nint handle, byte* name) => (Marshal.PtrToStringUTF8(ptr: ((nint)name)) switch {
        "vkQueueSubmit" => ((nint)((delegate* unmanaged[Cdecl]<nint, uint, VkSubmitInfo*, nint, VkResult>)&Submit)),
        "vkQueueWaitIdle" => ((nint)((delegate* unmanaged[Cdecl]<nint, VkResult>)&QueueIdle)),
        _ => ((nint)((delegate* unmanaged[Cdecl]<void>)&Trap)),
    });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Trap() => Environment.FailFast(message: "The batch law reached an unwired native entry point.");
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static VkResult Submit(nint queue, uint count, VkSubmitInfo* submits, nint fence) {
        var infos = new List<string>();

        for (var index = 0; (index < count); index++) {
            infos.Add(item: $"{submits[index].CommandBufferCount}:{((submits[index].CommandBufferCount == 0) ? 0 : ((nint*)submits[index].PCommandBuffers)[0])}");
        }
        Calls!.Add(item: $"submit fence={fence} infos={string.Join(separator: ",", values: infos)}");

        return VkResult.Success;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static VkResult QueueIdle(nint queue) {
        Calls!.Add(item: "queue idle");

        return VkResult.Success;
    }

    private sealed class Context(VulkanLogicalDevice logical) : IVulkanDeviceContext {
        public VulkanInstance Instance => throw new NotSupportedException();
        public VulkanLogicalDevice LogicalDevice => logical;
        public VkPhysicalDevice PhysicalDevice => throw new NotSupportedException();
        public VulkanSurface Surface => throw new NotSupportedException();
    }
    private sealed class LogicalApi : IVulkanLogicalDeviceApi {
        public VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out VulkanDeviceCommands? device) => throw new NotSupportedException();
        public void DestroyDevice(VulkanDeviceCommands device) { }
        public nint GetDeviceQueue(VulkanDeviceCommands device, uint queueFamilyIndex, uint queueIndex) => throw new NotSupportedException();
        public VkResult WaitIdle(VulkanDeviceCommands device) {
            Calls!.Add(item: "device idle");

            return VkResult.Success;
        }
    }
    // Fences numbered in creation order, signaled only when the law says so, and unsignaled again by a reset.
    private sealed class SynchronizationApi : IVulkanFrameSynchronizationApi {
        private readonly HashSet<nint> m_signaled = [];
        private nint m_next = First;

        public void Signal(nint handle) => m_signaled.Add(item: handle);

        public VkResult CreateFence(VulkanFrameSynchronizationCreateRequest request, out nint fenceHandle) {
            fenceHandle = m_next++;

            return VkResult.Success;
        }
        public VkResult CreateSemaphore(VulkanFrameSynchronizationCreateRequest request, out nint semaphoreHandle) => throw new NotSupportedException();
        public void DestroyFence(VulkanDeviceCommands device, nint fenceHandle) { }
        public void DestroySemaphore(VulkanDeviceCommands device, nint semaphoreHandle) => throw new NotSupportedException();
        public VkResult GetFenceStatus(VulkanDeviceCommands device, nint fenceHandle) => (m_signaled.Contains(item: fenceHandle) ? VkResult.Success : VkResult.NotReady);
        public VkResult ResetFence(VulkanDeviceCommands device, nint fenceHandle) {
            Calls!.Add(item: $"reset {fenceHandle}");
            _ = m_signaled.Remove(item: fenceHandle);

            return VkResult.Success;
        }
        public VkResult WaitForFence(VulkanDeviceCommands device, nint fenceHandle, ulong timeout) {
            Calls!.Add(item: $"wait {fenceHandle}");

            return VkResult.Success;
        }
    }
}
