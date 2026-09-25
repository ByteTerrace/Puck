using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// What a GPU device can bind, as its backend reported it when the device was created: the limits the grouped binding
/// contract assumes — how many descriptor sets or root-signature words a pipeline may bind, how many push-constant
/// bytes, and how many descriptors of each kind one shader stage may reach — and, on Direct3D 12, the resource binding
/// tier, the highest root signature version and shader model, and the shader-visible heap sizes. It is recorded beside
/// counted work, like <see cref="GpuDeviceIdentity"/>, so a reading on one device can be held to the contract's
/// assumptions; it is never branched on.
/// </summary>
/// <param name="Backend">The backend's name: <c>vulkan</c> or <c>directx</c>.</param>
/// <param name="MaxBoundDescriptorSets">Vulkan's <c>maxBoundDescriptorSets</c>; zero on Direct3D 12, whose tables are
/// bounded by <paramref name="MaxRootSignatureWords"/> instead.</param>
/// <param name="MaxRootSignatureWords">Direct3D 12's root signature cost limit in 32-bit words
/// (<c>D3D12_MAX_ROOT_COST</c>): a descriptor table costs one, a root descriptor two, a root constant one. Zero on
/// Vulkan.</param>
/// <param name="MaxPushConstantBytes">Vulkan's <c>maxPushConstantsSize</c>; on Direct3D 12 the whole root signature
/// spent on root constants (<c>D3D12_MAX_ROOT_COST</c> words).</param>
/// <param name="MaxPerStageSamplers">Vulkan's <c>maxPerStageDescriptorSamplers</c>; on Direct3D 12 the samplers one
/// stage may reach at <paramref name="ResourceBindingTier"/>.</param>
/// <param name="MaxPerStageUniformBuffers">Vulkan's <c>maxPerStageDescriptorUniformBuffers</c>; on Direct3D 12 the
/// constant buffer views one stage may reach at the tier.</param>
/// <param name="MaxPerStageStorageBuffers">Vulkan's <c>maxPerStageDescriptorStorageBuffers</c>; on Direct3D 12 the
/// unordered access views the tier allows.</param>
/// <param name="MaxPerStageSampledImages">Vulkan's <c>maxPerStageDescriptorSampledImages</c>; on Direct3D 12 the shader
/// resource views one stage may reach at the tier.</param>
/// <param name="MaxPerStageStorageImages">Vulkan's <c>maxPerStageDescriptorStorageImages</c>; on Direct3D 12 the
/// unordered access views the tier allows.</param>
/// <param name="MaxPerStageResources">Vulkan's <c>maxPerStageResources</c>; zero on Direct3D 12, which has no such
/// limit.</param>
/// <param name="ResourceBindingTier">Direct3D 12's <c>ResourceBindingTier</c> (1, 2 or 3); zero on Vulkan.</param>
/// <param name="RootSignatureVersion">The highest root signature version Direct3D 12 reports (<c>1.1</c>); empty on
/// Vulkan.</param>
/// <param name="ShaderModel">The highest shader model Direct3D 12 reports (<c>6.8</c>); empty on Vulkan.</param>
/// <param name="ViewHeapSize">Direct3D 12's largest shader-visible CBV/SRV/UAV heap, in descriptors; zero on
/// Vulkan.</param>
/// <param name="SamplerHeapSize">Direct3D 12's largest shader-visible sampler heap, in descriptors; zero on
/// Vulkan.</param>
public sealed record GpuDeviceCapabilities(
    string Backend,
    uint MaxBoundDescriptorSets,
    uint MaxRootSignatureWords,
    uint MaxPushConstantBytes,
    uint MaxPerStageSamplers,
    uint MaxPerStageUniformBuffers,
    uint MaxPerStageStorageBuffers,
    uint MaxPerStageSampledImages,
    uint MaxPerStageStorageImages,
    uint MaxPerStageResources,
    uint ResourceBindingTier = 0U,
    string RootSignatureVersion = "",
    string ShaderModel = "",
    uint ViewHeapSize = 0U,
    uint SamplerHeapSize = 0U
) {
    /// <summary>Direct3D 12's root signature cost limit in 32-bit words (<c>D3D12_MAX_ROOT_COST</c>).</summary>
    public const uint DirectXMaxRootSignatureWords = 64U;
    /// <summary>The CBV/SRV/UAV heap size Direct3D 12 guarantees at every binding tier
    /// (<c>D3D12_MAX_SHADER_VISIBLE_DESCRIPTOR_HEAP_SIZE_TIER_1</c>), reported when the runtime does not answer options
    /// 19.</summary>
    public const uint DirectXMinimumViewHeapSize = 1_000_000U;
    /// <summary>Direct3D 12's shader-visible sampler heap size (<c>D3D12_MAX_SHADER_VISIBLE_SAMPLER_HEAP_SIZE</c>),
    /// reported when the runtime does not answer options 19.</summary>
    public const uint DirectXMinimumSamplerHeapSize = 2048U;
    /// <summary>The name of the line and JSON object the capabilities form in a readout.</summary>
    public const string Section = "capabilities";

    // Each field's one spelling, shared by the text form and the JSON form.
    private const string BackendField = "backend";
    private const string BindingTierField = "binding-tier";
    private const string DescriptorSetsField = "descriptor-sets";
    private const string PushConstantBytesField = "push-constant-bytes";
    private const string RootSignatureField = "root-signature";
    private const string RootSignatureWordsField = "root-signature-words";
    private const string SamplerHeapField = "heap.samplers";
    private const string ShaderModelField = "shader-model";
    private const string StageResourcesField = "stage.resources";
    private const string StageSampledImagesField = "stage.sampled-images";
    private const string StageSamplersField = "stage.samplers";
    private const string StageStorageBuffersField = "stage.storage-buffers";
    private const string StageStorageImagesField = "stage.storage-images";
    private const string StageUniformBuffersField = "stage.uniform-buffers";
    private const string ViewHeapField = "heap.views";

    /// <summary>Fills Direct3D 12's capabilities from what the device reports. The per-stage limits follow the resource
    /// binding tier's documented table: tier 1 reaches 16 samplers, 14 constant buffer views, 128 shader resource views
    /// and 64 unordered access views (every Puck device is at feature level 11_1 or above); tier 2 reaches the whole
    /// sampler heap and shader resource views up to the whole view heap; tier 3 reaches the whole view heap for every
    /// view kind. A pure function of its arguments.</summary>
    /// <param name="resourceBindingTier">Options' <c>ResourceBindingTier</c>: 1, 2 or 3.</param>
    /// <param name="rootSignatureVersion">The highest root signature version the device reports.</param>
    /// <param name="shaderModel">The highest shader model the device reports.</param>
    /// <param name="viewHeapSize">Options 19's <c>MaxViewDescriptorHeapSize</c>, or zero when the runtime does not
    /// answer, which reports <see cref="DirectXMinimumViewHeapSize"/>.</param>
    /// <param name="samplerHeapSize">Options 19's <c>MaxSamplerDescriptorHeapSize</c>, or zero when the runtime does not
    /// answer, which reports <see cref="DirectXMinimumSamplerHeapSize"/>.</param>
    /// <returns>The capabilities.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="resourceBindingTier"/> is not 1, 2 or 3.</exception>
    public static GpuDeviceCapabilities FromDirectX(uint resourceBindingTier, string rootSignatureVersion, string shaderModel, uint viewHeapSize, uint samplerHeapSize) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1U,
            value: resourceBindingTier
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: 3U,
            value: resourceBindingTier
        );

        var views = ((viewHeapSize == 0U)
            ? DirectXMinimumViewHeapSize
            : viewHeapSize
        );
        var samplers = ((samplerHeapSize == 0U)
            ? DirectXMinimumSamplerHeapSize
            : samplerHeapSize
        );
        var (stageSamplers, stageConstantBuffers, stageShaderResources, unorderedAccess) = resourceBindingTier switch {
            1U => (16U, 14U, 128U, 64U),
            2U => (samplers, 14U, views, 64U),
            _ => (samplers, views, views, views),
        };

        return new GpuDeviceCapabilities(
            Backend: "directx",
            MaxBoundDescriptorSets: 0U,
            MaxPerStageResources: 0U,
            MaxPerStageSampledImages: stageShaderResources,
            MaxPerStageSamplers: stageSamplers,
            MaxPerStageStorageBuffers: unorderedAccess,
            MaxPerStageStorageImages: unorderedAccess,
            MaxPerStageUniformBuffers: stageConstantBuffers,
            MaxPushConstantBytes: (DirectXMaxRootSignatureWords * sizeof(uint)),
            MaxRootSignatureWords: DirectXMaxRootSignatureWords,
            ResourceBindingTier: resourceBindingTier,
            RootSignatureVersion: rootSignatureVersion,
            SamplerHeapSize: samplers,
            ShaderModel: shaderModel,
            ViewHeapSize: views
        );
    }

    /// <summary>Appends the capabilities as <c>key=value</c> fields on one line, an empty or zero backend-specific
    /// field left out:
    /// <c>backend=vulkan descriptor-sets=32 push-constant-bytes=256 stage.samplers=… stage.uniform-buffers=… stage.storage-buffers=… stage.sampled-images=… stage.storage-images=… stage.resources=…</c>,
    /// and on Direct3D 12 <c>root-signature-words=64 … binding-tier=3 root-signature=1.1 shader-model=6.8 heap.views=… heap.samplers=…</c>.</summary>
    /// <param name="builder">The text to append to.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public StringBuilder AppendFields(StringBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.Append(value: ' ').Append(value: BackendField).Append(value: '=').Append(value: Backend);
        AppendCount(
            builder: builder,
            name: DescriptorSetsField,
            value: MaxBoundDescriptorSets
        );
        AppendCount(
            builder: builder,
            name: RootSignatureWordsField,
            value: MaxRootSignatureWords
        );
        AppendCount(
            builder: builder,
            name: PushConstantBytesField,
            value: MaxPushConstantBytes
        );
        AppendCount(
            builder: builder,
            name: StageSamplersField,
            value: MaxPerStageSamplers
        );
        AppendCount(
            builder: builder,
            name: StageUniformBuffersField,
            value: MaxPerStageUniformBuffers
        );
        AppendCount(
            builder: builder,
            name: StageStorageBuffersField,
            value: MaxPerStageStorageBuffers
        );
        AppendCount(
            builder: builder,
            name: StageSampledImagesField,
            value: MaxPerStageSampledImages
        );
        AppendCount(
            builder: builder,
            name: StageStorageImagesField,
            value: MaxPerStageStorageImages
        );
        AppendCount(
            builder: builder,
            name: StageResourcesField,
            value: MaxPerStageResources
        );
        AppendCount(
            builder: builder,
            name: BindingTierField,
            value: ResourceBindingTier
        );
        AppendText(
            builder: builder,
            name: RootSignatureField,
            value: RootSignatureVersion
        );
        AppendText(
            builder: builder,
            name: ShaderModelField,
            value: ShaderModel
        );
        AppendCount(
            builder: builder,
            name: ViewHeapField,
            value: ViewHeapSize
        );
        AppendCount(
            builder: builder,
            name: SamplerHeapField,
            value: SamplerHeapSize
        );

        return builder;
    }
    /// <summary>Writes the capabilities as one JSON object with every field, under the names
    /// <see cref="AppendFields"/> prints.</summary>
    /// <param name="writer">The writer to write to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public void WriteJson(Utf8JsonWriter writer) {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteString(
            propertyName: BackendField,
            value: Backend
        );
        writer.WriteNumber(
            propertyName: DescriptorSetsField,
            value: MaxBoundDescriptorSets
        );
        writer.WriteNumber(
            propertyName: RootSignatureWordsField,
            value: MaxRootSignatureWords
        );
        writer.WriteNumber(
            propertyName: PushConstantBytesField,
            value: MaxPushConstantBytes
        );
        writer.WriteNumber(
            propertyName: StageSamplersField,
            value: MaxPerStageSamplers
        );
        writer.WriteNumber(
            propertyName: StageUniformBuffersField,
            value: MaxPerStageUniformBuffers
        );
        writer.WriteNumber(
            propertyName: StageStorageBuffersField,
            value: MaxPerStageStorageBuffers
        );
        writer.WriteNumber(
            propertyName: StageSampledImagesField,
            value: MaxPerStageSampledImages
        );
        writer.WriteNumber(
            propertyName: StageStorageImagesField,
            value: MaxPerStageStorageImages
        );
        writer.WriteNumber(
            propertyName: StageResourcesField,
            value: MaxPerStageResources
        );
        writer.WriteNumber(
            propertyName: BindingTierField,
            value: ResourceBindingTier
        );
        writer.WriteString(
            propertyName: RootSignatureField,
            value: RootSignatureVersion
        );
        writer.WriteString(
            propertyName: ShaderModelField,
            value: ShaderModel
        );
        writer.WriteNumber(
            propertyName: ViewHeapField,
            value: ViewHeapSize
        );
        writer.WriteNumber(
            propertyName: SamplerHeapField,
            value: SamplerHeapSize
        );
        writer.WriteEndObject();
    }

    // Appends " name=value" for a nonzero count; zero is a field the backend does not report.
    private static void AppendCount(StringBuilder builder, string name, uint value) {
        if (value == 0U) {
            return;
        }

        _ = builder.Append(value: ' ').Append(value: name).Append(value: '=').Append(
            provider: CultureInfo.InvariantCulture,
            handler: $"{value}"
        );
    }
    // Appends " name=value" for a nonempty value; the values are version numbers and hold no whitespace.
    private static void AppendText(StringBuilder builder, string name, string value) {
        if (string.IsNullOrEmpty(value: value)) {
            return;
        }

        _ = builder.Append(value: ' ').Append(value: name).Append(value: '=').Append(value: value);
    }
}
