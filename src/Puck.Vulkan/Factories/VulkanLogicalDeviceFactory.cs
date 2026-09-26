using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanLogicalDeviceFactory"/>: it creates a logical device, enabling the
/// swapchain extension always and the optional pipeline-executable-properties,
/// storage-image-without-format, block-compressed texture (<c>textureCompressionBC</c>), external memory and semaphore,
/// timeline-semaphore, and GPU capability-floor (fp16, 16-bit storage, subgroup-size-control) features only when the
/// physical device supports them.
/// </summary>
public sealed class VulkanLogicalDeviceFactory : IVulkanLogicalDeviceFactory {
    /// <summary>Diagnostic introspection extension (compiled register counts etc.); enabled
    /// whenever supported. Pure read-back — it changes no pipeline codegen by itself, so
    /// enabling it is pixel-neutral.</summary>
    private const string PipelineExecutablePropertiesExtension = "VK_KHR_pipeline_executable_properties";
    private const uint StructureTypePhysicalDevice16BitStorageFeatures = 1000083000;
    // Extension feature struct sTypes, verified against the Vulkan SDK 1.4.350 header
    // (vulkan_core.h). Each enables the struct's first VkBool32 when chained.
    private const uint StructureTypePhysicalDevicePipelineExecutablePropertiesFeaturesKhr = 1000269000;
    // CAUTION: the present_id block has TWO adjacent sTypes that are easy to transpose —
    // VK_STRUCTURE_TYPE_PRESENT_ID_KHR = 1000294000 (the present-info struct, used at present time in
    // VulkanNativeFramePresentationApi) and VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PRESENT_ID_FEATURES_KHR = 1000294001
    // (the FEATURE struct, used here). Swapping them silently breaks the feature query AND device creation. (1000294001)
    private const uint StructureTypePhysicalDevicePresentIdFeaturesKhr = 1000294001;
    private const uint StructureTypePhysicalDevicePresentWaitFeaturesKhr = 1000248000;
    // The GPU capability floor requires fp16 arithmetic and 16-bit storage — supported at 2× rate on all four target
    // GPUs (Turing / RDNA2 / RDNA3) — plus subgroup-size-control from Vulkan 1.3. Each is enabled only when the
    // device reports it, through the generic single-flag chain: the FIRST VkBool32 of each struct is exactly the
    // feature wanted — shaderFloat16 / storageBuffer16BitAccess / subgroupSizeControl — so the chain reaches it.
    // Enabling these features is pixel- and performance-neutral until a kernel uses them. Do not route them through the aggregate
    // VkPhysicalDeviceVulkan1xFeatures structs — their flags are not first, so the single-flag chain would miss them.
    private const uint StructureTypePhysicalDeviceShaderFloat16Int8Features = 1000082000;
    // 1000225002 = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SUBGROUP_SIZE_CONTROL_FEATURES (verified against the Vulkan SDK
    // 1.4.350 header). NOT 1000225001 — that is VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_REQUIRED_SUBGROUP_SIZE_CREATE_INFO,
    // the pipeline-stage create-info; probing the device's Features2 with it returns FALSE on hardware that DOES
    // support the feature.
    private const uint StructureTypePhysicalDeviceSubgroupSizeControlFeatures = 1000225002;
    // VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_TIMELINE_SEMAPHORE_FEATURES; its first VkBool32 is timelineSemaphore, which a
    // Direct3D 12 shared fence imported as a semaphore needs.
    private const uint StructureTypePhysicalDeviceTimelineSemaphoreFeatures = 1000207000;
    private const string SwapchainExtension = "VK_KHR_swapchain";

    /// <summary>Win32 external-memory import, for sampling a texture another backend (Direct3D 12) produced
    /// without a CPU round-trip. Enabled whenever supported; the base extension is core in Vulkan 1.1, only
    /// the win32 handle-import entry points need the extension. Pixel-neutral when unused.</summary>
    private static readonly string[] ExternalMemoryExtensions = [
        "VK_KHR_external_memory",
        "VK_KHR_external_memory_win32",
    ];
    /// <summary>Win32 external-semaphore import, for waiting on a Direct3D 12 shared fence imported as a timeline
    /// semaphore (<c>VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_D3D12_FENCE_BIT</c>). Enabled whenever supported, with the
    /// timeline-semaphore feature; the base extension is core in Vulkan 1.1.</summary>
    private static readonly string[] ExternalSemaphoreExtensions = [
        "VK_KHR_external_semaphore",
        "VK_KHR_external_semaphore_win32",
    ];
    /// <summary>Closed-loop present-timing extensions: <c>present_id</c> tags each present, <c>present_wait</c> blocks
    /// until it is displayed. Enabled only when both extensions AND both features are supported; otherwise the host pacer
    /// stays open-loop. present_wait depends on present_id, so both are required together.</summary>
    private static readonly string[] PresentTimingExtensions = [
        "VK_KHR_present_id",
        "VK_KHR_present_wait",
    ];

    // The 0-based VkPhysicalDeviceFeatures flag index of textureCompressionBC: every BC1 to BC7 format is sampleable
    // from optimal-tiling images. VulkanLogicalDevice.SamplesBlockCompression records whether it was enabled, and an
    // upload of a block-compressed format refuses a device without it.
    private const uint TextureCompressionBcFeatureIndex = 22u;

    // 0-based VkPhysicalDeviceFeatures flag indices enabled only when the device reports them: textureCompressionBC,
    // and storage-image read/write without a shader format qualifier (shaderStorageImage*WithoutFormat), needed to
    // write image views whose format (commonly BGRA8) has no GLSL format qualifier; callers that need those probe
    // separately and fall back otherwise.
    private static readonly uint[] OptionalBaseFeatureIndices = [TextureCompressionBcFeatureIndex, 31u, 32u, 36u];

    private readonly IVulkanLogicalDeviceApi m_logicalDeviceApi;
    private readonly IVulkanPhysicalDeviceApi m_physicalDeviceApi;
    private readonly GpuPipelineCacheStore? m_pipelineCacheStore;
    private readonly GpuPipelineCacheWork m_pipelineCacheWork;

    // Enable the GPU capability-floor features (fp16, 16-bit storage, subgroup-size-control) on any device that reports
    // them. Core-promoted (Vulkan 1.1/1.2/1.3), so no device extension is needed — just the chained feature struct.
    private void AppendCapabilityFloorFeatures(
        List<uint> featureStructureTypes,
        VulkanInstanceCommands instance,
        nint physicalDeviceHandle
    ) {
        var shaderFloat16 = m_physicalDeviceApi.IsExtensionFeatureSupported(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle,
            structureType: StructureTypePhysicalDeviceShaderFloat16Int8Features
        );

        if (shaderFloat16) {
            featureStructureTypes.Add(item: StructureTypePhysicalDeviceShaderFloat16Int8Features);
        }

        var storage16Bit = m_physicalDeviceApi.IsExtensionFeatureSupported(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle,
            structureType: StructureTypePhysicalDevice16BitStorageFeatures
        );

        if (storage16Bit) {
            featureStructureTypes.Add(item: StructureTypePhysicalDevice16BitStorageFeatures);
        }

        var subgroupSizeControl = m_physicalDeviceApi.IsExtensionFeatureSupported(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle,
            structureType: StructureTypePhysicalDeviceSubgroupSizeControlFeatures
        );

        if (subgroupSizeControl) {
            featureStructureTypes.Add(item: StructureTypePhysicalDeviceSubgroupSizeControlFeatures);
        }

        Console.Error.WriteLine(value: $"[capability-floor] shaderFloat16 {(shaderFloat16
            ? "ENABLED"
            : "unavailable")}, storageBuffer16BitAccess {(storage16Bit
            ? "ENABLED"
            : "unavailable")}, subgroupSizeControl {(subgroupSizeControl
            ? "ENABLED"
            : "unavailable")}");
    }
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
    private (IReadOnlyList<string> ExtensionNames, IReadOnlyList<uint> FeatureStructureTypes) ComposeExtensionsAndFeatures(
        VulkanInstanceCommands instance,
        nint physicalDeviceHandle
    ) {
        var extensions = new List<string> { SwapchainExtension };
        var featureStructureTypes = new List<uint>();

        if (SupportsPipelineExecutableProperties(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        )) {
            extensions.Add(item: PipelineExecutablePropertiesExtension);
            featureStructureTypes.Add(item: StructureTypePhysicalDevicePipelineExecutablePropertiesFeaturesKhr);
        }

        if (m_physicalDeviceApi.HasDeviceExtension(
            extensionName: "VK_KHR_external_memory_win32",
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        )) {
            extensions.AddRange(collection: ExternalMemoryExtensions);
        }

        if (m_physicalDeviceApi.HasDeviceExtension(
            extensionName: "VK_KHR_external_semaphore_win32",
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        )) {
            extensions.AddRange(collection: ExternalSemaphoreExtensions);
        }

        if (m_physicalDeviceApi.IsExtensionFeatureSupported(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle,
            structureType: StructureTypePhysicalDeviceTimelineSemaphoreFeatures
        )) {
            featureStructureTypes.Add(item: StructureTypePhysicalDeviceTimelineSemaphoreFeatures);
        }

        var presentTiming = SupportsPresentTiming(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        );

        if (presentTiming) {
            extensions.AddRange(collection: PresentTimingExtensions);
            featureStructureTypes.Add(item: StructureTypePhysicalDevicePresentIdFeaturesKhr);
            featureStructureTypes.Add(item: StructureTypePhysicalDevicePresentWaitFeaturesKhr);
        }

        Console.Error.WriteLine(value: $"[present-timing] VK_KHR_present_wait/present_id {(presentTiming
            ? "ENABLED (closed-loop pacing)"
            : "unavailable (open-loop pacing)")}");

        AppendCapabilityFloorFeatures(
            featureStructureTypes: featureStructureTypes,
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        );

        return (extensions, featureStructureTypes);
    }
    private IReadOnlyList<uint> ComposeFeatureIndices(VulkanInstanceCommands instance, nint physicalDeviceHandle) {
        var support = m_physicalDeviceApi.GetFeatureSupport(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        );
        var featureIndices = new List<uint>();

        foreach (var index in OptionalBaseFeatureIndices) {
            if (
                (index < support.Count) &&
                support[((int)index)]
            ) {
                featureIndices.Add(item: index);
            }
        }

        return featureIndices;
    }
    private VkQueue CreateQueue(
        VulkanDeviceCommands device,
        uint queueFamilyIndex
    ) {
        var queueHandle = m_logicalDeviceApi.GetDeviceQueue(
            device: device,
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
    private bool SupportsPipelineExecutableProperties(VulkanInstanceCommands instance, nint physicalDeviceHandle) {
        return (
            m_physicalDeviceApi.HasDeviceExtension(
            extensionName: PipelineExecutablePropertiesExtension,
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        ) &&
            m_physicalDeviceApi.IsExtensionFeatureSupported(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle,
            structureType: StructureTypePhysicalDevicePipelineExecutablePropertiesFeaturesKhr
        )
        );
    }
    /// <summary>Whether the device supports the full present-timing bundle (both extensions AND both features), so the
    /// host pacer can phase-lock to actual present times. Falls back to open-loop pacing when any piece is missing.</summary>
    private bool SupportsPresentTiming(VulkanInstanceCommands instance, nint physicalDeviceHandle) {
        foreach (var extension in PresentTimingExtensions) {
            if (!m_physicalDeviceApi.HasDeviceExtension(
                extensionName: extension,
                instance: instance,
                physicalDeviceHandle: physicalDeviceHandle
            )) {
                return false;
            }
        }

        return (
            m_physicalDeviceApi.IsExtensionFeatureSupported(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle,
            structureType: StructureTypePhysicalDevicePresentIdFeaturesKhr
        ) &&
            m_physicalDeviceApi.IsExtensionFeatureSupported(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle,
            structureType: StructureTypePhysicalDevicePresentWaitFeaturesKhr
        )
        );
    }

    /// <summary>Refuses, by name, a device that cannot bind the grouped binding contract: fewer descriptor sets than
    /// its <see cref="GpuPipelineLayoutDescription.GroupCount"/> groups, or a push-constant range smaller than its
    /// <see cref="GpuPipelineLayoutDescription.PushIndexBytes"/>-byte pushed index. Every supported device reports far
    /// more of each.</summary>
    /// <param name="capabilities">The physical device's capabilities, read before the device is created.</param>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is <see langword="null"/>.</exception>
    /// <exception cref="GpuDeviceUnavailableException">The device reports fewer descriptor sets or push-constant bytes
    /// than the contract binds; the message names each limit it misses.</exception>
    public static void RequireGroupedBinding(GpuDeviceCapabilities capabilities) {
        ArgumentNullException.ThrowIfNull(argument: capabilities);

        var missing = new List<string>(capacity: 2);

        if (capabilities.MaxBoundDescriptorSets < GpuPipelineLayoutDescription.GroupCount) {
            missing.Add(item: $"maxBoundDescriptorSets is {capabilities.MaxBoundDescriptorSets}, below the {GpuPipelineLayoutDescription.GroupCount} binding groups");
        }
        if (capabilities.MaxPushConstantBytes < GpuPipelineLayoutDescription.PushIndexBytes) {
            missing.Add(item: $"maxPushConstantsSize is {capabilities.MaxPushConstantBytes}, below the {GpuPipelineLayoutDescription.PushIndexBytes}-byte pushed index");
        }
        if (missing.Count != 0) {
            throw VulkanResultExtensions.Unavailable(reason: $"The Vulkan device cannot bind Puck's grouped binding contract: {string.Join(separator: "; ", values: missing)}.");
        }
    }
    /// <inheritdoc/>
    public VulkanLogicalDevice Create(
        VulkanInstance instance,
        VkPhysicalDevice physicalDevice
    ) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        // Read before the device exists, so a failing query leaves nothing to destroy.
        var identity = m_physicalDeviceApi.GetDeviceIdentity(
            instance: instance.Commands,
            physicalDeviceHandle: physicalDevice.Handle
        );
        var memoryProfile = m_physicalDeviceApi.GetMemoryProfile(
            instance: instance.Commands,
            physicalDeviceHandle: physicalDevice.Handle
        );
        var capabilities = m_physicalDeviceApi.GetDeviceCapabilities(
            instance: instance.Commands,
            physicalDeviceHandle: physicalDevice.Handle
        );

        RequireGroupedBinding(capabilities: capabilities);

        var (extensionNames, featureStructureTypes) = ComposeExtensionsAndFeatures(
            instance: instance.Commands,
            physicalDeviceHandle: physicalDevice.Handle
        );
        var featureIndices = ComposeFeatureIndices(
            instance: instance.Commands,
            physicalDeviceHandle: physicalDevice.Handle
        );
        var request = new VulkanLogicalDeviceCreateRequest(
            EnabledFeatureIndices: featureIndices,
            EnabledFeatureStructureTypes: featureStructureTypes,
            ExtensionNames: extensionNames,
            Instance: instance.Commands,
            PhysicalDevice: physicalDevice,
            Queues: BuildQueues(queueFamilySelection: physicalDevice.QueueFamilySelection)
        );
        var result = m_logicalDeviceApi.CreateLogicalDevice(
            device: out var device,
            request: request
        );

        // A driver refusing the device (missing features or extensions, an initialization failure) leaves this host
        // without a usable device.
        result.ThrowIfUnavailable(operation: "vkCreateDevice");

        if (device is null) {
            throw VulkanResultExtensions.Unavailable(reason: "vkCreateDevice returned success without a valid device handle.");
        }

        VulkanPipelineCache? pipelineCache = null;

        // From here the device is live and no owner holds it yet: whatever fails destroys what this call created,
        // the pipeline cache before the device, before the failure propagates.
        try {
            var graphicsQueue = CreateQueue(
                device: device,
                queueFamilyIndex: physicalDevice.QueueFamilySelection.GraphicsFamilyIndex
            );
            var presentQueue = (physicalDevice.QueueFamilySelection.UsesSingleQueueFamily
                ? graphicsQueue
                : CreateQueue(
                    device: device,
                    queueFamilyIndex: physicalDevice.QueueFamilySelection.PresentFamilyIndex
                )
            );

            pipelineCache = VulkanPipelineCache.Create(
                device: device,
                file: GpuPipelineCacheFile.Open(
                    identity: identity,
                    store: m_pipelineCacheStore,
                    work: m_pipelineCacheWork
                ),
                identity: identity
            );

            return new(
                device: device,
                graphicsQueue: graphicsQueue,
                logicalDeviceApi: m_logicalDeviceApi,
                physicalDevice: physicalDevice,
                presentQueue: presentQueue
            ) {
                Capabilities = capabilities,
                Identity = identity,
                MemoryProfile = memoryProfile,
                PipelineCache = pipelineCache,
                SamplesBlockCompression = featureIndices.Contains(value: TextureCompressionBcFeatureIndex),
            };
        } catch {
            pipelineCache?.Dispose();
            m_logicalDeviceApi.DestroyDevice(device: device);

            throw;
        }
    }

    /// <summary>Initializes a new instance of the <see cref="VulkanLogicalDeviceFactory"/> class.</summary>
    /// <param name="logicalDeviceApi">The logical-device API used to create the device and retrieve its queues.</param>
    /// <param name="physicalDeviceApi">The physical-device API used to probe optional extension and feature support.</param>
    /// <param name="pipelineCacheWork">The backend's pipeline counts, which every device's pipeline cache adds to.</param>
    /// <param name="pipelineCacheStore">Where each device's pipeline cache lives on disk, or <see langword="null"/> to
    /// keep every cache in memory only.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logicalDeviceApi"/>, <paramref name="physicalDeviceApi"/>
    /// or <paramref name="pipelineCacheWork"/> is <see langword="null"/>.</exception>
    public VulkanLogicalDeviceFactory(
        IVulkanLogicalDeviceApi logicalDeviceApi,
        IVulkanPhysicalDeviceApi physicalDeviceApi,
        GpuPipelineCacheWork pipelineCacheWork,
        GpuPipelineCacheStore? pipelineCacheStore
    ) {
        ArgumentNullException.ThrowIfNull(argument: logicalDeviceApi);
        ArgumentNullException.ThrowIfNull(argument: physicalDeviceApi);
        ArgumentNullException.ThrowIfNull(argument: pipelineCacheWork);

        m_logicalDeviceApi = logicalDeviceApi;
        m_physicalDeviceApi = physicalDeviceApi;
        m_pipelineCacheStore = pipelineCacheStore;
        m_pipelineCacheWork = pipelineCacheWork;
    }
}
