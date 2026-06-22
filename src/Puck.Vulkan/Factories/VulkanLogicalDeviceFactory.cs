using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanLogicalDeviceFactory"/>: it creates a logical device, enabling the
/// swapchain extension always and the optional ray-query, pipeline-executable-properties, and
/// storage-image-without-format features only when the physical device fully supports them.
/// </summary>
public sealed class VulkanLogicalDeviceFactory : IVulkanLogicalDeviceFactory {
    private const string SwapchainExtension = "VK_KHR_swapchain";

    /// <summary>The device-extension bundle for the optional ray-query path; enabled only
    /// when the device supports the full set. Callers probe again — via the
    /// acceleration-structure API's <c>SupportsDevice</c> — before relying on it, so devices
    /// without it are left to fall back on their own.</summary>
    private static readonly string[] RayQueryExtensions = [
        "VK_KHR_acceleration_structure",
        "VK_KHR_ray_query",
        "VK_KHR_deferred_host_operations",
        "VK_KHR_buffer_device_address",
    ];

    /// <summary>Diagnostic introspection extension (compiled register counts etc.); enabled
    /// whenever supported. Pure read-back — it changes no pipeline codegen by itself, so
    /// enabling it is pixel-neutral.</summary>
    private const string PipelineExecutablePropertiesExtension = "VK_KHR_pipeline_executable_properties";

    /// <summary>Win32 external-memory import, for sampling a texture another backend (Direct3D 12) produced
    /// without a CPU round-trip. Enabled whenever supported; the base extension is core in Vulkan 1.1, only
    /// the win32 handle-import entry points need the extension. Pixel-neutral when unused.</summary>
    private static readonly string[] ExternalMemoryExtensions = [
        "VK_KHR_external_memory",
        "VK_KHR_external_memory_win32",
    ];

    /// <summary>Closed-loop present-timing extensions: <c>present_id</c> tags each present, <c>present_wait</c> blocks
    /// until it is displayed. Enabled only when both extensions AND both features are supported; otherwise the host pacer
    /// stays open-loop. present_wait depends on present_id, so both are required together.</summary>
    private static readonly string[] PresentTimingExtensions = [
        "VK_KHR_present_id",
        "VK_KHR_present_wait",
    ];

    // Extension feature struct sTypes, verified against the Vulkan SDK 1.4.350 header
    // (vulkan_core.h). Each enables the struct's first VkBool32 when chained.
    private const uint StructureTypePhysicalDeviceBufferDeviceAddressFeatures = 1000257000;
    private const uint StructureTypePhysicalDeviceAccelerationStructureFeaturesKhr = 1000150013;
    private const uint StructureTypePhysicalDevicePipelineExecutablePropertiesFeaturesKhr = 1000269000;
    private const uint StructureTypePhysicalDeviceRayQueryFeaturesKhr = 1000348013;
    // CAUTION: the present_id block has TWO adjacent sTypes that are easy to transpose —
    // VK_STRUCTURE_TYPE_PRESENT_ID_KHR = 1000294000 (the present-info struct, used at present time in
    // VulkanNativeFramePresentationApi) and VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PRESENT_ID_FEATURES_KHR = 1000294001
    // (the FEATURE struct, used here). Swapping them silently breaks the feature query AND device creation. (1000294001)
    private const uint StructureTypePhysicalDevicePresentIdFeaturesKhr = 1000294001;
    private const uint StructureTypePhysicalDevicePresentWaitFeaturesKhr = 1000248000;

    // 0-based VkPhysicalDeviceFeatures flag indices for storage-image read/write without a
    // shader format qualifier (shaderStorageImage*WithoutFormat) — needed to write image
    // views whose format (commonly BGRA8) has no GLSL format qualifier. Enabled only when
    // the device reports them; callers that need them probe separately and fall back otherwise.
    private static readonly uint[] StorageImageFeatureIndices = [31u, 32u, 36u];

    private static IReadOnlyList<VulkanDeviceQueueCreateRequest> BuildQueues(
        VulkanQueueFamilySelection queueFamilySelection
    ) {
        if (queueFamilySelection.UsesSingleQueueFamily) {
            return [new VulkanDeviceQueueCreateRequest(
                FamilyIndex: queueFamilySelection.GraphicsFamilyIndex,
                Priority: 1.0f
            )];
        }

        return [
            new VulkanDeviceQueueCreateRequest(
                FamilyIndex: queueFamilySelection.GraphicsFamilyIndex,
                Priority: 1.0f
            ),
            new VulkanDeviceQueueCreateRequest(
                FamilyIndex: queueFamilySelection.PresentFamilyIndex,
                Priority: 1.0f
            ),
        ];
    }

    private readonly IVulkanLogicalDeviceApi m_logicalDeviceApi;
    private readonly IVulkanPhysicalDeviceApi m_physicalDeviceApi;

    /// <summary>Initializes a new instance of the <see cref="VulkanLogicalDeviceFactory"/> class.</summary>
    /// <param name="logicalDeviceApi">The logical-device API used to create the device and retrieve its queues.</param>
    /// <param name="physicalDeviceApi">The physical-device API used to probe optional extension and feature support.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logicalDeviceApi"/> or <paramref name="physicalDeviceApi"/> is <see langword="null"/>.</exception>
    public VulkanLogicalDeviceFactory(
        IVulkanLogicalDeviceApi logicalDeviceApi,
        IVulkanPhysicalDeviceApi physicalDeviceApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: logicalDeviceApi);
        ArgumentNullException.ThrowIfNull(argument: physicalDeviceApi);

        m_logicalDeviceApi = logicalDeviceApi;
        m_physicalDeviceApi = physicalDeviceApi;
    }

    /// <summary>Whether the device supports the full ray-query bundle (all extensions AND
    /// the accelerationStructure/rayQuery/bufferDeviceAddress features).</summary>
    private bool SupportsRayQuery(nint instanceHandle, nint physicalDeviceHandle) {
        foreach (var extension in RayQueryExtensions) {
            if (!m_physicalDeviceApi.HasDeviceExtension(
                extensionName: extension,
                instanceHandle: instanceHandle,
                physicalDeviceHandle: physicalDeviceHandle
            )) {
                return false;
            }
        }

        return (
            m_physicalDeviceApi.IsExtensionFeatureSupported(
                instanceHandle: instanceHandle,
                physicalDeviceHandle: physicalDeviceHandle,
                structureType: StructureTypePhysicalDeviceRayQueryFeaturesKhr
            ) &&
            m_physicalDeviceApi.IsExtensionFeatureSupported(
                instanceHandle: instanceHandle,
                physicalDeviceHandle: physicalDeviceHandle,
                structureType: StructureTypePhysicalDeviceAccelerationStructureFeaturesKhr
            ) &&
            m_physicalDeviceApi.IsExtensionFeatureSupported(
                instanceHandle: instanceHandle,
                physicalDeviceHandle: physicalDeviceHandle,
                structureType: StructureTypePhysicalDeviceBufferDeviceAddressFeatures
            )
        );
    }
    private bool SupportsPipelineExecutableProperties(nint instanceHandle, nint physicalDeviceHandle) {
        return (
            m_physicalDeviceApi.HasDeviceExtension(
                extensionName: PipelineExecutablePropertiesExtension,
                instanceHandle: instanceHandle,
                physicalDeviceHandle: physicalDeviceHandle
            ) &&
            m_physicalDeviceApi.IsExtensionFeatureSupported(
                instanceHandle: instanceHandle,
                physicalDeviceHandle: physicalDeviceHandle,
                structureType: StructureTypePhysicalDevicePipelineExecutablePropertiesFeaturesKhr
            )
        );
    }
    /// <summary>Whether the device supports the full present-timing bundle (both extensions AND both features), so the
    /// host pacer can phase-lock to actual present times. Falls back to open-loop pacing when any piece is missing.</summary>
    private bool SupportsPresentTiming(nint instanceHandle, nint physicalDeviceHandle) {
        foreach (var extension in PresentTimingExtensions) {
            if (!m_physicalDeviceApi.HasDeviceExtension(
                extensionName: extension,
                instanceHandle: instanceHandle,
                physicalDeviceHandle: physicalDeviceHandle
            )) {
                return false;
            }
        }

        return (
            m_physicalDeviceApi.IsExtensionFeatureSupported(
                instanceHandle: instanceHandle,
                physicalDeviceHandle: physicalDeviceHandle,
                structureType: StructureTypePhysicalDevicePresentIdFeaturesKhr
            ) &&
            m_physicalDeviceApi.IsExtensionFeatureSupported(
                instanceHandle: instanceHandle,
                physicalDeviceHandle: physicalDeviceHandle,
                structureType: StructureTypePhysicalDevicePresentWaitFeaturesKhr
            )
        );
    }
    private (IReadOnlyList<string> ExtensionNames, IReadOnlyList<uint> FeatureStructureTypes) ComposeExtensionsAndFeatures(
        nint instanceHandle,
        nint physicalDeviceHandle
    ) {
        var extensions = new List<string> { SwapchainExtension };
        var featureStructureTypes = new List<uint>();

        if (SupportsRayQuery(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        )) {
            extensions.AddRange(collection: RayQueryExtensions);
            featureStructureTypes.Add(item: StructureTypePhysicalDeviceBufferDeviceAddressFeatures);
            featureStructureTypes.Add(item: StructureTypePhysicalDeviceAccelerationStructureFeaturesKhr);
            featureStructureTypes.Add(item: StructureTypePhysicalDeviceRayQueryFeaturesKhr);
        }

        if (SupportsPipelineExecutableProperties(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        )) {
            extensions.Add(item: PipelineExecutablePropertiesExtension);
            featureStructureTypes.Add(item: StructureTypePhysicalDevicePipelineExecutablePropertiesFeaturesKhr);
        }

        if (m_physicalDeviceApi.HasDeviceExtension(
            extensionName: "VK_KHR_external_memory_win32",
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        )) {
            extensions.AddRange(collection: ExternalMemoryExtensions);
        }

        var presentTiming = SupportsPresentTiming(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        );

        if (presentTiming) {
            extensions.AddRange(collection: PresentTimingExtensions);
            featureStructureTypes.Add(item: StructureTypePhysicalDevicePresentIdFeaturesKhr);
            featureStructureTypes.Add(item: StructureTypePhysicalDevicePresentWaitFeaturesKhr);
        }

        Console.Error.WriteLine(value: $"[present-timing] VK_KHR_present_wait/present_id {(presentTiming ? "ENABLED (closed-loop pacing)" : "unavailable (open-loop pacing)")}");

        return (extensions, featureStructureTypes);
    }
    private IReadOnlyList<uint> ComposeFeatureIndices(nint instanceHandle, nint physicalDeviceHandle) {
        var support = m_physicalDeviceApi.GetFeatureSupport(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        );
        var featureIndices = new List<uint>();

        foreach (var index in StorageImageFeatureIndices) {
            if (
                (index < support.Count) &&
                support[(int)index]
            ) {
                featureIndices.Add(item: index);
            }
        }

        return featureIndices;
    }
    private VkQueue CreateQueue(
        nint deviceHandle,
        uint queueFamilyIndex
    ) {
        var queueHandle = m_logicalDeviceApi.GetDeviceQueue(
            deviceHandle: deviceHandle,
            queueFamilyIndex: queueFamilyIndex,
            queueIndex: 0
        );

        if (0 == queueHandle) {
            throw new InvalidOperationException(message: $"vkGetDeviceQueue returned success without a valid queue handle for family index {queueFamilyIndex}.");
        }

        return new VkQueue(
            familyIndex: queueFamilyIndex,
            handle: queueHandle
        );
    }

    /// <inheritdoc/>
    public VulkanLogicalDevice Create(
        VulkanInstance instance,
        VkPhysicalDevice physicalDevice
    ) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        var (extensionNames, featureStructureTypes) = ComposeExtensionsAndFeatures(
            instanceHandle: instance.Handle,
            physicalDeviceHandle: physicalDevice.Handle
        );
        var request = new VulkanLogicalDeviceCreateRequest(
            EnabledFeatureIndices: ComposeFeatureIndices(
                instanceHandle: instance.Handle,
                physicalDeviceHandle: physicalDevice.Handle
            ),
            EnabledFeatureStructureTypes: featureStructureTypes,
            ExtensionNames: extensionNames,
            InstanceHandle: instance.Handle,
            PhysicalDevice: physicalDevice,
            Queues: BuildQueues(queueFamilySelection: physicalDevice.QueueFamilySelection)
        );
        var result = m_logicalDeviceApi.CreateLogicalDevice(
            deviceHandle: out var deviceHandle,
            request: request
        );

        result.ThrowIfFailed(operation: "vkCreateDevice");

        if (0 == deviceHandle) {
            throw new InvalidOperationException(message: "vkCreateDevice returned success without a valid device handle.");
        }

        var graphicsQueue = CreateQueue(
            deviceHandle: deviceHandle,
            queueFamilyIndex: physicalDevice.QueueFamilySelection.GraphicsFamilyIndex
        );
        var presentQueue = (physicalDevice.QueueFamilySelection.UsesSingleQueueFamily
            ? graphicsQueue
            : CreateQueue(
                deviceHandle: deviceHandle,
                queueFamilyIndex: physicalDevice.QueueFamilySelection.PresentFamilyIndex
            ));

        return new(
            deviceHandle: deviceHandle,
            graphicsQueue: graphicsQueue,
            logicalDeviceApi: m_logicalDeviceApi,
            physicalDevice: physicalDevice,
            presentQueue: presentQueue
        );
    }
}
