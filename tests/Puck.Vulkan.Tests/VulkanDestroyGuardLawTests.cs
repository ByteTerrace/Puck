using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Pins, without a device, that every handle kind the backend destroys reaches one zero-handle guard:
/// <c>VulkanDeviceCommands.Destroy</c> for device-level objects and <see cref="VulkanInstanceCommands.Destroy"/> for
/// instance-level ones. Each kind is driven through the API its owners call, over a command table whose destroy entry
/// points record instead of reaching a driver: a zero handle makes no call, and a non-zero one makes exactly one call
/// through the kind's own entry point.</summary>
/// <remarks>The recording tables are built through their constructors over a procedure resolver that stands in for the
/// driver, so each table is resolved exactly as a real one is, and the names a table asks that resolver for are the
/// destroy entry points every kind must cover.</remarks>
public sealed unsafe class VulkanDestroyGuardLawTests {
    private const nint DeviceHandle = 0x0D00;
    private const nint FirstHandle = 0x1000;
    private const nint InstanceHandle = 0x0100;
    private const nint SecondHandle = 0x2000;

    [ThreadStatic]
    private static List<(nint Parent, nint Handle, nint Allocator)>? RecordedCalls;
    [ThreadStatic]
    private static List<(nint Parent, nint Handle, nint Allocator)>? RecordedStrayCalls;
    [ThreadStatic]
    private static List<string>? ResolvedNames;
    [ThreadStatic]
    private static (IReadOnlyList<string> DestroyEntryPoints, IReadOnlyCollection<string> Wired)? Wiring;

    private static readonly string[] DeviceDestroyEntryPoints = [
        nameof(VulkanDeviceCommands.DestroyBuffer),
        nameof(VulkanDeviceCommands.DestroyCommandPool),
        nameof(VulkanDeviceCommands.DestroyDescriptorPool),
        nameof(VulkanDeviceCommands.DestroyDescriptorSetLayout),
        nameof(VulkanDeviceCommands.DestroyFence),
        nameof(VulkanDeviceCommands.DestroyFramebuffer),
        nameof(VulkanDeviceCommands.DestroyImage),
        nameof(VulkanDeviceCommands.DestroyImageView),
        nameof(VulkanDeviceCommands.DestroyPipeline),
        nameof(VulkanDeviceCommands.DestroyPipelineCache),
        nameof(VulkanDeviceCommands.DestroyPipelineLayout),
        nameof(VulkanDeviceCommands.DestroyRenderPass),
        nameof(VulkanDeviceCommands.DestroySampler),
        nameof(VulkanDeviceCommands.DestroySemaphore),
        nameof(VulkanDeviceCommands.DestroyShaderModule),
        nameof(VulkanDeviceCommands.DestroySwapchainKhr),
        nameof(VulkanDeviceCommands.FreeMemory),
    ];
    private static readonly string[] InstanceDestroyEntryPoints = [
        nameof(VulkanInstanceCommands.DestroyDebugUtilsMessengerExt),
        nameof(VulkanInstanceCommands.DestroySurfaceKhr),
    ];
    /// <summary>Every device-level handle kind: the entry points it is released through, in call order, and the
    /// destroy its owners call with the kind's handles.</summary>
    private static readonly Dictionary<string, DeviceKind> DeviceKinds = new() {
        ["buffer and memory"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroyBuffer), nameof(VulkanDeviceCommands.FreeMemory)],
            Release: (device, handles) => new VulkanNativeBufferApi().Destroy(handles: new VulkanBufferHandles(
                Buffer: handles[0],
                Device: device,
                Memory: handles[1]
            ))
        ),
        ["command pool"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroyCommandPool)],
            Release: (device, handles) => new VulkanNativeCommandResourcesApi().DestroyCommandPool(
                commandPoolHandle: handles[0],
                device: device
            )
        ),
        ["compute pipeline"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroyPipeline)],
            Release: (device, handles) => new VulkanNativeComputePipelineApi(allocator: new RefusingAllocator()).DestroyPipeline(
                device: device,
                pipelineHandle: handles[0]
            )
        ),
        ["descriptor pool"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroyDescriptorPool)],
            Release: (device, handles) => new VulkanDescriptorAllocator(descriptorApi: new VulkanNativeDescriptorApi()).DestroyPool(
                device: device,
                poolHandle: handles[0]
            )
        ),
        ["exported or imported image and memory"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroyImage), nameof(VulkanDeviceCommands.FreeMemory)],
            Release: (device, handles) => new VulkanNativeExternalMemoryApi().DestroyImage(
                device: device,
                imageHandle: handles[0],
                memoryHandle: handles[1]
            )
        ),
        ["fence"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroyFence)],
            Release: (device, handles) => new VulkanNativeFrameSynchronizationApi().DestroyFence(
                device: device,
                fenceHandle: handles[0]
            )
        ),
        ["framebuffer"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroyFramebuffer)],
            Release: (device, handles) => new VulkanNativeFramebufferSetApi(allocator: new RefusingAllocator()).DestroyFramebuffer(
                device: device,
                framebufferHandle: handles[0]
            )
        ),
        ["graphics pipeline"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroyPipeline)],
            Release: (device, handles) => new VulkanNativeGraphicsPipelineApi(allocator: new RefusingAllocator()).DestroyPipeline(
                device: device,
                pipelineHandle: handles[0]
            )
        ),
        ["image view"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroyImageView)],
            Release: (device, handles) => new VulkanNativeFramebufferSetApi(allocator: new RefusingAllocator()).DestroyImageView(
                device: device,
                imageViewHandle: handles[0]
            )
        ),
        ["offscreen image and memory"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroyImage), nameof(VulkanDeviceCommands.FreeMemory)],
            Release: (device, handles) => new VulkanNativeOffscreenImageApi().DestroyColorImage(
                device: device,
                imageHandle: handles[0],
                memoryHandle: handles[1]
            )
        ),
        ["pipeline cache"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroyPipelineCache)],
            Release: (device, handles) => device.Destroy(
                destroy: device.DestroyPipelineCache,
                handle: handles[0]
            )
        ),
        ["pipeline layout and descriptor set layout"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroyPipelineLayout), nameof(VulkanDeviceCommands.DestroyDescriptorSetLayout)],
            Release: (device, handles) => VulkanPipelineLayouts.Destroy(
                descriptorSetLayoutHandle: handles[1],
                device: device,
                pipelineLayoutHandle: handles[0]
            )
        ),
        ["render pass"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroyRenderPass)],
            Release: (device, handles) => new VulkanNativeRenderPassApi(allocator: new RefusingAllocator()).DestroyRenderPass(
                device: device,
                renderPassHandle: handles[0]
            )
        ),
        ["sampler"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroySampler)],
            Release: (device, handles) => new VulkanDescriptorAllocator(descriptorApi: new VulkanNativeDescriptorApi()).DestroySampler(
                device: device,
                samplerHandle: handles[0]
            )
        ),
        ["semaphore"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroySemaphore)],
            Release: (device, handles) => new VulkanNativeFrameSynchronizationApi().DestroySemaphore(
                device: device,
                semaphoreHandle: handles[0]
            )
        ),
        ["shader module"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroyShaderModule)],
            Release: (device, handles) => new VulkanNativeShaderModuleApi().DestroyShaderModule(
                device: device,
                moduleHandle: handles[0]
            )
        ),
        ["swapchain"] = new(
            EntryPoints: [nameof(VulkanDeviceCommands.DestroySwapchainKhr)],
            Release: (device, handles) => new VulkanNativeSwapchainApi(allocator: new RefusingAllocator()).DestroySwapchain(
                device: device,
                swapchainHandle: handles[0]
            )
        ),
    };
    /// <summary>Every instance-level handle kind, as <see cref="DeviceKinds"/> lists the device-level ones.</summary>
    private static readonly Dictionary<string, InstanceKind> InstanceKinds = new() {
        ["debug messenger"] = new(
            EntryPoint: nameof(VulkanInstanceCommands.DestroyDebugUtilsMessengerExt),
            Release: (instance, handle) => new VulkanNativeInstanceApi(
                allocator: new RefusingAllocator(),
                procedures: new VulkanProcResolver()
            ).DestroyDebugMessenger(
                instance: instance,
                messengerHandle: handle
            )
        ),
        ["surface"] = new(
            EntryPoint: nameof(VulkanInstanceCommands.DestroySurfaceKhr),
            Release: (instance, handle) => new VulkanNativeSurfaceApi().DestroySurface(
                instance: instance,
                surfaceHandle: handle
            )
        ),
    };

    public static TheoryData<string> EveryDeviceKind() => [.. DeviceKinds.Keys];
    public static TheoryData<string> EveryInstanceKind() => [.. InstanceKinds.Keys];
    [MemberData(nameof(EveryDeviceKind))]
    [Theory]
    public void AZeroDeviceHandleReachesTheGuardAndMakesNoNativeCall(string kind) {
        var device = RecordingDevice(wired: DeviceDestroyEntryPoints);
        var entry = DeviceKinds[kind];
        var calls = Record(release: () => entry.Release(
            device,
            new nint[entry.EntryPoints.Length]
        ));

        Assert.Empty(collection: calls.Calls);
        Assert.Empty(collection: calls.Stray);
    }
    [MemberData(nameof(EveryDeviceKind))]
    [Theory]
    public void ALiveDeviceHandleIsDestroyedOnceThroughItsOwnEntryPoint(string kind) {
        var entry = DeviceKinds[kind];
        var device = RecordingDevice(wired: entry.EntryPoints);
        nint[] handles = [.. entry.EntryPoints.Select(selector: (_, index) => (FirstHandle + (index * SecondHandle)))];
        var calls = Record(release: () => entry.Release(
            device,
            handles
        ));

        Assert.Equal(
            actual: calls.Calls,
            expected: [.. handles.Select(selector: handle => (device.Handle, handle, ((nint)0)))]
        );
        Assert.Empty(collection: calls.Stray);
    }
    [MemberData(nameof(EveryInstanceKind))]
    [Theory]
    public void AZeroInstanceHandleReachesTheGuardAndMakesNoNativeCall(string kind) {
        var instance = RecordingInstance(wired: InstanceDestroyEntryPoints);
        var calls = Record(release: () => InstanceKinds[kind].Release(
            instance,
            0
        ));

        Assert.Empty(collection: calls.Calls);
        Assert.Empty(collection: calls.Stray);
    }
    [MemberData(nameof(EveryInstanceKind))]
    [Theory]
    public void ALiveInstanceHandleIsDestroyedOnceThroughItsOwnEntryPoint(string kind) {
        var entry = InstanceKinds[kind];
        var instance = RecordingInstance(wired: [entry.EntryPoint]);
        var calls = Record(release: () => entry.Release(
            instance,
            FirstHandle
        ));

        Assert.Equal(
            actual: calls.Calls,
            expected: [(instance.Handle, FirstHandle, ((nint)0))]
        );
        Assert.Empty(collection: calls.Stray);
    }
    [Fact]
    public void DeviceLocalMemoryIsCountedAtAllocationAndReleasedThroughTheFreeGuard() {
        const nint DeviceLocalMemory = 0x3000;
        const nint HostMemory = 0x4000;

        var memory = new GpuDeviceMemoryWork(backend: "vulkan");
        var device = RecordingDevice(
            memory: memory,
            wired: [nameof(VulkanDeviceCommands.FreeMemory)]
        );
        var properties = new VkPhysicalDeviceMemoryProperties { MemoryTypeCount = 2U };

        properties.MemoryTypePairs[0] = 0x1U;
        properties.MemoryTypePairs[2] = 0x6U;
        device.CountAllocated(
            allocateInfo: new VkMemoryAllocateInfo { AllocationSize = 65536UL, MemoryTypeIndex = 0U },
            memoryHandle: DeviceLocalMemory,
            memoryProperties: in properties
        );
        device.CountAllocated(
            allocateInfo: new VkMemoryAllocateInfo { AllocationSize = 4096UL, MemoryTypeIndex = 1U },
            memoryHandle: HostMemory,
            memoryProperties: in properties
        );

        var calls = Record(release: () => {
            device.Destroy(destroy: device.FreeMemory, handle: HostMemory);
            device.Destroy(destroy: device.FreeMemory, handle: DeviceLocalMemory);
        });

        Assert.Equal(
            actual: calls.Calls,
            expected: [(device.Handle, HostMemory, ((nint)0)), (device.Handle, DeviceLocalMemory, ((nint)0))]
        );
        Assert.Equal(expected: (65536L, 65536L, 65536L), actual: (memory.Read(kind: GpuDeviceMemoryWork.Allocated), memory.Read(kind: GpuDeviceMemoryWork.Released), memory.Read(kind: GpuDeviceMemoryWork.Peak)));
    }
    [Fact]
    public void EveryDestroyEntryPointOfBothTablesIsCoveredByAKind() {
        var deviceCovered = DeviceKinds.Values.SelectMany(selector: kind => kind.EntryPoints).ToHashSet();
        var instanceCovered = InstanceKinds.Values.Select(selector: kind => kind.EntryPoint).ToHashSet();

        Assert.Equal(
            actual: deviceCovered.Order(),
            expected: DeviceDestroyEntryPoints.Order()
        );
        Assert.Equal(
            actual: instanceCovered.Order(),
            expected: InstanceDestroyEntryPoints.Order()
        );
        Assert.Equal(
            actual: ChildReleasesResolvedBy(build: static () => RecordingDevice(wired: [])),
            expected: DeviceDestroyEntryPoints.Order()
        );
        Assert.Equal(
            actual: ChildReleasesResolvedBy(build: static () => RecordingInstance(wired: [])),
            expected: InstanceDestroyEntryPoints.Order()
        );
    }

    // The child-object releases a table resolves while it is built, named as its fields are: every vkDestroy* and
    // vkFreeMemory the table asks its resolver for, except the parent's own vkDestroyDevice and vkDestroyInstance.
    private static IOrderedEnumerable<string> ChildReleasesResolvedBy(Action build) {
        ResolvedNames = [];

        try {
            build();

            return ResolvedNames
                .Where(predicate: static name => ((name.StartsWith(comparisonType: StringComparison.Ordinal, value: "Destroy") || (name == nameof(VulkanDeviceCommands.FreeMemory))) &&
                    (name != nameof(VulkanDeviceCommands.DestroyDevice)) &&
                    (name != nameof(VulkanInstanceCommands.DestroyInstance))))
                .Select(selector: static name => (DeviceDestroyEntryPoints.Concat(second: InstanceDestroyEntryPoints).FirstOrDefault(predicate: entryPoint => string.Equals(
                    a: entryPoint,
                    b: name,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )) ?? name))
                .ToArray()
                .Order();
        } finally {
            ResolvedNames = null;
        }
    }
    private static VulkanProcResolver RecordingProcedures() =>
        new(
            getDeviceProcAddr: &ResolveRecording,
            getInstanceProcAddr: &ResolveRecording
        );
    private static (List<(nint Parent, nint Handle, nint Allocator)> Calls, List<(nint Parent, nint Handle, nint Allocator)> Stray) Record(Action release) {
        RecordedCalls = [];
        RecordedStrayCalls = [];

        try {
            release();
            return (RecordedCalls, RecordedStrayCalls);
        } finally {
            RecordedCalls = null;
            RecordedStrayCalls = null;
        }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RecordCall(nint parent, nint handle, nint allocator) =>
        RecordedCalls!.Add(item: (parent, handle, allocator));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RecordStrayCall(nint parent, nint handle, nint allocator) =>
        RecordedStrayCalls!.Add(item: (parent, handle, allocator));
    private static VulkanDeviceCommands RecordingDevice(IReadOnlyCollection<string> wired, GpuDeviceMemoryWork? memory = null) =>
        RecordingTable(
            build: () => new VulkanDeviceCommands(
                deviceHandle: DeviceHandle,
                memory: memory,
                procedures: RecordingProcedures()
            ),
            destroyEntryPoints: DeviceDestroyEntryPoints,
            wired: wired
        );
    private static VulkanInstanceCommands RecordingInstance(IReadOnlyCollection<string> wired) =>
        RecordingTable(
            build: static () => new VulkanInstanceCommands(
                instanceHandle: InstanceHandle,
                procedures: RecordingProcedures()
            ),
            destroyEntryPoints: InstanceDestroyEntryPoints,
            wired: wired
        );
    // Builds a table over ResolveRecording while the wiring is in force.
    private static TTable RecordingTable<TTable>(Func<TTable> build, IReadOnlyList<string> destroyEntryPoints, IReadOnlyCollection<string> wired) {
        Wiring = (destroyEntryPoints, wired);

        try {
            return build();
        } finally {
            Wiring = null;
        }
    }
    // A vkGet*ProcAddr stand-in. The wired destroy entry points record into the kind's calls and every other destroy
    // entry point records a stray call, so a release through the wrong entry point is a failure; every other entry
    // point resolves to a trap, which no release reaches. A table field is the procedure's name without its vk prefix,
    // with the extension suffix in Pascal case (vkDestroySwapchainKHR is DestroySwapchainKhr).
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint ResolveRecording(nint handle, byte* name) {
        var (destroyEntryPoints, wired) = Wiring!.Value;
        var field = Encoding.UTF8.GetString(bytes: MemoryMarshal.CreateReadOnlySpanFromNullTerminated(value: name))[2..];

        ResolvedNames?.Add(item: field);

        if (destroyEntryPoints.FirstOrDefault(predicate: entryPoint => string.Equals(
            a: entryPoint,
            b: field,
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) is not { } entryPoint) {
            return ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)&Trap));
        }

        return (wired.Contains(value: entryPoint)
            ? ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&RecordCall))
            : ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&RecordStrayCall))
        );
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Trap(nint first, nint second, nint third, nint fourth) {
        RecordedStrayCalls!.Add(item: (first, second, third));

        return 0;
    }

    private sealed record DeviceKind(string[] EntryPoints, Action<VulkanDeviceCommands, nint[]> Release);
    private sealed record InstanceKind(string EntryPoint, Action<VulkanInstanceCommands, nint> Release);
    /// <summary>A destroy allocates nothing, so every allocation fails the law.</summary>
    private sealed class RefusingAllocator : IAllocator {
        public void* Allocate(nuint size, nuint alignment = 0) => throw new NotSupportedException();
        public void Free(void* ptr) => throw new NotSupportedException();
        public void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0) => throw new NotSupportedException();
    }
}
