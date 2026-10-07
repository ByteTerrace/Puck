using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Exercises the real submission fence and fault ABI over a CPU recording driver.</summary>
public sealed unsafe class VulkanDeviceFaultLawTests {
    [ThreadStatic] private static List<string>? Calls;
    [ThreadStatic] private static int Failure;

    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    [Theory]
    public void LostSubmissionRetainsDriverDetailsWithoutQueryingHealthyWork(bool wait, int failure) {
        Assert.Equal(actual: sizeof(VkDeviceFaultCountsExt), expected: 32);
        Assert.Equal(actual: sizeof(VkDeviceFaultAddressInfoExt), expected: 24);
        Assert.Equal(actual: sizeof(VkDeviceFaultVendorInfoExt), expected: 272);
        Assert.Equal(actual: sizeof(VkDeviceFaultInfoExt), expected: 296);
        Assert.Equal(((nint)272), Marshal.OffsetOf<VkDeviceFaultInfoExt>(fieldName: nameof(VkDeviceFaultInfoExt.PAddressInfos)));
        Calls = [];
        Failure = failure;
        var device = new VulkanDeviceCommands(17, Procedures(), deviceFaultEnabled: (failure != 4));

        Assert.Null(@object: device.Fault.Report);
        using var logical = new VulkanLogicalDevice(device, default, new VkQueue(familyIndex: 0, handle: 31), default, new LogicalApi());
        var sync = new SynchronizationApi();
        var queue = new VulkanGpuQueueSubmitter(new Context(logical: logical), new VulkanQueueSubmitter(), sync);
        using var fence = queue.CreateSubmissionFence();

        queue.Submit(commandBufferHandles: [42], fence: fence);
        Assert.True(condition: fence.IsSignaled);
        Assert.Empty(collection: Calls);
        sync.Lost = true;
        var error = Assert.Throws<DeviceLostException>(testCode: () => { if (wait) { fence.Wait(); } else { _ = fence.IsSignaled; } });

        Assert.Equal(((long)VkResult.ErrorDeviceLost), error.ReasonCode);
        Assert.Contains((wait ? "vkWaitForFences" : "vkGetFenceStatus"), error.Message);
        Assert.NotNull(@object: device.Fault.Report);
        Assert.Contains(device.Fault.Report, error.Message);
        if (failure == 4) {
            Assert.Contains("VK_EXT_device_fault unavailable", error.Message);
            Assert.Empty(collection: Calls);
        } else if (failure is 1 or 2) {
            Assert.Contains($"vkGetDeviceFaultInfoEXT {((failure == 1) ? "counts" : "details")} failed: ErrorUnknown", error.Message);
        } else {
            Assert.Contains("GPU fault: café", error.Message);
            Assert.Contains(((failure == 3) ? "query: Incomplete" : "query: Success"), error.Message);
            Assert.Contains("read invalid (page fault) (type 1), address=0x0000000000001000, precision=0x1000", error.Message);
            Assert.Contains("write invalid (page fault) (type 2)", error.Message);
            Assert.Contains("execute invalid (page fault) (type 3)", error.Message);
            Assert.Contains("instruction pointer fault (type 6)", error.Message);
            Assert.Contains("vendor[0]: driver diagnosis, code=0x00000000000000ab, data=0x00000000000000cd", error.Message);
            Assert.Equal(actual: Calls, expected: ["counts:17:1000341001", "details:17:1000341002:4:1:0"]);
        }
        var calls = Calls.ToArray();
        var repeated = Assert.Throws<DeviceLostException>(testCode: () => fence.Wait());

        Assert.Contains(device.Fault.ReadAfterDeviceLoss(), repeated.Message);
        Assert.Equal(actual: Calls, expected: calls);
    }

    private static VulkanProcResolver Procedures() => new(getDeviceProcAddr: &Resolve, getInstanceProcAddr: &Resolve);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Resolve(nint handle, byte* name) {
        var text = Marshal.PtrToStringUTF8(ptr: ((nint)name));

        return text switch {
            "vkGetDeviceFaultInfoEXT" => ((nint)((delegate* unmanaged[Cdecl]<nint, VkDeviceFaultCountsExt*, VkDeviceFaultInfoExt*, VkResult>)&Query)),
            "vkQueueSubmit" => ((nint)((delegate* unmanaged[Cdecl]<nint, uint, nint, nint, VkResult>)&Submit)),
            _ => ((nint)((delegate* unmanaged[Cdecl]<void>)&Trap)),
        };
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Trap() => Environment.FailFast(message: "The fault law reached an unwired native entry point.");
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static VkResult Submit(nint queue, uint count, nint submits, nint fence) => VkResult.Success;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static VkResult Query(nint device, VkDeviceFaultCountsExt* counts, VkDeviceFaultInfoExt* info) {
        if (info == null) {
            Calls!.Add(item: $"counts:{device}:{counts->SType}");
            counts->AddressInfoCount = 4;
            counts->VendorInfoCount = 1;
            return ((Failure == 1) ? VkResult.ErrorUnknown : VkResult.Success);
        }
        Calls!.Add(item: $"details:{device}:{info->SType}:{counts->AddressInfoCount}:{counts->VendorInfoCount}:{counts->VendorBinarySize}");
        if (Failure == 2) { return VkResult.ErrorUnknown; }
        Encoding.UTF8.GetBytes(s: "GPU fault: café\0").CopyTo(destination: new Span<byte>(length: 256, pointer: info->Description));
        for (var index = 0; (index < 4); index++) {
            info->PAddressInfos[index] = new() { AddressPrecision = 0x1000, AddressType = ((index == 3) ? 6u : (uint)(index + 1)), ReportedAddress = (0x1000u + ((uint)index)) };
        }
        Encoding.UTF8.GetBytes(s: "driver diagnosis\0").CopyTo(destination: new Span<byte>(info->PVendorInfos[0].Description, 256));
        info->PVendorInfos[0].VendorFaultCode = 0xab;
        info->PVendorInfos[0].VendorFaultData = 0xcd;
        return ((Failure == 3) ? VkResult.Incomplete : VkResult.Success);
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
        public VkResult WaitIdle(VulkanDeviceCommands device) => throw new NotSupportedException();
    }
    private sealed class SynchronizationApi : IVulkanFrameSynchronizationApi {
        public bool Lost;

        public VkResult CreateFence(VulkanFrameSynchronizationCreateRequest request, out nint fenceHandle) { fenceHandle = 23; return VkResult.Success; }
        public VkResult CreateSemaphore(VulkanFrameSynchronizationCreateRequest request, out nint semaphoreHandle) => throw new NotSupportedException();
        public void DestroyFence(VulkanDeviceCommands device, nint fenceHandle) { }
        public void DestroySemaphore(VulkanDeviceCommands device, nint semaphoreHandle) => throw new NotSupportedException();
        public VkResult GetFenceStatus(VulkanDeviceCommands device, nint fenceHandle) => (Lost ? VkResult.ErrorDeviceLost : VkResult.Success);
        public VkResult ResetFence(VulkanDeviceCommands device, nint fenceHandle) => VkResult.Success;
        public VkResult WaitForFence(VulkanDeviceCommands device, nint fenceHandle, ulong timeout) => (Lost ? VkResult.ErrorDeviceLost : VkResult.Success);
    }
}
