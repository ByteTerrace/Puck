using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuPipelineFactory"/> for Direct3D 12. Creates a root signature and PSO tailored
/// for the SDF renderer: POSITION-only (R32G32_FLOAT) vertex input, a descriptor table with N SRV slots and
/// an optional UAV slot, root constants for push data, and a static linear-clamp sampler at <c>s0</c>.
/// <para>
/// Root signature layout (always the same slot ordering):
/// <list type="bullet">
/// <item>Parameter 0: descriptor table (SRVs t0..tN-1, optional UAV u0) — omitted when both counts are zero</item>
/// <item>Parameter 0 or 1: root constants (b0) — omitted when no push-constant binding is supplied</item>
/// </list>
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuPipelineFactory : IGpuPipelineFactory {
    private const byte ColorWriteEnableAll = 15;

    /// <inheritdoc/>
    public IGpuPipeline Create(
        IGpuDeviceContext deviceContext,
        IGpuRenderTarget renderTarget,
        IGpuShaderModule vertexShaderModule,
        IGpuShaderModule fragmentShaderModule,
        GpuPushConstantBinding? pushConstantBinding,
        uint textureSamplerCount,
        bool enableStorageBuffer,
        uint width,
        uint height
    ) {
        var device = (ID3D12Device*)deviceContext.DeviceHandle;
        var vs = (DirectXGpuShaderModule)vertexShaderModule;
        var ps = (DirectXGpuShaderModule)fragmentShaderModule;
        // Derive the PSO render-target format from the bound target (the DirectXImageView it exposes via
        // ImageViewHandle) so the PSO format always matches the RTV — mirroring how the Vulkan factory derives
        // the format from the render pass. A hardcoded format would mismatch a B8G8R8A8Unorm target.
        var renderTargetView = (DirectXImageView)GCHandle.FromIntPtr(value: renderTarget.ImageViewHandle).Target!;
        var layout = BuildLayout(
            device: device,
            textureSamplerCount: textureSamplerCount,
            enableStorageBuffer: enableStorageBuffer,
            pushConstantBinding: pushConstantBinding
        );

        fixed (byte* positionSemantic = "POSITION\0"u8) {
            var inputElement = new D3D12_INPUT_ELEMENT_DESC {
                AlignedByteOffset = 0,
                Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
                InputSlot = 0,
                InputSlotClass = D3D12_INPUT_CLASSIFICATION.D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA,
                InstanceDataStepRate = 0,
                SemanticIndex = 0,
                SemanticName = new PCSTR(positionSemantic),
            };

            layout.PsoHandle = BuildPso(
                device: device,
                rootSignature: layout.RootSignatureHandle,
                renderTargetFormat: renderTargetView.Format,
                inputElement: &inputElement,
                vsHandle: vs.Handle,
                vsLength: vs.BytecodeLength,
                psHandle: ps.Handle,
                psLength: ps.BytecodeLength
            );
        }

        return new DirectXGpuPipeline(layout: layout);
    }

    private static DirectXPipelineLayout BuildLayout(
        ID3D12Device* device,
        uint textureSamplerCount,
        bool enableStorageBuffer,
        GpuPushConstantBinding? pushConstantBinding
    ) {
        var hasDescriptorTable = ((textureSamplerCount > 0) || enableStorageBuffer);
        var hasRootConstants = (pushConstantBinding is not null);
        var layout = new DirectXPipelineLayout();

        if (hasDescriptorTable) {
            layout.DescriptorTableParamIndex = 0;
            layout.RootConstantsParamIndex = (hasRootConstants ? 1 : -1);
        } else {
            layout.RootConstantsParamIndex = (hasRootConstants ? 0 : -1);
        }

        if (hasRootConstants) {
            layout.RootConstantsCount = ((pushConstantBinding!.Size + 3) / 4);
        }

        // The descriptor table packs SRVs t0..tN-1 then the optional storage-buffer SRV — contiguous from slot 0 — so
        // the slot span is just their count (lets AllocateSet sub-allocate one pool across multiple sets). Each binding
        // maps to the identically-numbered slot: the texture array starts at binding 0 and the storage-buffer SRV sits
        // at binding textureSamplerCount, both inside this contiguous range, so the map is the identity.
        var slotCount = (textureSamplerCount + (enableStorageBuffer ? 1u : 0u));

        layout.DescriptorSlotCount = slotCount;
        layout.SlotByBinding = new uint[slotCount];

        for (var slot = 0u; (slot < slotCount); slot++) {
            layout.SlotByBinding[slot] = slot;
        }

        layout.RootSignatureHandle = CreateRootSignature(
            device: device,
            textureSamplerCount: textureSamplerCount,
            enableStorageBuffer: enableStorageBuffer,
            hasDescriptorTable: hasDescriptorTable,
            hasRootConstants: hasRootConstants,
            rootConstantsCount: layout.RootConstantsCount
        );

        return layout;
    }
    private static nint CreateRootSignature(
        ID3D12Device* device,
        uint textureSamplerCount,
        bool enableStorageBuffer,
        bool hasDescriptorTable,
        bool hasRootConstants,
        uint rootConstantsCount
    ) {
        var rangeCount = (((textureSamplerCount > 0) ? 1 : 0) + (enableStorageBuffer ? 1 : 0));
        var paramCount = ((hasDescriptorTable ? 1 : 0) + (hasRootConstants ? 1 : 0));
        var ranges = stackalloc D3D12_DESCRIPTOR_RANGE[2];
        var parameters = stackalloc D3D12_ROOT_PARAMETER[2];
        var rangeIndex = 0;
        var paramIndex = 0;

        if (textureSamplerCount > 0) {
            ranges[rangeIndex++] = new D3D12_DESCRIPTOR_RANGE {
                BaseShaderRegister = 0,
                NumDescriptors = textureSamplerCount,
                OffsetInDescriptorsFromTableStart = 0,
                RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV,
                RegisterSpace = 0,
            };
        }

        if (enableStorageBuffer) {
            ranges[rangeIndex++] = new D3D12_DESCRIPTOR_RANGE {
                // A read-only program/storage buffer is an SRV (a StructuredBuffer at t{textureSamplerCount}), not
                // a UAV: the buffer lives on an upload heap, where D3D12 forbids UAVs, and a pixel-shader UAV would
                // also collide with the render-target output at u0. The SRV register follows the sampler SRVs.
                BaseShaderRegister = textureSamplerCount,
                NumDescriptors = 1,
                OffsetInDescriptorsFromTableStart = textureSamplerCount,
                RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV,
                RegisterSpace = 0,
            };
        }

        if (hasDescriptorTable) {
            var tableParam = new D3D12_ROOT_PARAMETER {
                ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE,
                ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL,
            };

            tableParam.Anonymous.DescriptorTable = new D3D12_ROOT_DESCRIPTOR_TABLE {
                NumDescriptorRanges = (uint)rangeCount,
                pDescriptorRanges = ranges,
            };

            parameters[paramIndex++] = tableParam;
        }

        if (hasRootConstants) {
            var constantsParam = new D3D12_ROOT_PARAMETER {
                ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS,
                ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL,
            };

            constantsParam.Anonymous.Constants = new D3D12_ROOT_CONSTANTS {
                Num32BitValues = rootConstantsCount,
                RegisterSpace = 0,
                ShaderRegister = 0,
            };

            parameters[paramIndex++] = constantsParam;
        }

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

        var desc = new D3D12_ROOT_SIGNATURE_DESC {
            Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT,
            NumParameters = (uint)paramCount,
            NumStaticSamplers = 1,
            pParameters = ((0 < paramCount) ? parameters : null),
            pStaticSamplers = &staticSampler,
        };

        return SerializeAndCreate(device: device, desc: in desc);
    }
    private static nint SerializeAndCreate(ID3D12Device* device, in D3D12_ROOT_SIGNATURE_DESC desc) {
        ID3DBlob* sigBlob = null;
        ID3DBlob* errBlob = null;

        try {
            PInvoke.D3D12SerializeRootSignature(
                in desc,
                D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1,
                &sigBlob,
                &errBlob
            ).ThrowIfFailed(operation: "D3D12SerializeRootSignature");

            void* rootSig;
            var rootSigIid = ID3D12RootSignature.IID_Guid;

            device->CreateRootSignature(
                0,
                sigBlob->GetBufferPointer(),
                sigBlob->GetBufferSize(),
                in rootSigIid,
                out rootSig
            );

            return (nint)rootSig;
        } finally {
            if (sigBlob is not null) {
                _ = sigBlob->Release();
            }

            if (errBlob is not null) {
                _ = errBlob->Release();
            }
        }
    }
    private static nint BuildPso(
        ID3D12Device* device,
        nint rootSignature,
        DXGI_FORMAT renderTargetFormat,
        D3D12_INPUT_ELEMENT_DESC* inputElement,
        nint vsHandle,
        nuint vsLength,
        nint psHandle,
        nuint psLength
    ) {
        var psoDesc = new D3D12_GRAPHICS_PIPELINE_STATE_DESC {
            BlendState = new D3D12_BLEND_DESC {
                AlphaToCoverageEnable = false,
                IndependentBlendEnable = false,
            },
            DepthStencilState = new D3D12_DEPTH_STENCIL_DESC {
                DepthEnable = false,
                StencilEnable = false,
            },
            InputLayout = new D3D12_INPUT_LAYOUT_DESC {
                NumElements = 1,
                pInputElementDescs = inputElement,
            },
            NumRenderTargets = 1,
            PS = new D3D12_SHADER_BYTECODE {
                BytecodeLength = psLength,
                pShaderBytecode = (void*)psHandle,
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
                BytecodeLength = vsLength,
                pShaderBytecode = (void*)vsHandle,
            },
            pRootSignature = (ID3D12RootSignature*)rootSignature,
        };

        psoDesc.BlendState.RenderTarget._0 = new D3D12_RENDER_TARGET_BLEND_DESC {
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
        psoDesc.RTVFormats._0 = renderTargetFormat;

        void* pso;
        var psoIid = ID3D12PipelineState.IID_Guid;

        device->CreateGraphicsPipelineState(in psoDesc, in psoIid, out pso);

        return (nint)pso;
    }
}
