using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Puck.Testing;
using Puck.Vulkan;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Factories;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;
using Xunit.Sdk;

namespace Puck.World.Tests;

/// <summary>Exercises the headless fixture's ownership and validation checks over recording APIs, without a loader or device.</summary>
public sealed unsafe class HeadlessVulkanLifecycleLawTests {
    private const string Violation = "[vulkan-debug] validation ERROR: VUID-injected-creation-failure";

    [InlineData("instance")]
    [InlineData("enumeration")]
    [InlineData("device")]
    [InlineData("teardown")]
    [Theory]
    public void ACreationOrTeardownValidationMessageCannotBecomeASkip(string failure) {
        var api = new RecordingApi(failure: failure);

        var exception = Assert.IsType<FailException>(@object: Thrown(create: () => Create(api: api)));

        Assert.Contains(expectedSubstring: "the first VUID-injected-creation-failure", actualString: exception.Message);
        Assert.Equal(expected: ((failure == "instance") ? new[] { "provider" } : ["instance", "provider"]), actual: api.Destroyed);
    }
    [Fact]
    public void AnUnavailableDeviceWithoutValidationMessagesStillSkips() {
        var api = new RecordingApi(failure: "no device");

        var exception = Assert.IsType<SkipException>(@object: Thrown(create: () => Create(api: api)));

        Assert.Contains(expectedSubstring: "no Vulkan device with a graphics queue family", actualString: exception.Message);
        Assert.Equal(expected: ["instance", "provider"], actual: api.Destroyed);
    }
    [Fact]
    public void AMissingMessengerFailsBeforeAnyDeviceQuery() {
        var api = new RecordingApi(failure: "no messenger");

        var exception = Assert.Throws<TrueException>(testCode: () => Create(api: api));

        Assert.Contains(expectedSubstring: VulkanInstance.ValidationNotLiveLine, actualString: exception.Message);
        Assert.Equal(expected: 0, actual: api.PhysicalQueries);
        Assert.Equal(expected: ["instance", "provider"], actual: api.Destroyed);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AFailureAfterDeviceCreationReleasesEveryOwnerInOrder(bool teardownThrows) {
        var api = new RecordingApi(failure: (teardownThrows ? "device teardown" : "name"));

        var exception = Assert.Throws<InvalidOperationException>(testCode: () => Create(api: api));

        Assert.Equal(expected: (teardownThrows ? "device teardown failed" : "device name failed"), actual: exception.Message);
        Assert.Equal(expected: ["device", "instance", "provider"], actual: api.Destroyed);
    }
    [Fact]
    public void AValidationSnapshotHoldsTheCallbackWritersLock() {
        var raw = new LockCheckingWriter();
        var output = new HeadlessVulkanDevice.ValidationOutput(writer: raw);

        raw.Sync = output.Writer;
        using var handle = new VulkanDebugOutput(writer: output.Writer);
        var callbackWriter = VulkanDebugOutput.Writer(userData: handle.UserData);

        Assert.Same(expected: output.Writer, actual: callbackWriter);
        callbackWriter.WriteLine(value: Violation);
        Assert.Equal(expected: [Violation], actual: output.Messages);
        var exception = Assert.Throws<FailException>(testCode: output.AssertClean);

        Assert.Contains(expectedSubstring: "the first VUID-injected-creation-failure", actualString: exception.Message);
    }

    private static HeadlessVulkanDevice Create(RecordingApi api) {
        var physical = DispatchProxy.Create<IVulkanPhysicalDeviceApi, PhysicalQueries>();

        ((PhysicalQueries)physical).Api = api;
        var provider = new ServiceCollection()
            .AddSingleton<IVulkanInstanceFactory>(implementationInstance: new VulkanInstanceFactory(instanceApi: api))
            .AddSingleton<IVulkanPhysicalDeviceApi>(implementationInstance: physical)
            .AddSingleton<IVulkanLogicalDeviceFactory>(implementationInstance: api)
            .AddSingleton(implementationFactory: _ => new ProviderLease(api: api))
            .BuildServiceProvider();

        _ = provider.GetRequiredService<ProviderLease>();

        return HeadlessVulkanDevice.Create(applicationName: "lifecycle-law", provider: provider, validation: true);
    }
    // Assert.Throws rethrows a skip, which would skip the law instead of judging it, so a skip is caught here like any
    // other exception.
    private static Exception? Thrown(Func<HeadlessVulkanDevice> create) {
        try {
            _ = create();
        } catch (Exception exception) {
            return exception;
        }

        return null;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint GetProcAddr(nint instance, byte* name) => 1;
    private static VulkanProcResolver Procedures() => new(getDeviceProcAddr: &GetProcAddr, getInstanceProcAddr: &GetProcAddr);

    private sealed class ProviderLease(RecordingApi api) : IDisposable {
        public void Dispose() => api.Destroyed.Add(item: "provider");
    }
    private sealed class LockCheckingWriter : StringWriter {
        public object? Sync { get; set; }

        public override string ToString() {
            Assert.True(condition: Monitor.IsEntered(obj: Sync!));
            return base.ToString();
        }
    }

    public class PhysicalQueries : DispatchProxy {
        internal RecordingApi Api { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
            Api.PhysicalQueries++;
            switch (targetMethod!.Name) {
                case nameof(IVulkanPhysicalDeviceApi.EnumeratePhysicalDevices):
                    if (Api.Failure == "enumeration") {
                        Api.Report();
                    }
                    return ((Api.Failure is "enumeration" or "teardown" or "no device") ? Array.Empty<nint>() : new nint[] { 3 });
                case nameof(IVulkanPhysicalDeviceApi.GetPhysicalDeviceType): return VkPhysicalDeviceType.DiscreteGpu;
                case nameof(IVulkanPhysicalDeviceApi.GetQueueFamilies): return new VkQueueFamilyInfo[] { new(Flags: VkQueueFlags.Graphics, Index: 0, QueueCount: 1) };
                case nameof(IVulkanPhysicalDeviceApi.GetDeviceLuid): return 0L;
                case nameof(IVulkanPhysicalDeviceApi.GetDeviceName): throw new InvalidOperationException(message: "device name failed");
                default: throw new InvalidOperationException(message: $"unexpected physical-device query: {targetMethod.Name}");
            }
        }
    }

    internal sealed class RecordingApi(string failure) : IVulkanInstanceApi, IVulkanLogicalDeviceFactory, IVulkanLogicalDeviceApi {
        private nint m_userData;

        public string Failure { get; } = failure;
        public List<string> Destroyed { get; } = [];

        public int PhysicalQueries { get; set; }

        public void Report() => VulkanDebugOutput.Writer(userData: m_userData).WriteLine(value: Violation);
        public bool HasInstanceExtension(string extensionName, string? layerName) => true;
        public VkResult CreateInstance(VulkanInstanceCreateRequest request, out VulkanInstanceCommands? instance) {
            m_userData = request.DebugUserData;
            instance = null;
            if (Failure == "instance") {
                Report();
                return VkResult.ErrorInitializationFailed;
            }
            instance = new VulkanInstanceCommands(instanceHandle: 1, procedures: Procedures());
            return VkResult.Success;
        }
        public nint CreateDebugMessenger(VulkanInstanceCommands instance, nint userData) => ((Failure == "no messenger") ? 0 : 2);
        public void DestroyDebugMessenger(VulkanInstanceCommands instance, nint messengerHandle) { }
        public void DestroyInstance(VulkanInstanceCommands instance) {
            if (Failure == "teardown") {
                Report();
            }
            Destroyed.Add(item: "instance");
        }
        public VulkanLogicalDevice Create(VulkanInstance instance, VkPhysicalDevice physicalDevice) {
            if (Failure == "device") {
                Report();
                throw VulkanResultExtensions.Unavailable(reason: "injected unavailable device");
            }
            return new VulkanLogicalDevice(
                device: new VulkanDeviceCommands(deviceHandle: 4, procedures: Procedures()),
                graphicsQueue: new VkQueue(familyIndex: 0, handle: 5),
                logicalDeviceApi: this,
                physicalDevice: physicalDevice,
                presentQueue: new VkQueue(familyIndex: 0, handle: 5)
            );
        }
        public void DestroyDevice(VulkanDeviceCommands device) {
            Destroyed.Add(item: "device");
            if (Failure == "device teardown") {
                throw new InvalidOperationException(message: "device teardown failed");
            }
        }
        public VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out VulkanDeviceCommands? device) => throw new NotSupportedException();
        public nint GetDeviceQueue(VulkanDeviceCommands device, uint queueFamilyIndex, uint queueIndex) => throw new NotSupportedException();
        public VkResult WaitIdle(VulkanDeviceCommands device) => throw new NotSupportedException();
    }
}
