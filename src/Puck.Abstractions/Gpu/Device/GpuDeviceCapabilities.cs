using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// What a GPU device can bind, as its backend reported it when the device was created: the limits the grouped binding
/// contract assumes — how many descriptor sets or root-signature words a pipeline may bind, how many push-constant
/// bytes, and how many descriptors of each kind one shader stage may reach — and, on Direct3D 12, the resource binding
/// tier, the highest root signature version and shader model, and the shader-visible heap sizes — and how it can back a
/// large buffer and give memory back (<see cref="Backing"/> and the native figures beside it). It is recorded beside
/// counted work, like <see cref="GpuDeviceIdentity"/>, so a reading on one device can be held to the contract's
/// assumptions. Uniform-region admission also reads its constant-buffer view limit.
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
/// <param name="MaxConstantBufferBytes">The largest constant-buffer view in bytes: Vulkan's
/// <c>maxUniformBufferRange</c> or Direct3D 12's 4,096 float4 elements.</param>
/// <param name="ResourceBindingTier">Direct3D 12's <c>ResourceBindingTier</c> (1, 2 or 3); zero on Vulkan.</param>
/// <param name="RootSignatureVersion">The highest root signature version Direct3D 12 reports (<c>1.1</c>); empty on
/// Vulkan.</param>
/// <param name="ShaderModel">The highest shader model Direct3D 12 reports (<c>6.8</c>); empty on Vulkan.</param>
/// <param name="ViewHeapSize">Direct3D 12's largest shader-visible CBV/SRV/UAV heap, in descriptors; zero on
/// Vulkan.</param>
/// <param name="SamplerHeapSize">Direct3D 12's largest shader-visible sampler heap, in descriptors; zero on
/// Vulkan.</param>
/// <param name="StaticSamplerHeapSize">Direct3D 12's largest shader-visible sampler heap a root signature with static
/// samplers may be used beside, in descriptors; zero on Vulkan.</param>
/// <param name="Backing">How the device can back a large buffer and give its memory back, and how a kernel may index an
/// array of buffers (<see cref="GpuBackingSupport"/>), the capability read the streaming plan's backing choice rests on;
/// <see langword="null"/> until a backend reads it (<see cref="WithDirectXBacking"/>, and on Vulkan the physical device's
/// features, sparse properties, queue families and the two memory extensions), so a device that offers none of it reads
/// <see cref="GpuBackingSupport.None"/> apart from one never asked.</param>
/// <param name="ReservedResourcesTier">Direct3D 12's <c>TiledResourcesTier</c> (0 for none, 1 to 4); zero on
/// Vulkan.</param>
/// <param name="ResourceAddressBits">Direct3D 12's <c>MaxGPUVirtualAddressBitsPerResource</c>, how much address one
/// reserved resource may span; zero on Vulkan.</param>
/// <param name="SparseAddressSpaceBytes">Vulkan's <c>sparseAddressSpaceSize</c>, the address space every sparse resource
/// shares; zero on Direct3D 12.</param>
/// <param name="StorageBufferOffsetAlignment">Vulkan's <c>minStorageBufferOffsetAlignment</c>, the alignment a buffer
/// view's offset into its buffer keeps; zero on Direct3D 12, whose view starts at any whole element.</param>
/// <param name="Optional">The optional capabilities the device was created with, which a kernel variant may depend on
/// (<see cref="Has"/>): each one the device reports, less what the host withheld.</param>
/// <param name="Withheld">The optional capabilities the device reports but was created without, because the host
/// withheld them (<see cref="GpuDeviceOptions.Withheld"/>).</param>
/// <param name="RaytracingTier">Direct3D 12's <c>D3D12_RAYTRACING_TIER</c> as reported (10 for 1.0, 11 for 1.1, 0 for
/// none); zero on Vulkan.</param>
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
    uint MaxConstantBufferBytes = 0U,
    uint ResourceBindingTier = 0U,
    string RootSignatureVersion = "",
    string ShaderModel = "",
    uint ViewHeapSize = 0U,
    uint SamplerHeapSize = 0U,
    uint StaticSamplerHeapSize = 0U,
    GpuBackingSupport? Backing = null,
    uint ReservedResourcesTier = 0U,
    uint ResourceAddressBits = 0U,
    ulong SparseAddressSpaceBytes = 0UL,
    uint StorageBufferOffsetAlignment = 0U,
    GpuOptionalCapabilities Optional = GpuOptionalCapabilities.None,
    GpuOptionalCapabilities Withheld = GpuOptionalCapabilities.None,
    uint RaytracingTier = 0U
) {
    /// <summary>Direct3D 12's 4,096 constant-buffer elements, each a 16-byte float4.</summary>
    public const uint DirectXConstantBufferBytes = (4096U * 16U);
    /// <summary>Direct3D 12's root signature cost limit in 32-bit words (<c>D3D12_MAX_ROOT_COST</c>).</summary>
    public const uint DirectXMaxRootSignatureWords = 64U;
    /// <summary>The CBV/SRV/UAV heap size Direct3D 12 guarantees at every binding tier
    /// (<c>D3D12_MAX_SHADER_VISIBLE_DESCRIPTOR_HEAP_SIZE_TIER_1</c>), reported when the runtime does not answer options
    /// 19.</summary>
    public const uint DirectXMinimumViewHeapSize = 1_000_000U;
    /// <summary>Direct3D 12's shader-visible sampler heap size (<c>D3D12_MAX_SHADER_VISIBLE_SAMPLER_HEAP_SIZE</c>),
    /// reported for both sampler heap limits when the runtime does not answer options 19.</summary>
    public const uint DirectXMinimumSamplerHeapSize = 2048U;
    /// <summary>The name of the line and JSON object the capabilities form in a readout.</summary>
    public const string Section = "capabilities";

    // Each field's one spelling, shared by the text form and the JSON form.
    private const string BackendField = "backend";
    private const string BindingTierField = "binding-tier";
    private const string ConstantBufferBytesField = "constant-buffer-bytes";
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
    private const string StaticSamplerHeapField = "heap.samplers-static";
    private const string ViewHeapField = "heap.views";
    private const string BackingField = "backing";
    private const string ReservedTierField = "reserved.tier";
    private const string ResourceAddressBitsField = "reserved.address-bits";
    private const string SparseAddressSpaceField = "sparse.address-space";
    private const string StorageBufferOffsetAlignmentField = "storage-buffer.offset-alignment";
    private const string OptionalField = "optional";
    private const string WithheldField = "withheld";
    private const string RaytracingTierField = "raytracing-tier";

    /// <summary>Returns whether the device was created with every optional capability a kernel variant needs.</summary>
    /// <param name="required">The capabilities the variant needs; <see cref="GpuOptionalCapabilities.None"/> is always
    /// met.</param>
    /// <returns><see langword="true"/> when <see cref="Optional"/> holds every flag of <paramref name="required"/>.</returns>
    public bool Has(GpuOptionalCapabilities required) => ((Optional & required) == required);
    /// <summary>Returns these capabilities with the optional capabilities the device reports split into what it is created
    /// with and what the host withholds. A pure function of its arguments.</summary>
    /// <param name="reported">The optional capabilities the device reports.</param>
    /// <param name="withheld">The capabilities the host withholds.</param>
    /// <returns>The capabilities with <see cref="Optional"/> and <see cref="Withheld"/> filled.</returns>
    public GpuDeviceCapabilities WithOptional(GpuOptionalCapabilities reported, GpuOptionalCapabilities withheld) =>
        (this with { Optional = (reported & ~withheld), Withheld = (reported & withheld) });

    /// <summary>Returns these capabilities with what a Direct3D 12 device reports about backing. A reserved resource
    /// (<c>CreateReservedResource</c>) at tiled resources tier 1 or above is a buffer whose tiles are mapped and unmapped
    /// on any queue (<c>UpdateTileMappings</c>): sparse binding, buffer residency and a queue that binds; tier 2 reads an
    /// unmapped tile as zero, which is Vulkan's strict non-residency. Every heap is paged by the operating system's video
    /// memory manager (<c>MakeResident</c>, <c>Evict</c>), so device-local memory is always pageable; and a shader model
    /// 5.1 kernel may index an array of views by a dynamic or, through <c>NonUniformResourceIndex</c>, a non-uniform
    /// value at every binding tier. A pure function of its arguments.</summary>
    /// <param name="tiledResourcesTier">Options' <c>TiledResourcesTier</c>, 0 to 4.</param>
    /// <param name="resourceAddressBits">The GPU virtual address support query's
    /// <c>MaxGPUVirtualAddressBitsPerResource</c>, or zero when the runtime does not answer it.</param>
    /// <param name="residencyPriority">Whether the device implements <c>ID3D12Device1</c>, whose
    /// <c>SetResidencyPriority</c> passes a priority to the memory manager.</param>
    /// <param name="enqueueMakeResident">Whether the device implements <c>ID3D12Device3</c>, whose
    /// <c>EnqueueMakeResident</c> makes memory resident without blocking and can refuse past the budget.</param>
    /// <returns>The capabilities with <see cref="Backing"/>, <see cref="ReservedResourcesTier"/> and
    /// <see cref="ResourceAddressBits"/> filled.</returns>
    public GpuDeviceCapabilities WithDirectXBacking(uint tiledResourcesTier, uint resourceAddressBits, bool residencyPriority, bool enqueueMakeResident) {
        var backing = (GpuBackingSupport.PageableDeviceLocal | GpuBackingSupport.BufferArrayDynamicIndexing | GpuBackingSupport.BufferArrayNonUniformIndexing);

        if (tiledResourcesTier >= 1U) { backing |= (GpuBackingSupport.SparseBinding | GpuBackingSupport.SparseResidencyBuffer | GpuBackingSupport.SparseQueue); }
        if (tiledResourcesTier >= 2U) { backing |= GpuBackingSupport.SparseNonResidentStrict; }
        if (residencyPriority) { backing |= GpuBackingSupport.MemoryPriority; }
        if (enqueueMakeResident) { backing |= GpuBackingSupport.EnqueueMakeResident; }

        return (this with { Backing = backing, ReservedResourcesTier = tiledResourcesTier, ResourceAddressBits = resourceAddressBits });
    }
    /// <summary>Returns the backing flags as they print: each set flag's name joined by <c>+</c> in declaration order, or
    /// <c>none</c>.</summary>
    /// <param name="backing">The flags.</param>
    /// <returns>The spelling.</returns>
    public static string BackingSpelling(GpuBackingSupport backing) {
        if (backing == GpuBackingSupport.None) { return "none"; }

        var names = new List<string>();

        foreach (var (flag, name) in BackingNames) {
            if ((backing & flag) != 0) { names.Add(item: name); }
        }

        return string.Join(separator: '+', values: names);
    }

    // Each backing flag's one spelling, in declaration order.
    private static readonly (GpuBackingSupport Flag, string Name)[] BackingNames = [
        (GpuBackingSupport.SparseBinding, "sparse-binding"),
        (GpuBackingSupport.SparseResidencyBuffer, "sparse-residency-buffer"),
        (GpuBackingSupport.SparseResidencyAliased, "sparse-residency-aliased"),
        (GpuBackingSupport.SparseQueue, "sparse-queue"),
        (GpuBackingSupport.SparseNonResidentStrict, "sparse-non-resident-strict"),
        (GpuBackingSupport.MemoryPriority, "memory-priority"),
        (GpuBackingSupport.PageableDeviceLocal, "pageable-device-local"),
        (GpuBackingSupport.EnqueueMakeResident, "enqueue-make-resident"),
        (GpuBackingSupport.BufferArrayDynamicIndexing, "buffer-array-dynamic"),
        (GpuBackingSupport.BufferArrayNonUniformIndexing, "buffer-array-non-uniform"),
    ];

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
    /// <param name="staticSamplerHeapSize">Options 19's <c>MaxSamplerDescriptorHeapSizeWithStaticSamplers</c>, or zero
    /// when the runtime does not answer, which reports <see cref="DirectXMinimumSamplerHeapSize"/>.</param>
    /// <returns>The capabilities.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="resourceBindingTier"/> is not 1, 2 or 3.</exception>
    public static GpuDeviceCapabilities FromDirectX(uint resourceBindingTier, string rootSignatureVersion, string shaderModel, uint viewHeapSize, uint samplerHeapSize, uint staticSamplerHeapSize) {
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
        var staticSamplers = ((staticSamplerHeapSize == 0U)
            ? DirectXMinimumSamplerHeapSize
            : staticSamplerHeapSize
        );

        var (stageSamplers, stageConstantBuffers, stageShaderResources, unorderedAccess) = resourceBindingTier switch {
            1U => (16U, 14U, 128U, 64U),
            2U => (samplers, 14U, views, 64U),
            _ => (samplers, views, views, views),
        };

        return new GpuDeviceCapabilities(
            Backend: "directx",
            MaxBoundDescriptorSets: 0U,
            MaxConstantBufferBytes: DirectXConstantBufferBytes,
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
            StaticSamplerHeapSize: staticSamplers,
            ViewHeapSize: views
        );
    }
    /// <summary>Appends the capabilities as <c>key=value</c> fields on one line, an empty or zero backend-specific
    /// field left out:
    /// <c>backend=vulkan descriptor-sets=32 push-constant-bytes=256 stage.samplers=… stage.uniform-buffers=… stage.storage-buffers=… stage.sampled-images=… stage.storage-images=… stage.resources=…</c>,
    /// and on Direct3D 12 <c>root-signature-words=64 … binding-tier=3 root-signature=1.1 shader-model=6.8 heap.views=… heap.samplers=… heap.samplers-static=…</c>;
    /// then <c>backing=</c> (<see cref="BackingSpelling"/>) once a backend has read it, and the nonzero of <c>reserved.tier</c>,
    /// <c>reserved.address-bits</c>, <c>sparse.address-space</c> and <c>storage-buffer.offset-alignment</c>; then
/// <c>optional=</c> (<see cref="GpuOptionalCapabilitiesSpelling.Of"/>), <c>withheld=</c> when the host withheld any, and
/// a nonzero <c>raytracing-tier</c>.</summary>
    /// <param name="builder">The text to append to.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public StringBuilder AppendFields(StringBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.Append(value: ' ').Append(value: BackendField).Append(value: '=').Append(value: Backend);
        AppendCount(builder: builder, name: ConstantBufferBytesField, value: MaxConstantBufferBytes);
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
        AppendCount(
            builder: builder,
            name: StaticSamplerHeapField,
            value: StaticSamplerHeapSize
        );
        if (Backing is { } backing) {
            _ = builder.Append(value: ' ').Append(value: BackingField).Append(value: '=').Append(value: BackingSpelling(backing: backing));
        }
        AppendCount(builder: builder, name: ReservedTierField, value: ReservedResourcesTier);
        AppendCount(builder: builder, name: ResourceAddressBitsField, value: ResourceAddressBits);
        if (SparseAddressSpaceBytes != 0UL) {
            _ = builder.Append(value: ' ').Append(value: SparseAddressSpaceField).Append(value: '=').Append(provider: CultureInfo.InvariantCulture, handler: $"{SparseAddressSpaceBytes}");
        }
        AppendCount(builder: builder, name: StorageBufferOffsetAlignmentField, value: StorageBufferOffsetAlignment);
        _ = builder.Append(value: ' ').Append(value: OptionalField).Append(value: '=').Append(value: GpuOptionalCapabilitiesSpelling.Of(capabilities: Optional));
        if (Withheld != GpuOptionalCapabilities.None) {
            _ = builder.Append(value: ' ').Append(value: WithheldField).Append(value: '=').Append(value: GpuOptionalCapabilitiesSpelling.Of(capabilities: Withheld));
        }
        AppendCount(builder: builder, name: RaytracingTierField, value: RaytracingTier);

        return builder;
    }
    /// <summary>Writes the capabilities as one JSON object with every field, under the names
    /// <see cref="AppendFields"/> prints.</summary>
    /// <param name="writer">The writer to write to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public void WriteJson(Utf8JsonWriter writer) {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(propertyName: ConstantBufferBytesField, value: MaxConstantBufferBytes);
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
        writer.WriteNumber(
            propertyName: StaticSamplerHeapField,
            value: StaticSamplerHeapSize
        );
        if (Backing is { } backing) {
            writer.WriteString(propertyName: BackingField, value: BackingSpelling(backing: backing));
        } else {
            writer.WriteNull(propertyName: BackingField);
        }
        writer.WriteNumber(propertyName: ReservedTierField, value: ReservedResourcesTier);
        writer.WriteNumber(propertyName: ResourceAddressBitsField, value: ResourceAddressBits);
        writer.WriteNumber(propertyName: SparseAddressSpaceField, value: SparseAddressSpaceBytes);
        writer.WriteNumber(propertyName: StorageBufferOffsetAlignmentField, value: StorageBufferOffsetAlignment);
        writer.WriteString(propertyName: OptionalField, value: GpuOptionalCapabilitiesSpelling.Of(capabilities: Optional));
        writer.WriteString(propertyName: WithheldField, value: GpuOptionalCapabilitiesSpelling.Of(capabilities: Withheld));
        writer.WriteNumber(propertyName: RaytracingTierField, value: RaytracingTier);
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
