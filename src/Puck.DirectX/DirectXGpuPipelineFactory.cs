using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuPipelineFactory"/> for Direct3D 12 on its device context. A graphics pipeline is the root
/// signature <c>DirectXRootSignatures.CreateLayout</c> creates from its description's groups, with their samplers in
/// sampler tables, and an opaque PSO over <c>POSITIONn</c> vertex attributes and the render pass's formats and depth
/// test.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe partial class DirectXGpuPipelineFactory(DirectXDeviceContext deviceContext) : IGpuPipelineFactory {
    private const byte ColorWriteEnableAll = 15;

    /// <inheritdoc/>
    public IGpuPipeline Create(
        IGpuRenderPass renderPass,
        IGpuShaderModule vertexShaderModule,
        IGpuShaderModule fragmentShaderModule,
        GpuGraphicsPipelineDescription description,
        in GpuObjectName name
    ) =>
        Named(
            name: in name,
            pipeline: CreateGraphics(
                description: description,
                fragmentShaderModule: fragmentShaderModule,
                renderPass: renderPass,
                vertexShaderModule: vertexShaderModule
            )
        );

    // Names a created pipeline's state object.
    private DirectXGpuPipeline Named(DirectXGpuPipeline pipeline, in GpuObjectName name) {
        deviceContext.Services.Naming.Name(
            handle: pipeline.Layout.PsoHandle,
            kind: GpuObjectKind.Pipeline,
            name: in name
        );

        return pipeline;
    }
    private DirectXGpuPipeline CreateGraphics(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description) {
        ArgumentNullException.ThrowIfNull(description);
        description.ValidateAgainst(renderPass: renderPass);

        var device = ((ID3D12Device*)deviceContext.Device.Handle);
        var vs = ((DirectXGpuShaderModule)vertexShaderModule);
        var ps = ((DirectXGpuShaderModule)fragmentShaderModule);
        // The render pass's formats are the PSO's render-target and depth-stencil formats, as a Vulkan pipeline takes
        // them from its render pass.
        var pass = ((DirectXGpuRenderPass)renderPass);
        // The groups bind through DirectXRootSignatures.CreateLayout's root signature, with their samplers in sampler
        // tables rather than static samplers.
        var layout = DirectXRootSignatures.CreateLayout(
            description: description.RequireLayout(),
            device: device
        );
        var attributes = description.VertexInput.Attributes;
        var inputElements = stackalloc D3D12_INPUT_ELEMENT_DESC[attributes.Count];

        // Every attribute reads as the "POSITION" semantic at its location's SemanticIndex: Direct3D's HLSL-facing
        // semantic-name concept has no Vulkan counterpart, where DXC numbers a vertex stage's inputs by declaration
        // order, so a shader declares attribute n as POSITIONn and as its nth input, and both backends read the same
        // attribute.
        fixed (byte* positionSemantic = "POSITION\0"u8) {
            for (var index = 0; (index < attributes.Count); index++) {
                inputElements[index] = new D3D12_INPUT_ELEMENT_DESC {
                    AlignedByteOffset = attributes[index].OffsetBytes,
                    Format = ToDxgiFormat(format: attributes[index].Format),
                    InputSlot = 0,
                    InputSlotClass = D3D12_INPUT_CLASSIFICATION.D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA,
                    InstanceDataStepRate = 0,
                    SemanticIndex = attributes[index].Location,
                    SemanticName = new PCSTR(value: positionSemantic),
                };
            }

            try {
                layout.PsoHandle = BuildPso(
                    depthCompare: description.DepthCompare,
                    device: device,
                    library: deviceContext.PipelineLibrary,
                    rootSignature: layout.RootSignatureHandle,
                    rootSignatureBlob: layout.RootSignatureBlob,
                    renderPass: pass,
                    inputElements: inputElements,
                    inputElementCount: ((uint)attributes.Count),
                    vsHandle: vs.Handle,
                    vsLength: vs.BytecodeLength,
                    psHandle: ps.Handle,
                    psLength: ps.BytecodeLength
                );
            } catch {
                layout.Dispose();
                throw;
            }
        }

        return new DirectXGpuPipeline(layout: layout);
    }
    private static DXGI_FORMAT ToDxgiFormat(GpuVertexFormat format) {
        return format switch {
            GpuVertexFormat.R32G32Float => DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
            GpuVertexFormat.R32G32B32Float => DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT,
            GpuVertexFormat.R32G32B32A32Float => DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
            _ => throw new ArgumentOutOfRangeException(
            nameof(format),
            format,
            "The vertex attribute format is not defined."
        ),
        };
    }
    private static D3D12_COMPARISON_FUNC ToComparisonFunc(GpuDepthCompare compare) => compare switch {
        GpuDepthCompare.Less => D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_LESS,
        GpuDepthCompare.LessOrEqual => D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_LESS_EQUAL,
        GpuDepthCompare.Greater => D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_GREATER,
        GpuDepthCompare.GreaterOrEqual => D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_GREATER_EQUAL,
        GpuDepthCompare.Equal => D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_EQUAL,
        GpuDepthCompare.Always => D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_ALWAYS,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: compare,
            message: "The depth comparison is not defined.",
            paramName: nameof(compare)
        ),
    };
    private static nint BuildPso(
        GpuDepthCompare? depthCompare,
        ID3D12Device* device,
        DirectXPipelineLibrary? library,
        nint rootSignature,
        byte[] rootSignatureBlob,
        DirectXGpuRenderPass renderPass,
        D3D12_INPUT_ELEMENT_DESC* inputElements,
        uint inputElementCount,
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
            DepthStencilState = ((depthCompare is { } compare)
                ? new D3D12_DEPTH_STENCIL_DESC {
                    DepthEnable = true,
                    DepthFunc = ToComparisonFunc(compare: compare),
                    DepthWriteMask = D3D12_DEPTH_WRITE_MASK.D3D12_DEPTH_WRITE_MASK_ALL,
                    StencilEnable = false,
                }
                : new D3D12_DEPTH_STENCIL_DESC {
                    DepthEnable = false,
                    StencilEnable = false,
                }),
            InputLayout = new D3D12_INPUT_LAYOUT_DESC {
                NumElements = inputElementCount,
                pInputElementDescs = inputElements,
            },
            DSVFormat = renderPass.DepthFormat,
            NumRenderTargets = ((uint)renderPass.ColorFormats.Count),
            PS = new D3D12_SHADER_BYTECODE {
                BytecodeLength = psLength,
                pShaderBytecode = ((void*)psHandle),
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
                pShaderBytecode = ((void*)vsHandle),
            },
            pRootSignature = ((ID3D12RootSignature*)rootSignature),
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
        for (var index = 0; (index < renderPass.ColorFormats.Count); index++) {
            psoDesc.RTVFormats.AsSpan()[index] = renderPass.ColorFormats[index];
        }

        if (library is not null) {
            // The fixed state this factory varies besides the stages and root signature: the render-target count and
            // formats, the depth-stencil format, the depth test (-1 for none), and each vertex attribute's format,
            // offset and semantic index.
            var colorCount = renderPass.ColorFormats.Count;
            var state = new byte[(sizeof(int) * ((3 + colorCount) + (3 * ((int)inputElementCount))))];
            var words = MemoryMarshal.Cast<byte, int>(span: state.AsSpan());

            words[0] = colorCount;

            for (var index = 0; (index < colorCount); index++) {
                words[(1 + index)] = ((int)renderPass.ColorFormats[index]);
            }

            words[(1 + colorCount)] = ((int)renderPass.DepthFormat);
            words[(2 + colorCount)] = ((depthCompare is { } test)
                ? ((int)test)
                : -1);

            for (var index = 0; (index < inputElementCount); index++) {
                words[((3 + colorCount) + (3 * index))] = ((int)inputElements[index].Format);
                words[((4 + colorCount) + (3 * index))] = ((int)inputElements[index].AlignedByteOffset);
                words[((5 + colorCount) + (3 * index))] = ((int)inputElements[index].SemanticIndex);
            }

            return library.CreateGraphicsPipeline(
                description: in psoDesc,
                device: device,
                identity: [new ReadOnlySpan<byte>(
                    length: checked((int)vsLength),
                    pointer: ((void*)vsHandle)
                ).ToArray(), new ReadOnlySpan<byte>(
                    length: checked((int)psLength),
                    pointer: ((void*)psHandle)
                ).ToArray(), rootSignatureBlob, state]
            );
        }

        void* pso;
        var psoIid = ID3D12PipelineState.IID_Guid;

        device->CreateGraphicsPipelineState(
            pDesc: in psoDesc,
            ppPipelineState: out pso,
            riid: in psoIid
        );

        return ((nint)pso);
    }
}
