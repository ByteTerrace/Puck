using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.DirectX.Apis;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Every feature query a runtime may not know falls back as documented when it answers
/// <c>E_INVALIDARG</c>, the result a runtime gives a feature, root signature version or shader model it does not
/// know (<see cref="DirectXFeatureReads"/>): options 19 reports the heap sizes every binding tier guarantees, the root
/// signature and shader model queries step down to the highest the runtime answers, the feature-levels query reports an
/// empty level, the architecture query reports the default memory profile, options 16 reports no upload heaps, and the
/// Shader Model floor reads a refused query as below. The queries are answered by a fake; no device is created.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXFeatureReadsLawTests {
    private static readonly HRESULT Answered = new(value: 0);
    private static readonly HRESULT InvalidArgument = new(value: unchecked((int)0x80070057));

    [Fact]
    public void RefusedCapabilityQueriesFallBackToWhatTheRuntimeAnswers() {
        var support = new FakeFeatureSupport(
            highestRootSignature: D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1_1,
            highestShaderModel: D3D_SHADER_MODEL.D3D_SHADER_MODEL_6_7,
            refused: [D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS19]
        );

        var capabilities = DirectXFeatureReads.Capabilities(support: support);

        Assert.Equal(
            actual: capabilities.RootSignatureVersion,
            expected: "1.1"
        );
        Assert.Equal(
            actual: capabilities.ShaderModel,
            expected: "6.7"
        );
        Assert.Equal(
            actual: capabilities.ViewHeapSize,
            expected: GpuDeviceCapabilities.DirectXMinimumViewHeapSize
        );
        Assert.Equal(
            actual: capabilities.SamplerHeapSize,
            expected: GpuDeviceCapabilities.DirectXMinimumSamplerHeapSize
        );
        Assert.Equal(
            actual: capabilities.ResourceBindingTier,
            expected: 3U
        );
    }
    [Fact]
    public void ACapabilityQueryTheRuntimeKnowsNothingOfReportsEmpty() {
        var support = new FakeFeatureSupport(
            highestRootSignature: default,
            highestShaderModel: default,
            refused: [
                D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS19,
                D3D12_FEATURE.D3D12_FEATURE_ROOT_SIGNATURE,
                D3D12_FEATURE.D3D12_FEATURE_SHADER_MODEL,
            ]
        );

        var capabilities = DirectXFeatureReads.Capabilities(support: support);

        Assert.Equal(
            actual: capabilities.RootSignatureVersion,
            expected: string.Empty
        );
        Assert.Equal(
            actual: capabilities.ShaderModel,
            expected: string.Empty
        );
    }
    [Fact]
    public void ARefusedOptionsQueryIsADirectXFailure() {
        var support = new FakeFeatureSupport(
            highestRootSignature: D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1_1,
            highestShaderModel: D3D_SHADER_MODEL.D3D_SHADER_MODEL_6_7,
            refused: [D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS]
        );

        var failure = Assert.Throws<DirectXException>(testCode: () => DirectXFeatureReads.Capabilities(support: support));

        Assert.Equal(
            actual: failure.Result,
            expected: InvalidArgument.Value
        );
    }
    [Fact]
    public void ARefusedFeatureLevelsQueryReportsAnEmptyLevel() {
        var refusing = new FakeFeatureSupport(
            highestRootSignature: default,
            highestShaderModel: default,
            refused: [D3D12_FEATURE.D3D12_FEATURE_FEATURE_LEVELS]
        );
        var answering = new FakeFeatureSupport(
            highestRootSignature: default,
            highestShaderModel: default,
            refused: []
        );

        Assert.Equal(
            actual: DirectXFeatureReads.MaxFeatureLevel(support: refusing),
            expected: string.Empty
        );
        Assert.Equal(
            actual: DirectXFeatureReads.MaxFeatureLevel(support: answering),
            expected: "12_1"
        );
    }
    [Fact]
    public void RefusedMemoryQueriesReportTheDefaultProfileOrNoUploadHeaps() {
        var adapter = new DXGI_ADAPTER_DESC1 {
            DedicatedVideoMemory = unchecked((nuint)(8UL << 30)),
            SharedSystemMemory = unchecked((nuint)(16UL << 30)),
        };
        var noArchitecture = new FakeFeatureSupport(
            highestRootSignature: default,
            highestShaderModel: default,
            refused: [D3D12_FEATURE.D3D12_FEATURE_ARCHITECTURE]
        );
        var noOptions16 = new FakeFeatureSupport(
            highestRootSignature: default,
            highestShaderModel: default,
            refused: [D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS16]
        );
        var answering = new FakeFeatureSupport(
            highestRootSignature: default,
            highestShaderModel: default,
            refused: []
        );

        Assert.Equal(
            actual: DirectXFeatureReads.MemoryProfile(
                adapter: in adapter,
                support: noArchitecture
            ),
            expected: default
        );

        var architecture = new D3D12_FEATURE_DATA_ARCHITECTURE();

        Assert.Equal(
            actual: DirectXFeatureReads.MemoryProfile(
                adapter: in adapter,
                support: noOptions16
            ),
            expected: DirectXNativeDeviceApi.MemoryProfile(
                adapter: in adapter,
                architecture: in architecture,
                options16: default
            )
        );

        var uploadHeaps = new D3D12_FEATURE_DATA_D3D12_OPTIONS16 {
            GPUUploadHeapSupported = true,
        };

        // The control: an answered options 16 reports the upload heaps it answered.
        Assert.Equal(
            actual: DirectXFeatureReads.MemoryProfile(
                adapter: in adapter,
                support: answering
            ),
            expected: DirectXNativeDeviceApi.MemoryProfile(
                adapter: in adapter,
                architecture: in architecture,
                options16: in uploadHeaps
            )
        );
    }
    [Fact]
    public void TheShaderModelFloorReadsARefusedQueryAsBelow() {
        var refusing = new FakeFeatureSupport(
            highestRootSignature: default,
            highestShaderModel: D3D_SHADER_MODEL.D3D_SHADER_MODEL_6_5,
            refused: []
        );
        var reaching = new FakeFeatureSupport(
            highestRootSignature: default,
            highestShaderModel: D3D_SHADER_MODEL.D3D_SHADER_MODEL_6_8,
            refused: []
        );

        // A runtime whose newest model is 6.5 does not know 6.6 and refuses the query.
        Assert.False(condition: DirectXFeatureReads.ReachesShaderModel(
            reported: out var refused,
            required: D3D_SHADER_MODEL.D3D_SHADER_MODEL_6_6,
            support: refusing
        ));
        Assert.Equal(
            actual: refused,
            expected: string.Empty
        );
        Assert.True(condition: DirectXFeatureReads.ReachesShaderModel(
            reported: out var reached,
            required: D3D_SHADER_MODEL.D3D_SHADER_MODEL_6_6,
            support: reaching
        ));
        Assert.Equal(
            actual: reached,
            expected: "6.6"
        );
    }

    // Answers as a runtime whose newest root signature version and shader model are the given ones: asking about a
    // newer one is refused, asking about one it knows is answered; each refused feature is refused outright. Options
    // answers binding tier 3, feature levels answer 12_1, and options 16 answers upload heaps.
    private sealed class FakeFeatureSupport(D3D_ROOT_SIGNATURE_VERSION highestRootSignature, D3D_SHADER_MODEL highestShaderModel, D3D12_FEATURE[] refused) : IDirectXFeatureSupport {
        public HRESULT CheckFeatureSupport(D3D12_FEATURE feature, void* data, uint size) {
            if (Array.IndexOf(array: refused, value: feature) >= 0) {
                return InvalidArgument;
            }

            switch (feature) {
                case D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS:
                    ((D3D12_FEATURE_DATA_D3D12_OPTIONS*)data)->ResourceBindingTier = D3D12_RESOURCE_BINDING_TIER.D3D12_RESOURCE_BINDING_TIER_3;

                    return Answered;
                case D3D12_FEATURE.D3D12_FEATURE_ROOT_SIGNATURE:
                    var signature = (D3D12_FEATURE_DATA_ROOT_SIGNATURE*)data;

                    return ((signature->HighestVersion > highestRootSignature)
                        ? InvalidArgument
                        : Answered
                    );
                case D3D12_FEATURE.D3D12_FEATURE_SHADER_MODEL:
                    var model = (D3D12_FEATURE_DATA_SHADER_MODEL*)data;

                    return ((model->HighestShaderModel > highestShaderModel)
                        ? InvalidArgument
                        : Answered
                    );
                case D3D12_FEATURE.D3D12_FEATURE_FEATURE_LEVELS:
                    ((D3D12_FEATURE_DATA_FEATURE_LEVELS*)data)->MaxSupportedFeatureLevel = D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_12_1;

                    return Answered;
                case D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS16:
                    ((D3D12_FEATURE_DATA_D3D12_OPTIONS16*)data)->GPUUploadHeapSupported = true;

                    return Answered;
                default:
                    return Answered;
            }
        }
    }
}
