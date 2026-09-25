using System.Globalization;
using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi;

namespace Puck.DirectX.Apis;

/// <summary>Answers <c>ID3D12Device::CheckFeatureSupport</c> queries with their <c>HRESULT</c>. A runtime that does
/// not know a feature, a root signature version or a shader model answers <c>E_INVALIDARG</c>, which is an answer,
/// never an exception.</summary>
public unsafe interface IDirectXFeatureSupport {
    /// <summary>Asks one feature query.</summary>
    /// <param name="feature">The feature asked about.</param>
    /// <param name="data">The feature's data structure, read for the query's inputs and filled on success.</param>
    /// <param name="size">The size, in bytes, of the structure <paramref name="data"/> points to.</param>
    /// <returns>The query's result.</returns>
    HRESULT CheckFeatureSupport(D3D12_FEATURE feature, void* data, uint size);
}
/// <summary>A device's own feature queries, through the <c>ID3D12Device::CheckFeatureSupport</c> vtable slot
/// (<see cref="DirectXConstants.CheckFeatureSupportSlot"/>) rather than the generated wrapper, which throws on the
/// result a query about an unknown feature returns.</summary>
/// <param name="device">The device asked; it stays owned by the caller.</param>
[SupportedOSPlatform("windows8.1")]
public readonly unsafe struct DirectXDeviceFeatureSupport(ID3D12Device* device) : IDirectXFeatureSupport {
    /// <inheritdoc/>
    public HRESULT CheckFeatureSupport(D3D12_FEATURE feature, void* data, uint size) {
        var vtable = *((void***)device);

        return ((delegate* unmanaged[Stdcall]<ID3D12Device*, D3D12_FEATURE, void*, uint, HRESULT>)vtable[DirectXConstants.CheckFeatureSupportSlot])(
            device,
            feature,
            data,
            size
        );
    }
}
/// <summary>
/// Reads what a Direct3D 12 device reports through its feature queries: the highest feature level, the capabilities,
/// the memory profile and whether it reaches a shader model. Each is a pure function of the queries' answers, and each
/// query a runtime may not know has a documented fallback that runs when the query answers a failure.
/// </summary>
[SupportedOSPlatform("windows8.1")]
public static unsafe class DirectXFeatureReads {
    // Probed highest-first: the first level that accepts device creation is the adapter's maximum, and the
    // feature-levels query is asked about the same list.
    internal static readonly DirectXFeatureLevel[] FeatureLevelsHighToLow = [
        DirectXFeatureLevel.Level122,
        DirectXFeatureLevel.Level121,
        DirectXFeatureLevel.Level120,
        DirectXFeatureLevel.Level111,
        DirectXFeatureLevel.Level110,
    ];

    /// <summary>Asks one feature query over a structure.</summary>
    /// <typeparam name="TSupport">The query's answerer.</typeparam>
    /// <typeparam name="TData">The feature's data structure.</typeparam>
    /// <param name="support">The answerer.</param>
    /// <param name="feature">The feature asked about.</param>
    /// <param name="data">The structure, holding the query's inputs; filled when the query succeeds.</param>
    /// <returns><see langword="true"/> when the query succeeded.</returns>
    public static bool TryCheck<TSupport, TData>(TSupport support, D3D12_FEATURE feature, ref TData data)
        where TSupport : IDirectXFeatureSupport
        where TData : unmanaged {
        fixed (TData* pointer = &data) {
            return support.CheckFeatureSupport(
                data: pointer,
                feature: feature,
                size: ((uint)sizeof(TData))
            ).Succeeded;
        }
    }
    /// <summary>Returns the highest feature level a device supports, as <c>&lt;major&gt;_&lt;minor&gt;</c>
    /// (<c>12_1</c>).</summary>
    /// <typeparam name="TSupport">The query's answerer.</typeparam>
    /// <param name="support">The device's feature queries.</param>
    /// <returns>The level; empty when the feature-levels query fails.</returns>
    public static string MaxFeatureLevel<TSupport>(TSupport support) where TSupport : IDirectXFeatureSupport {
        var requested = stackalloc D3D_FEATURE_LEVEL[FeatureLevelsHighToLow.Length];

        for (var index = 0; (index < FeatureLevelsHighToLow.Length); index++) {
            requested[index] = ((D3D_FEATURE_LEVEL)FeatureLevelsHighToLow[index]);
        }

        var levels = new D3D12_FEATURE_DATA_FEATURE_LEVELS {
            NumFeatureLevels = ((uint)FeatureLevelsHighToLow.Length),
            pFeatureLevelsRequested = requested,
        };

        if (!TryCheck(
            data: ref levels,
            feature: D3D12_FEATURE.D3D12_FEATURE_FEATURE_LEVELS,
            support: support
        )) {
            return string.Empty;
        }

        var level = ((uint)levels.MaxSupportedFeatureLevel);

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{(level >> 12)}_{((level >> 8) & 0xFU)}"
        );
    }
    /// <summary>Reads what a device can bind: options' resource binding tier, the highest root signature version and
    /// shader model, and options 19's shader-visible heap sizes. A runtime that does not answer options 19 reports the
    /// heap sizes every binding tier guarantees; one that knows no root signature version or shader model the query
    /// asks reports that field empty.</summary>
    /// <typeparam name="TSupport">The query's answerer.</typeparam>
    /// <param name="support">The device's feature queries.</param>
    /// <returns>The capabilities.</returns>
    /// <exception cref="DirectXException">The options query, which every Direct3D 12 runtime answers, failed.</exception>
    public static GpuDeviceCapabilities Capabilities<TSupport>(TSupport support) where TSupport : IDirectXFeatureSupport {
        var options = new D3D12_FEATURE_DATA_D3D12_OPTIONS();
        var options19 = new D3D12_FEATURE_DATA_D3D12_OPTIONS19();

        support.CheckFeatureSupport(
            data: &options,
            feature: D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS,
            size: ((uint)sizeof(D3D12_FEATURE_DATA_D3D12_OPTIONS))
        ).ThrowIfFailed(operation: "ID3D12Device::CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS)");

        if (!TryCheck(
            data: ref options19,
            feature: D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS19,
            support: support
        )) {
            options19 = default;
        }

        return GpuDeviceCapabilities.FromDirectX(
            resourceBindingTier: ((uint)options.ResourceBindingTier),
            rootSignatureVersion: HighestRootSignatureVersion(support: support),
            samplerHeapSize: options19.MaxSamplerDescriptorHeapSize,
            shaderModel: HighestShaderModel(support: support),
            viewHeapSize: options19.MaxViewDescriptorHeapSize
        );
    }
    /// <summary>Reads a device's memory profile. A device that does not answer the architecture query reports the
    /// default profile, which selects the staged copy; one that does not answer options 16 predates GPU upload
    /// heaps.</summary>
    /// <typeparam name="TSupport">The query's answerer.</typeparam>
    /// <param name="support">The device's feature queries.</param>
    /// <param name="adapter">The device's adapter's <c>DXGI_ADAPTER_DESC1</c>.</param>
    /// <returns>The profile.</returns>
    public static GpuMemoryProfile MemoryProfile<TSupport>(TSupport support, in DXGI_ADAPTER_DESC1 adapter) where TSupport : IDirectXFeatureSupport {
        var architecture = new D3D12_FEATURE_DATA_ARCHITECTURE();
        var options16 = new D3D12_FEATURE_DATA_D3D12_OPTIONS16();

        if (!TryCheck(
            data: ref architecture,
            feature: D3D12_FEATURE.D3D12_FEATURE_ARCHITECTURE,
            support: support
        )) {
            return default;
        }

        if (!TryCheck(
            data: ref options16,
            feature: D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS16,
            support: support
        )) {
            options16 = default;
        }

        return DirectXNativeDeviceApi.MemoryProfile(
            adapter: in adapter,
            architecture: in architecture,
            options16: in options16
        );
    }
    /// <summary>Returns whether a device reaches a shader model. The query lowers the model it is asked about to the
    /// highest the device supports, and a runtime too old to know the asked model fails it, which reads as below.</summary>
    /// <typeparam name="TSupport">The query's answerer.</typeparam>
    /// <param name="support">The device's feature queries.</param>
    /// <param name="required">The model the device must reach.</param>
    /// <param name="reported">The model the device reports, formatted <c>6.8</c>; empty when the query fails.</param>
    /// <returns><see langword="true"/> when the device reports <paramref name="required"/> or above.</returns>
    public static bool ReachesShaderModel<TSupport>(TSupport support, D3D_SHADER_MODEL required, out string reported) where TSupport : IDirectXFeatureSupport {
        var data = new D3D12_FEATURE_DATA_SHADER_MODEL {
            HighestShaderModel = required,
        };

        if (!TryCheck(
            data: ref data,
            feature: D3D12_FEATURE.D3D12_FEATURE_SHADER_MODEL,
            support: support
        )) {
            reported = string.Empty;

            return false;
        }

        reported = FormatShaderModel(model: data.HighestShaderModel);

        return (data.HighestShaderModel >= required);
    }

    // D3D_SHADER_MODEL packs the major version in the high nibble and the minor in the low (0x66 is 6.6).
    private static string FormatShaderModel(D3D_SHADER_MODEL model) => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"{(((int)model) >> 4)}.{(((int)model) & 0xF)}"
    );
    // The query lowers the version it is asked about to the highest the device supports; a runtime that does not know
    // the asked version fails the query, so each is asked from the top down.
    private static string HighestRootSignatureVersion<TSupport>(TSupport support) where TSupport : IDirectXFeatureSupport {
        ReadOnlySpan<D3D_ROOT_SIGNATURE_VERSION> versions = [
            D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1_2,
            D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1_1,
            D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1_0,
        ];

        foreach (var version in versions) {
            var data = new D3D12_FEATURE_DATA_ROOT_SIGNATURE {
                HighestVersion = version,
            };

            if (TryCheck(
                data: ref data,
                feature: D3D12_FEATURE.D3D12_FEATURE_ROOT_SIGNATURE,
                support: support
            )) {
                // D3D_ROOT_SIGNATURE_VERSION_1_0 is 0x1, 1_1 is 0x2 and 1_2 is 0x3.
                return string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"1.{(((int)data.HighestVersion) - 1)}"
                );
            }
        }

        return string.Empty;
    }
    private static string HighestShaderModel<TSupport>(TSupport support) where TSupport : IDirectXFeatureSupport {
        for (var model = ((int)D3D_SHADER_MODEL.D3D_SHADER_MODEL_6_9); (model >= ((int)D3D_SHADER_MODEL.D3D_SHADER_MODEL_6_0)); model--) {
            var data = new D3D12_FEATURE_DATA_SHADER_MODEL {
                HighestShaderModel = ((D3D_SHADER_MODEL)model),
            };

            if (TryCheck(
                data: ref data,
                feature: D3D12_FEATURE.D3D12_FEATURE_SHADER_MODEL,
                support: support
            )) {
                return FormatShaderModel(model: data.HighestShaderModel);
            }
        }

        return string.Empty;
    }
}
