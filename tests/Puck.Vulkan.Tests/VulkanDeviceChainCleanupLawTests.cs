using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Factories;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Puck.Vulkan.Presentation;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Pins, without a device, that the renderer's boot chain cleans up after itself: a failure injected at each
/// link destroys every object the chain created before that link exactly once, in reverse creation order, and never
/// reaches a later link; a chain that completes is destroyed the same way by <see cref="VulkanRenderer.Dispose"/>.</summary>
/// <remarks>The chain runs through the real renderer, factories and physical-device selector over a recording driver:
/// the instance, surface, physical-device and logical-device APIs are fakes that log what they create and destroy,
/// and the command tables are built over a procedure resolver whose pipeline-cache entry points log the same way.
/// Every other entry point resolves to a trap that logs the call, so a table read the chain should not make fails the
/// law rather than reaching a driver.</remarks>
public sealed unsafe class VulkanDeviceChainCleanupLawTests {
    private const nint DeviceHandle = 0x3000;
    private const uint GraphicsFamily = 0;
    private const nint InstanceHandle = 0x1000;
    private const nint MessengerHandle = 0x2000;
    private const nint PhysicalDeviceHandle = 0x4000;
    private const nint PipelineCacheHandle = 0x5000;
    private const uint PresentFamily = 1;
    private const nint QueueHandle = 0x6000;
    private const nint SurfaceHandle = 0x7000;
    // Vulkan 1.3, the selector's device floor.
    private const uint Vulkan13 = (1u << 22) | (3u << 12);

    [ThreadStatic]
    private static List<string>? NativeLog;

    /// <summary>Every link of the chain in the order the chain runs it, and the object it creates, if any.</summary>
    private static readonly (string Link, string? Creates)[] Links = [
        ("instance", "instance"),
        ("debug messenger", "debug messenger"),
        ("surface", "surface"),
        ("physical device", null),
        ("device identity", null),
        ("device", "device"),
        ("graphics queue", null),
        ("present queue", null),
        ("pipeline cache file", null),
        ("pipeline cache", "pipeline cache"),
    ];

    // Every link that can fail the chain. The pipeline cache is best-effort: a cache the driver refuses leaves the
    // device uncached rather than failing it, so it is not a failure link.
    public static TheoryData<string> EveryFailingLink() => [.. Links.Where(predicate: link => (link.Link != "pipeline cache")).Select(selector: link => link.Link)];
    [MemberData(nameof(EveryFailingLink))]
    [Theory]
    public void AFailureAtALinkDestroysEverythingBeforeItOnceInReverseAndTouchesNothingAfter(string link) {
        var driver = new RecordingDriver(failAt: link);
        var built = Links.TakeWhile(predicate: entry => (entry.Link != link)).Where(predicate: entry => (entry.Creates is not null)).Select(selector: entry => entry.Creates!).ToArray();
        Exception? thrown = null;
        var log = Record(run: () => {
            using var renderer = Renderer(driver: driver);

            thrown = Assert.ThrowsAny<Exception>(testCode: () => renderer.Initialize(
                binding: Binding(),
                height: 1,
                width: 1
            ));
        });

        Assert.Equal(
            actual: FailedLink(exception: thrown!),
            expected: link
        );
        Assert.Equal(
            actual: log,
            expected: [.. built.Select(selector: name => $"create {name}"), .. built.Reverse().Select(selector: name => $"destroy {name}")]
        );
    }
    [Fact]
    public void ACompleteChainIsDestroyedOnceInReverseByDispose() {
        var driver = new RecordingDriver(failAt: null);
        var built = Links.Where(predicate: entry => (entry.Creates is not null)).Select(selector: entry => entry.Creates!).ToArray();
        var log = Record(run: () => {
            using var renderer = Renderer(driver: driver);

            renderer.Initialize(
                binding: Binding(),
                height: 1,
                width: 1
            );
            Assert.Equal(
                actual: NativeLog,
                expected: [.. built.Select(selector: name => $"create {name}")]
            );
        });

        Assert.Equal(
            actual: log,
            expected: [.. built.Select(selector: name => $"create {name}"), .. built.Reverse().Select(selector: name => $"destroy {name}")]
        );
    }
    [Fact]
    public void AFailedChainLeavesTheRendererToBuildAFreshOne() {
        var failing = new RecordingDriver(failAt: "present queue");
        var log = Record(run: () => {
            using var renderer = Renderer(driver: failing);

            _ = Assert.Throws<InjectedFailureException>(testCode: () => renderer.Initialize(
                binding: Binding(),
                height: 1,
                width: 1
            ));
            failing.FailAt = null;
            renderer.Initialize(
                binding: Binding(),
                height: 1,
                width: 1
            );
        });
        string[] partial = ["instance", "debug messenger", "surface", "device"];
        string[] whole = [.. Links.Where(predicate: entry => (entry.Creates is not null)).Select(selector: entry => entry.Creates!)];

        Assert.Equal(
            actual: log,
            expected: [
                .. partial.Select(selector: name => $"create {name}"),
                .. partial.Reverse().Select(selector: name => $"destroy {name}"),
                .. whole.Select(selector: name => $"create {name}"),
                .. whole.Reverse().Select(selector: name => $"destroy {name}"),
            ]
        );
    }

    private static NativeSurfaceBinding Binding() =>
        new(
            DisplayKind: NativeDisplayKind.Win32,
            Win32: new Win32NativeSurfaceBinding(
                InstanceHandle: 1,
                WindowHandle: 1
            )
        );
    // The link a failure came from: an injected failure names its own; the pipeline-cache file refuses the identity.
    private static string? FailedLink(Exception exception) =>
        exception switch {
            InjectedFailureException injected => injected.Link,
            ArgumentException { ParamName: "identity" } => "pipeline cache file",
            _ => null,
        };
    private static List<string> Record(Action run) {
        NativeLog = [];

        try {
            run();
            return NativeLog;
        } finally {
            NativeLog = null;
        }
    }
    // The chain's collaborators are real; the presentation collaborators are never reached by the boot chain or its
    // teardown, so they are absent and any use of one fails the law.
    private static VulkanRenderer Renderer(RecordingDriver driver) =>
        new(
            commandBufferRecorder: null!,
            commandResourcesFactory: null!,
            framebufferSetFactory: null!,
            framePresenter: null!,
            frameSynchronizationFactory: null!,
            instanceFactory: new VulkanInstanceFactory(instanceApi: driver),
            logicalDeviceFactory: new VulkanLogicalDeviceFactory(
                logicalDeviceApi: driver,
                physicalDeviceApi: driver,
                pipelineCacheStore: null,
                pipelineCacheWork: new GpuPipelineCacheWork(backend: "vulkan")
            ),
            options: new VulkanRendererOptions {
                ApplicationName = nameof(VulkanDeviceChainCleanupLawTests),
                EnableValidation = true,
            },
            physicalDeviceApi: driver,
            physicalDeviceSelector: new VulkanPhysicalDeviceSelector(physicalDeviceApi: driver),
            presentationOptions: new PresentationOptions(),
            renderPassFactory: null!,
            surfaceFactory: new VulkanSurfaceFactory(surfaceApi: driver),
            swapchainFactory: null!,
            swapchainSupportApi: null!
        );
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static VkResult CreatePipelineCache(nint device, VkPipelineCacheCreateInfo* createInfo, nint allocator, nint* pipelineCache) {
        NativeLog!.Add(item: "create pipeline cache");
        *pipelineCache = PipelineCacheHandle;

        return VkResult.Success;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DestroyPipelineCache(nint device, nint pipelineCache, nint allocator) =>
        NativeLog!.Add(item: ((pipelineCache == PipelineCacheHandle)
            ? "destroy pipeline cache"
            : $"destroy unknown pipeline cache {pipelineCache}"));
    // A vkGet*ProcAddr stand-in: the pipeline-cache entry points record, every other name resolves to the trap.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint GetProcAddr(nint handle, byte* name) {
        var functionName = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(value: name);

        if (functionName.SequenceEqual(other: "vkCreatePipelineCache"u8)) {
            return ((nint)((delegate* unmanaged[Cdecl]<nint, VkPipelineCacheCreateInfo*, nint, nint*, VkResult>)&CreatePipelineCache));
        }

        if (functionName.SequenceEqual(other: "vkDestroyPipelineCache"u8)) {
            return ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&DestroyPipelineCache));
        }

        return ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)&Trap));
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Trap(nint first, nint second, nint third, nint fourth) {
        NativeLog!.Add(item: "unexpected native call");

        return 0;
    }

    /// <summary>The injected failure; a link fails by throwing it before it creates anything.</summary>
    private sealed class InjectedFailureException(string link) : Exception(message: $"injected failure at {link}") {
        public string Link { get; } = link;
    }
    /// <summary>A driver that logs every object it creates and destroys, and fails the chain at one named link.</summary>
    private sealed class RecordingDriver(string? failAt) : IVulkanInstanceApi, IVulkanSurfaceApi, IVulkanPhysicalDeviceApi, IVulkanLogicalDeviceApi {
        public string? FailAt { get; set; } = failAt;

        private static void Log(string entry) =>
            NativeLog!.Add(item: entry);
        private void Reach(string link) {
            if (link == FailAt) {
                throw new InjectedFailureException(link: link);
            }
        }

        public nint CreateDebugMessenger(VulkanInstanceCommands instance) {
            Reach(link: "debug messenger");
            Log(entry: "create debug messenger");

            return MessengerHandle;
        }
        public VkResult CreateInstance(VulkanInstanceCreateRequest request, out VulkanInstanceCommands? instance) {
            Reach(link: "instance");
            instance = new VulkanInstanceCommands(
                getInstanceProcAddr: &GetProcAddr,
                instanceHandle: InstanceHandle
            );
            Log(entry: "create instance");

            return VkResult.Success;
        }
        public VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out VulkanDeviceCommands? device) {
            Reach(link: "device");
            device = new VulkanDeviceCommands(
                deviceHandle: DeviceHandle,
                getDeviceProcAddr: &GetProcAddr
            );
            Log(entry: "create device");

            return VkResult.Success;
        }
        public VkResult CreateViSurface(VulkanInstanceCommands instance, ViNativeSurfaceBinding binding, out nint surfaceHandle) =>
            throw new NotSupportedException();
        public VkResult CreateWaylandSurface(VulkanInstanceCommands instance, WaylandNativeSurfaceBinding binding, out nint surfaceHandle) =>
            throw new NotSupportedException();
        public VkResult CreateWin32Surface(VulkanInstanceCommands instance, Win32NativeSurfaceBinding binding, out nint surfaceHandle) {
            Reach(link: "surface");
            surfaceHandle = SurfaceHandle;
            Log(entry: "create surface");

            return VkResult.Success;
        }
        public VkResult CreateXcbSurface(VulkanInstanceCommands instance, XcbNativeSurfaceBinding binding, out nint surfaceHandle) =>
            throw new NotSupportedException();
        // A zero messenger handle is the zero-handle guard's case (VulkanDestroyGuardLawTests), not a destroy.
        public void DestroyDebugMessenger(VulkanInstanceCommands instance, nint messengerHandle) {
            if (0 != messengerHandle) {
                Log(entry: "destroy debug messenger");
            }
        }
        public void DestroyDevice(VulkanDeviceCommands device) {
            Log(entry: "destroy device");
            device.Dispose();
        }
        public void DestroyInstance(VulkanInstanceCommands instance) =>
            Log(entry: "destroy instance");
        public void DestroySurface(VulkanInstanceCommands instance, nint surfaceHandle) =>
            Log(entry: "destroy surface");
        public IReadOnlyList<nint> EnumeratePhysicalDevices(VulkanInstanceCommands instance) {
            Reach(link: "physical device");

            return [PhysicalDeviceHandle];
        }
        public uint GetDeviceApiVersion(VulkanInstanceCommands instance, nint physicalDeviceHandle) =>
            Vulkan13;
        // The pipeline-cache file refuses an identity whose backend is not one path segment; that is how the link
        // after the queues fails.
        public GpuDeviceIdentity GetDeviceIdentity(VulkanInstanceCommands instance, nint physicalDeviceHandle) {
            Reach(link: "device identity");

            return new(
                AdapterName: "recording driver",
                ApiVersion: "1.3.0",
                Backend: ((FailAt == "pipeline cache file") ? "Not A Segment" : "vulkan"),
                DeviceId: 1,
                DriverVersion: "1",
                DriverVersionRaw: 1,
                VendorId: 1
            );
        }
        public long GetDeviceLuid(VulkanInstanceCommands instance, nint physicalDeviceHandle) =>
            throw new NotSupportedException();
        public string GetDeviceName(VulkanInstanceCommands instance, nint physicalDeviceHandle) =>
            "recording driver";
        public nint GetDeviceQueue(VulkanDeviceCommands device, uint queueFamilyIndex, uint queueIndex) {
            Reach(link: ((queueFamilyIndex == GraphicsFamily) ? "graphics queue" : "present queue"));

            return (QueueHandle + ((nint)queueFamilyIndex));
        }
        public IReadOnlyList<bool> GetFeatureSupport(VulkanInstanceCommands instance, nint physicalDeviceHandle) =>
            [];
        public GpuMemoryProfile GetMemoryProfile(VulkanInstanceCommands instance, nint physicalDeviceHandle) =>
            default;
        public VkPhysicalDeviceType GetPhysicalDeviceType(VulkanInstanceCommands instance, nint physicalDeviceHandle) =>
            VkPhysicalDeviceType.DiscreteGpu;
        public IReadOnlyList<uint> GetPresentModes(VulkanInstanceCommands instance, nint physicalDeviceHandle, nint surfaceHandle) =>
            throw new NotSupportedException();
        // Graphics and present on separate families, so each queue is its own link.
        public IReadOnlyList<VkQueueFamilyInfo> GetQueueFamilies(VulkanInstanceCommands instance, nint physicalDeviceHandle) =>
            [
                new(
                    Flags: VkQueueFlags.Graphics,
                    Index: GraphicsFamily,
                    QueueCount: 1
                ),
                new(
                    Flags: VkQueueFlags.Transfer,
                    Index: PresentFamily,
                    QueueCount: 1
                ),
            ];
        public VulkanSurfaceCapabilities GetSurfaceCapabilities(VulkanInstanceCommands instance, nint physicalDeviceHandle, nint surfaceHandle) =>
            throw new NotSupportedException();
        public IReadOnlyList<VulkanSurfaceFormat> GetSurfaceFormats(VulkanInstanceCommands instance, nint physicalDeviceHandle, nint surfaceHandle) =>
            throw new NotSupportedException();
        public bool GetSurfaceSupport(VulkanInstanceCommands instance, nint physicalDeviceHandle, uint queueFamilyIndex, nint surfaceHandle) =>
            (queueFamilyIndex == PresentFamily);
        public bool HasDeviceExtension(VulkanInstanceCommands instance, nint physicalDeviceHandle, string extensionName) =>
            false;
        public bool HasInstanceExtension(string extensionName) =>
            false;
        public bool IsExtensionFeatureSupported(VulkanInstanceCommands instance, nint physicalDeviceHandle, uint structureType) =>
            false;
        public VkResult WaitIdle(VulkanDeviceCommands device) =>
            VkResult.Success;
    }
}
