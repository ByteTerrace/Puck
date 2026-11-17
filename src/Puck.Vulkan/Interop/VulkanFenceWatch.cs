using Puck.Abstractions.Waiting;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Turns a <c>VkFence</c>'s completion into a host event. Vulkan has no call that sets an event when a fence signals
/// (Direct3D 12's <c>ID3D12Fence::SetEventOnCompletion</c>), so one thread for the process waits each watched fence in
/// the driver (<c>vkWaitForFences</c>, a host wait) and sets the event its submission asked for
/// (<see cref="IGpuSubmissionFence.SignalOnCompletion"/>). One queue retires its submissions in order, so the watches
/// are waited in the order they were asked, one at a time.
/// <para>Invariant: no fence is reset or destroyed while the watcher is inside <c>vkWaitForFences</c> on it, which
/// Vulkan requires of <c>vkResetFences</c> and <c>vkDestroyFence</c>. <see cref="Withdraw"/> is the one door: a fence
/// calls it before either, and it returns only once the watcher has left the call for that fence. The watcher waits in
/// slices of <see cref="SliceNanoseconds"/>, so a withdrawn fence that never signals is given up within one.</para>
/// </summary>
internal static class VulkanFenceWatch {
    /// <summary>The longest one <c>vkWaitForFences</c> of the watcher blocks before it reads whether its fence was
    /// withdrawn: a tenth of a second.</summary>
    internal const ulong SliceNanoseconds = 100_000_000UL;

    private static readonly WaitReason IdleReason = new(Subsystem: "vulkan", WaitsFor: "a fence to watch");
    private static readonly object Gate = new();
    private static readonly Queue<Entry> Pending = new();

    // The watch the watcher is inside vkWaitForFences for, or null between watches. Under Gate.
    private static Entry? Current;
    private static Thread? Watcher;

    /// <summary>Sets <paramref name="signal"/> once <paramref name="fenceHandle"/> signals, or the device reports its
    /// loss, or the fence is withdrawn. Never blocks.</summary>
    /// <param name="api">The frame synchronization calls.</param>
    /// <param name="device">The device the fence belongs to.</param>
    /// <param name="fenceHandle">The armed <c>VkFence</c>.</param>
    /// <param name="signal">The event to set.</param>
    internal static void Watch(IVulkanFrameSynchronizationApi api, VulkanDeviceCommands device, nint fenceHandle, EventWaitHandle signal) {
        lock (Gate) {
            Pending.Enqueue(item: new Entry(Api: api, Device: device, Fence: fenceHandle, Signal: signal));
            if (Watcher is null) {
                Watcher = new Thread(start: Run) { IsBackground = true, Name = "Puck Vulkan fence watch" };
                Watcher.Start();
            }
            Monitor.PulseAll(obj: Gate);
        }
    }
    /// <summary>Ends every watch of <paramref name="fenceHandle"/> and returns once the watcher is outside
    /// <c>vkWaitForFences</c> for it, so the caller may reset or destroy the fence. A fence that has signaled, the usual
    /// case, releases the watcher at once.</summary>
    /// <param name="fenceHandle">The <c>VkFence</c> about to be reset or destroyed.</param>
    internal static void Withdraw(nint fenceHandle) {
        lock (Gate) {
            foreach (var entry in Pending) {
                if (entry.Fence == fenceHandle) { entry.Withdrawn = true; }
            }
            while (Current is { } current && (current.Fence == fenceHandle)) {
                current.Withdrawn = true;
                // The watcher pulses as it leaves each watch, within one slice of the withdrawal at the latest.
                _ = Monitor.Wait(obj: Gate, millisecondsTimeout: ((int)(SliceNanoseconds / 1_000_000UL)));
            }
        }
    }

    private static void Run() {
        var consumer = HostWaits.ClaimConsumer(reason: IdleReason);

        while (true) {
            Entry entry;

            lock (Gate) {
                Current = null;
                Monitor.PulseAll(obj: Gate);
                while (!Pending.TryDequeue(result: out entry!)) {
                    HostWaits.IdleOnCondition(consumer: consumer, monitor: Gate);
                }
                if (entry.Withdrawn) { continue; }
                Current = entry;
            }

            VkResult result;

            do {
                result = entry.Api.WaitForFence(device: entry.Device, fenceHandle: entry.Fence, timeout: SliceNanoseconds);
            } while ((result == VkResult.Timeout) && !Volatile.Read(location: ref entry.Withdrawn));

            // Set on completion, on a reported loss and on withdrawal alike: whoever wakes reads the fence.
            _ = entry.Signal.Set();
        }
    }

    private sealed record Entry(IVulkanFrameSynchronizationApi Api, VulkanDeviceCommands Device, nint Fence, EventWaitHandle Signal) {
        public bool Withdrawn;
    }
}
