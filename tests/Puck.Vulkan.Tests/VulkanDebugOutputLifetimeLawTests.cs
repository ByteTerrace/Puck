using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Windowing;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Factories;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Writer ownership through the real instance factory over a recording API, without a Vulkan loader.</summary>
public sealed unsafe class VulkanDebugOutputLifetimeLawTests {
    [InlineData("create")]
    [InlineData("loader")]
    [InlineData("entry point")]
    [InlineData("result")]
    [InlineData("messenger")]
    [Theory]
    public void EveryCreationFailureReleasesTheWriterHandle(string failure) {
        var api = new RecordingApi(failure: failure);
        var writer = new StringWriter();

        FailCreation(api: api, writer: writer);
        Collect();

        Assert.NotNull(@object: api.Output);
        Assert.False(condition: api.Output.TryGetTarget(target: out _));
        Assert.Equal(expected: ((failure == "messenger") ? new[] { "instance" } : []), actual: api.Destroyed);
        Assert.Contains(expectedSubstring: "creation callback", actualString: writer.ToString());
    }
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void TheWriterOutlivesBothDestructionsAndIsReleasedAfterIdempotentDisposal(bool messenger) {
        var api = new RecordingApi(failure: null) { Messenger = messenger };
        var writer = new StringWriter();

        CreateAndDispose(api: api, writer: writer);
        Collect();

        Assert.False(condition: api.Output!.TryGetTarget(target: out _));
        Assert.Equal(expected: (messenger ? new[] { "messenger", "instance" } : ["instance"]), actual: api.Destroyed);
        Assert.Contains(expectedSubstring: "instance callback", actualString: writer.ToString());
        Assert.Contains(expectedSubstring: (messenger ? VulkanInstance.ValidationLiveLine : VulkanInstance.ValidationNotLiveLine), actualString: writer.ToString());
    }
    [Fact]
    public void TheLiveLineUsesTheCallbackWritersLock() {
        var api = new RecordingApi(failure: null);
        var writer = new LockCheckingWriter(api: api);

        CreateAndDispose(api: api, writer: writer);

        Assert.True(condition: writer.EveryWriteHeldTheLock);
        Assert.Contains(expectedSubstring: VulkanInstance.ValidationLiveLine, actualString: writer.ToString());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailCreation(RecordingApi api, StringWriter writer) {
        var exception = Record.Exception(testCode: () => Create(api: api, writer: writer));

        Assert.NotNull(@object: exception);
        Assert.True(condition: (exception is InvalidOperationException or GpuDeviceUnavailableException));
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateAndDispose(RecordingApi api, StringWriter writer) {
        var instance = Create(api: api, writer: writer);

        Assert.Equal(expected: api.Messenger, actual: instance.ReportsValidation);
        instance.Dispose();
        instance.Dispose();
    }
    private static VulkanInstance Create(RecordingApi api, StringWriter writer) => new VulkanInstanceFactory(instanceApi: api).Create(
        applicationName: "writer-law", debugOutput: writer, displayKind: NativeDisplayKind.Win32, enableValidation: true
    );
    private static void Collect() {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint GetProcAddr(nint instance, byte* name) => 1;

    private sealed class RecordingApi(string? failure) : IVulkanInstanceApi {
        private nint m_userData;

        public List<string> Destroyed { get; } = [];
        public bool Messenger { get; init; } = true;
        public WeakReference<TextWriter>? Output { get; private set; }

        public bool HasInstanceExtension(string extensionName, string? layerName) => true;
        public VkResult CreateInstance(VulkanInstanceCreateRequest request, out VulkanInstanceCommands? instance) {
            instance = null;
            m_userData = request.DebugUserData;
            Output = new WeakReference<TextWriter>(target: VulkanDebugOutput.Writer(userData: m_userData));
            VulkanDebugOutput.Writer(userData: m_userData).WriteLine(value: "creation callback");
            switch (failure) {
                case "create": throw new InvalidOperationException(message: "instance command resolution failed");
                case "loader": throw new DllNotFoundException();
                case "entry point": throw new EntryPointNotFoundException();
                case "result": return VkResult.ErrorInitializationFailed;
            }

            instance = new VulkanInstanceCommands(instanceHandle: 1, procedures: new VulkanProcResolver(getDeviceProcAddr: &GetProcAddr, getInstanceProcAddr: &GetProcAddr));
            return VkResult.Success;
        }
        public nint CreateDebugMessenger(VulkanInstanceCommands instance, nint userData) {
            Assert.Equal(actual: userData, expected: m_userData);
            if (failure == "messenger") {
                throw new InvalidOperationException(message: "messenger creation failed");
            }
            return (Messenger ? 2 : 0);
        }
        public void DestroyDebugMessenger(VulkanInstanceCommands instance, nint messengerHandle) {
            if (messengerHandle != 0) {
                Destroy(part: "messenger");
            }
        }
        public void DestroyInstance(VulkanInstanceCommands instance) => Destroy(part: "instance");

        private void Destroy(string part) {
            Collect();
            Assert.True(condition: Output!.TryGetTarget(target: out _), userMessage: "The writer must remain rooted until native teardown returns.");
            VulkanDebugOutput.Writer(userData: m_userData).WriteLine(value: $"{part} callback");
            Destroyed.Add(item: part);
        }
    }
    private sealed class LockCheckingWriter(RecordingApi api) : StringWriter {
        public bool EveryWriteHeldTheLock { get; private set; } = true;

        public override void WriteLine(string? value) {
            EveryWriteHeldTheLock &= (api.Output!.TryGetTarget(target: out var writer) && Monitor.IsEntered(obj: writer));
            base.WriteLine(value: value);
        }
    }
}
