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

/// <summary>Pins, without a loader, what each validation level asks the layer for: the chain <c>vkCreateInstance</c>'s
/// create-info carries leads with a <c>VkValidationFeaturesEXT</c> enabling synchronization validation, and at the full
/// level GPU-assisted validation and best practices with a <c>VkLayerSettingsEXT</c> turning on NVIDIA's best
/// practices, and ends with the messenger; the factory enables the layer's extensions that declare them; an instance
/// requested without the layer carries none of it.</summary>
public sealed unsafe class VulkanInstanceCreateChainLawTests {
    private const uint MessengerStructureType = 1000128004;

    [Fact]
    public void ABasicInstanceChainsSynchronizationValidationBeforeTheMessenger() {
        VulkanInstanceCreateChain chain = default;
        var head = VulkanNativeInstanceApi.LinkCreateChain(
            chain: &chain,
            debugUserData: 0x5EED,
            validation: GpuValidation.Basic
        );
        var features = ((VkValidationFeaturesExt*)head);

        Assert.Equal(
            actual: features->StructureType,
            expected: VulkanInstanceCreateChain.ValidationFeaturesStructureType
        );
        Assert.Equal(
            actual: features->EnabledValidationFeatureCount,
            expected: 1U
        );
        Assert.Equal(
            actual: *((uint*)features->EnabledValidationFeatures),
            expected: VulkanInstanceCreateChain.SynchronizationValidation
        );
        Assert.Equal(
            actual: features->DisabledValidationFeatureCount,
            expected: 0U
        );
        AssertMessenger(messenger: ((VkDebugUtilsMessengerCreateInfoExt*)features->Next));
    }
    [Fact]
    public void AFullInstanceAddsGpuAssistedValidationBestPracticesAndNvidiasSetting() {
        VulkanInstanceCreateChain chain = default;
        var features = ((VkValidationFeaturesExt*)VulkanNativeInstanceApi.LinkCreateChain(
            chain: &chain,
            debugUserData: 0x5EED,
            validation: GpuValidation.Full
        ));
        var enabled = new ReadOnlySpan<uint>(
            length: ((int)features->EnabledValidationFeatureCount),
            pointer: ((void*)features->EnabledValidationFeatures)
        );

        Assert.Equal(
            actual: enabled.ToArray(),
            expected: [VulkanInstanceCreateChain.SynchronizationValidation, VulkanInstanceCreateChain.GpuAssistedValidation, VulkanInstanceCreateChain.BestPractices]
        );

        var settings = ((VkLayerSettingsCreateInfoExt*)features->Next);

        Assert.Equal(
            actual: settings->StructureType,
            expected: VulkanInstanceCreateChain.LayerSettingsStructureType
        );
        Assert.Equal(
            actual: settings->SettingCount,
            expected: 1U
        );

        var setting = ((VkLayerSettingExt*)settings->Settings);

        Assert.Equal(
            actual: Marshal.PtrToStringUTF8(ptr: setting->LayerName),
            expected: VulkanInstanceCreateChain.ValidationLayer
        );
        Assert.Equal(
            actual: Marshal.PtrToStringUTF8(ptr: setting->SettingName),
            expected: VulkanInstanceCreateChain.NvidiaBestPracticesSetting
        );
        Assert.Equal(
            actual: (setting->Type, setting->ValueCount, *((uint*)setting->Values)),
            expected: (VulkanInstanceCreateChain.LayerSettingBool32, 1U, 1U)
        );
        AssertMessenger(messenger: ((VkDebugUtilsMessengerCreateInfoExt*)settings->Next));
    }
    [Fact]
    public void AnInstanceWithoutValidationChainsNothing() {
        VulkanInstanceCreateChain chain = default;

        Assert.Equal(
            actual: VulkanNativeInstanceApi.LinkCreateChain(
                chain: &chain,
                debugUserData: 0,
                validation: GpuValidation.Off
            ),
            expected: 0
        );
    }
    [Fact]
    public void AnUnreachableChainIsRefused() =>
        Assert.Throws<ArgumentNullException>(testCode: () => VulkanNativeInstanceApi.LinkCreateChain(
            chain: null,
            debugUserData: 0,
            validation: GpuValidation.Basic
        ));
    [InlineData(GpuValidation.Off)]
    [InlineData(GpuValidation.Basic)]
    [InlineData(GpuValidation.Full)]
    [Theory]
    public void TheFactoryEnablesTheLayersExtensionsExactlyWithTheirLevel(GpuValidation validation) {
        var api = new RequestRecordingApi();

        _ = Assert.Throws<InvalidOperationException>(testCode: () => new VulkanInstanceFactory(instanceApi: api).Create(
            applicationName: "law",
            displayKind: NativeDisplayKind.Win32,
            validation: validation
        ));

        var request = api.Request!.Value;

        Assert.Equal(
            actual: request.Validation,
            expected: validation
        );
        Assert.Equal(
            actual: request.ExtensionNames.Contains(value: VulkanInstanceCreateChain.ValidationFeaturesExtension),
            expected: (validation != GpuValidation.Off)
        );
        Assert.Equal(
            actual: request.ExtensionNames.Contains(value: VulkanInstanceCreateChain.LayerSettingsExtension),
            expected: (validation == GpuValidation.Full)
        );
        Assert.Equal(
            actual: request.LayerNames.Contains(value: VulkanInstanceCreateChain.ValidationLayer),
            expected: (validation != GpuValidation.Off)
        );
    }
    [InlineData("BestPractices-NVIDIA-ClearColor-NotCompressed", true)]
    [InlineData("BestPractices-vkCreateDevice-deprecated-extension", true)]
    [InlineData("SYNC-HAZARD-WRITE-AFTER-READ", false)]
    [InlineData(null, false)]
    [Theory]
    public void BestPracticesAdviceIsToldFromValidationByItsIdentifier(string? messageIdName, bool advice) {
        var name = ((messageIdName is null) ? 0 : Marshal.StringToCoTaskMemUTF8(s: messageIdName));

        try {
            Assert.Equal(
                actual: VulkanNativeInstanceApi.IsBestPractice(messageIdName: name),
                expected: advice
            );
        } finally {
            Marshal.FreeCoTaskMem(ptr: name);
        }
    }

    private static void AssertMessenger(VkDebugUtilsMessengerCreateInfoExt* messenger) {
        Assert.Equal(
            actual: messenger->StructureType,
            expected: MessengerStructureType
        );
        Assert.NotEqual(
            actual: messenger->UserCallback,
            expected: 0
        );
        // The writer the instance's messages go to rides as the callback's pUserData.
        Assert.Equal(
            actual: messenger->UserData,
            expected: 0x5EED
        );
        Assert.Equal(
            actual: messenger->Next,
            expected: 0
        );
    }

    // Advertises the validation features and layer settings only from the validation layer, as the layer's manifest
    // does, records the request it is handed and refuses the creation, so the factory's request is observed without a
    // loader.
    private sealed class RequestRecordingApi : IVulkanInstanceApi {
        public VulkanInstanceCreateRequest? Request { get; private set; }

        public nint CreateDebugMessenger(VulkanInstanceCommands instance, nint userData) => throw new NotSupportedException();
        public VkResult CreateInstance(VulkanInstanceCreateRequest request, out VulkanInstanceCommands? instance) {
            Request = request;
            instance = null;

            throw new InvalidOperationException(message: "the law observes the request only");
        }
        public void DestroyDebugMessenger(VulkanInstanceCommands instance, nint messengerHandle) { }
        public void DestroyInstance(VulkanInstanceCommands instance) { }
        public bool HasInstanceExtension(string extensionName, string? layerName) =>
            ((extensionName is VulkanInstanceCreateChain.ValidationFeaturesExtension or VulkanInstanceCreateChain.LayerSettingsExtension) &&
            (layerName == VulkanInstanceCreateChain.ValidationLayer));
    }
}
