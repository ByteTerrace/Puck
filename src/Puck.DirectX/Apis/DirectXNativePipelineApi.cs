using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Puck.DirectX.Messages;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.DirectX.Apis;

/// <summary>
/// The native implementation of <see cref="IDirectXPipelineApi"/>: it serializes and creates a root signature,
/// then a graphics pipeline state. Two flavors are offered — a flat-shaded one (empty root signature,
/// <c>POSITION</c> + <c>COLOR</c> layout) and a textured one (a single SRV descriptor table + a static sampler,
/// <c>POSITION</c> + <c>TEXCOORD</c> layout) — both with solid no-cull rasterization, opaque blend, depth and
/// stencil disabled, triangle topology, and a single <c>R8G8B8A8_UNORM</c> render target.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXNativePipelineApi : IDirectXPipelineApi {
    // D3D12_COLOR_WRITE_ENABLE_ALL — write every channel.
    private const byte ColorWriteEnableAll = 15;

    /// <inheritdoc/>
    public DirectXGraphicsPipelineCreateResult CreateGraphicsPipeline(DirectXGraphicsPipelineCreateRequest request) {
        var device = (ID3D12Device*)request.DeviceHandle;
        var rootSignature = CreateColorRootSignature(device: device);

        fixed (byte* positionSemantic = "POSITION\0"u8)
        fixed (byte* colorSemantic = "COLOR\0"u8) {
            var inputElements = stackalloc D3D12_INPUT_ELEMENT_DESC[2];

            inputElements[0] = InputElement(
                format: DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
                offset: 0,
                semantic: positionSemantic
            );
            inputElements[1] = InputElement(
                format: DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT,
                offset: 8,
                semantic: colorSemantic
            );

            return new DirectXGraphicsPipelineCreateResult(
                PipelineStateHandle: BuildPipelineState(
                    device: device,
                    elementCount: 2,
                    inputElements: inputElements,
                    request: request,
                    rootSignature: rootSignature
                ),
                RootSignatureHandle: rootSignature
            );
        }
    }
    /// <inheritdoc/>
    public DirectXGraphicsPipelineCreateResult CreateTexturedGraphicsPipeline(DirectXGraphicsPipelineCreateRequest request) {
        var device = (ID3D12Device*)request.DeviceHandle;
        var rootSignature = CreateTexturedRootSignature(device: device);

        fixed (byte* positionSemantic = "POSITION\0"u8)
        fixed (byte* texCoordSemantic = "TEXCOORD\0"u8) {
            var inputElements = stackalloc D3D12_INPUT_ELEMENT_DESC[2];

            inputElements[0] = InputElement(
                format: DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
                offset: 0,
                semantic: positionSemantic
            );
            inputElements[1] = InputElement(
                format: DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
                offset: 8,
                semantic: texCoordSemantic
            );

            return new DirectXGraphicsPipelineCreateResult(
                PipelineStateHandle: BuildPipelineState(
                    device: device,
                    elementCount: 2,
                    inputElements: inputElements,
                    request: request,
                    rootSignature: rootSignature
                ),
                RootSignatureHandle: rootSignature
            );
        }
    }

    private static D3D12_INPUT_ELEMENT_DESC InputElement(byte* semantic, DXGI_FORMAT format, uint offset) {
        return new D3D12_INPUT_ELEMENT_DESC {
            AlignedByteOffset = offset,
            Format = format,
            InputSlot = 0,
            InputSlotClass = D3D12_INPUT_CLASSIFICATION.D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA,
            InstanceDataStepRate = 0,
            SemanticIndex = 0,
            SemanticName = new PCSTR(semantic),
        };
    }
    private static nint BuildPipelineState(ID3D12Device* device, nint rootSignature, DirectXGraphicsPipelineCreateRequest request, D3D12_INPUT_ELEMENT_DESC* inputElements, uint elementCount) {
        var pipelineDesc = new D3D12_GRAPHICS_PIPELINE_STATE_DESC {
            BlendState = new D3D12_BLEND_DESC {
                AlphaToCoverageEnable = false,
                IndependentBlendEnable = false,
            },
            DepthStencilState = new D3D12_DEPTH_STENCIL_DESC {
                DepthEnable = false,
                StencilEnable = false,
            },
            InputLayout = new D3D12_INPUT_LAYOUT_DESC {
                NumElements = elementCount,
                pInputElementDescs = inputElements,
            },
            NumRenderTargets = 1,
            PS = new D3D12_SHADER_BYTECODE {
                BytecodeLength = request.PixelShaderLength,
                pShaderBytecode = (void*)request.PixelShaderBytecode,
            },
            PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE,
            RasterizerState = new D3D12_RASTERIZER_DESC {
                AntialiasedLineEnable = false,
                ConservativeRaster = D3D12_CONSERVATIVE_RASTERIZATION_MODE.D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF,
                CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE,
                DepthBias = 0,
                DepthBiasClamp = 0f,
                DepthClipEnable = true,
                FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID,
                ForcedSampleCount = 0,
                FrontCounterClockwise = false,
                MultisampleEnable = false,
                SlopeScaledDepthBias = 0f,
            },
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            SampleMask = uint.MaxValue,
            VS = new D3D12_SHADER_BYTECODE {
                BytecodeLength = request.VertexShaderLength,
                pShaderBytecode = (void*)request.VertexShaderBytecode,
            },
            pRootSignature = (ID3D12RootSignature*)rootSignature,
        };

        pipelineDesc.BlendState.RenderTarget._0 = new D3D12_RENDER_TARGET_BLEND_DESC {
            BlendEnable = false,
            BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD,
            BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD,
            DestBlend = D3D12_BLEND.D3D12_BLEND_ZERO,
            DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_ZERO,
            LogicOp = D3D12_LOGIC_OP.D3D12_LOGIC_OP_NOOP,
            LogicOpEnable = false,
            RenderTargetWriteMask = ColorWriteEnableAll,
            SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE,
            SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE,
        };
        pipelineDesc.RTVFormats._0 = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;

        void* pipelineState;
        var pipelineStateIid = ID3D12PipelineState.IID_Guid;

        device->CreateGraphicsPipelineState(
            in pipelineDesc,
            in pipelineStateIid,
            out pipelineState
        );

        return (nint)pipelineState;
    }
    private static nint CreateColorRootSignature(ID3D12Device* device) {
        var rootSignatureDesc = new D3D12_ROOT_SIGNATURE_DESC {
            Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT,
        };

        return SerializeAndCreateRootSignature(
            device: device,
            rootSignatureDesc: in rootSignatureDesc
        );
    }
    private static nint CreateTexturedRootSignature(ID3D12Device* device) {
        var descriptorRange = new D3D12_DESCRIPTOR_RANGE {
            BaseShaderRegister = 0,
            NumDescriptors = 1,
            OffsetInDescriptorsFromTableStart = 0,
            RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV,
            RegisterSpace = 0,
        };
        var rootParameter = new D3D12_ROOT_PARAMETER {
            ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE,
            ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL,
        };

        rootParameter.Anonymous.DescriptorTable = new D3D12_ROOT_DESCRIPTOR_TABLE {
            NumDescriptorRanges = 1,
            pDescriptorRanges = &descriptorRange,
        };

        var staticSampler = new D3D12_STATIC_SAMPLER_DESC {
            AddressU = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressV = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            BorderColor = D3D12_STATIC_BORDER_COLOR.D3D12_STATIC_BORDER_COLOR_OPAQUE_BLACK,
            ComparisonFunc = D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_NEVER,
            Filter = D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_LINEAR,
            MaxAnisotropy = 0,
            MaxLOD = float.MaxValue,
            MinLOD = 0f,
            MipLODBias = 0f,
            RegisterSpace = 0,
            ShaderRegister = 0,
            ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL,
        };
        var rootSignatureDesc = new D3D12_ROOT_SIGNATURE_DESC {
            Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT,
            NumParameters = 1,
            NumStaticSamplers = 1,
            pParameters = &rootParameter,
            pStaticSamplers = &staticSampler,
        };

        return SerializeAndCreateRootSignature(
            device: device,
            rootSignatureDesc: in rootSignatureDesc
        );
    }
    private static nint SerializeAndCreateRootSignature(ID3D12Device* device, in D3D12_ROOT_SIGNATURE_DESC rootSignatureDesc) {
        ID3DBlob* signatureBlob = null;
        ID3DBlob* errorBlob = null;

        try {
            var result = PInvoke.D3D12SerializeRootSignature(
                in rootSignatureDesc,
                D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1,
                &signatureBlob,
                &errorBlob
            );

            result.ThrowIfFailed(operation: "D3D12SerializeRootSignature");

            void* rootSignature;
            var rootSignatureIid = ID3D12RootSignature.IID_Guid;

            device->CreateRootSignature(
                0,
                signatureBlob->GetBufferPointer(),
                signatureBlob->GetBufferSize(),
                in rootSignatureIid,
                out rootSignature
            );

            return (nint)rootSignature;
        } finally {
            if (signatureBlob is not null) {
                _ = signatureBlob->Release();
            }

            if (errorBlob is not null) {
                _ = errorBlob->Release();
            }
        }
    }
}
