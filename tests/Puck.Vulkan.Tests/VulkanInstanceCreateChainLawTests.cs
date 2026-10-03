using Puck.Abstractions.Windowing;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Factories;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Pins, without a loader, that a Vulkan instance requested with the validation layer turns synchronization
/// validation on: the chain <c>vkCreateInstance</c>'s create-info carries leads with a <c>VkValidationFeaturesEXT</c>
/// enabling <c>VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT</c> and ends with the messenger, the
/// factory enables the layer's <c>VK_EXT_validation_features</c> that declares it, and an instance requested without
/// the layer carries neither.</summary>
public sealed unsafe class VulkanInstanceCreateChainLawTests {
    private const uint MessengerStructureType = 1000128004;

    [Fact]
    public void AValidatedInstanceChainsSynchronizationValidationBeforeTheMessenger() {
        VulkanInstanceCreateChain chain = default;
        var head = VulkanNativeInstanceApi.LinkCreateChain(
            chain: &chain,
            debugUserData: 0x5EED,
            enableValidation: true
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

        var messenger = ((VkDebugUtilsMessengerCreateInfoExt*)features->Next);

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
    [Fact]
    public void AnInstanceWithoutValidationChainsNothing() {
        VulkanInstanceCreateChain chain = default;

        Assert.Equal(
            actual: VulkanNativeInstanceApi.LinkCreateChain(
                chain: &chain,
                debugUserData: 0,
                enableValidation: false
            ),
            expected: 0
        );
    }
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void TheFactoryEnablesTheLayersValidationFeaturesExactlyWithTheLayer(bool enableValidation) {
        var api = new RequestRecordingApi();

        _ = Assert.Throws<InvalidOperationException>(testCode: () => new VulkanInstanceFactory(instanceApi: api).Create(
            applicationName: "law",
            displayKind: NativeDisplayKind.Win32,
            enableValidation: enableValidation
        ));

        var request = api.Request!.Value;

        Assert.Equal(
            actual: request.EnableValidation,
            expected: enableValidation
        );
        Assert.Equal(
            actual: request.ExtensionNames.Contains(value: VulkanInstanceCreateChain.ValidationFeaturesExtension),
            expected: enableValidation
        );
        Assert.Equal(
            actual: request.LayerNames.Contains(value: VulkanInstanceCreateChain.ValidationLayer),
            expected: enableValidation
        );
    }

    // Advertises the validation features only from the validation layer, as the layer's manifest does, records the
    // request it is handed and refuses the creation, so the factory's request is observed without a loader.
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
            ((extensionName == VulkanInstanceCreateChain.ValidationFeaturesExtension) &&
            (layerName == VulkanInstanceCreateChain.ValidationLayer));
    }
}
