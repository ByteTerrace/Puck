using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuComputePipelineFactory"/> for Direct3D 12. Builds a compute root signature whose
/// single descriptor table mirrors the neutral binding list and a compute PSO from the supplied DXIL.
/// <para>
/// The descriptor table holds one range per binding, with each range's slot in the heap fixed at its binding
/// index (<c>OffsetInDescriptorsFromTableStart = binding</c>, matching how the descriptor allocator writes a
/// descriptor at <c>CpuBase + binding * size</c>). Shader registers are assigned per type in binding order: each
/// UAV binding (a storage image or a read-write buffer) takes the next <c>u#</c>, each SRV binding (a read-only
/// buffer) takes the next <c>t#</c>, and an array binding (<see cref="GpuComputeBinding.Count"/> &gt; 1) consumes
/// that many consecutive registers and heap slots — so each binding list lays out exactly as its kernel declares
/// its registers. Every parameter is
/// <c>SHADER_VISIBILITY_ALL</c> (the compute visibility class); EACH SampledImage binding adds its own CLAMP static
/// sampler, at s0, s1, ... in binding-list order (all sharing the pipeline's one requested filter — DXC's
/// <c>vk::combinedImageSampler</c> only fuses a scalar Texture2D+SamplerState pair, so a kernel with several
/// screen-like sources declares several distinct sampler symbols at distinct registers); the input-layout
/// flag is dropped. Push constants are eight 32-bit root constants at <c>b0</c>.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuComputePipelineFactory : IGpuComputePipelineFactory {
    /// <inheritdoc/>
    public IGpuComputePipeline Create(IGpuDeviceContext deviceContext, IGpuShaderModule computeShaderModule, IReadOnlyList<GpuComputeBinding> bindings, GpuPushConstantBinding? pushConstantBinding, GpuSamplerFilter samplerFilter = GpuSamplerFilter.Linear) {
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(computeShaderModule);
        ArgumentNullException.ThrowIfNull(bindings);

        var device = (ID3D12Device*)((IDirectXDeviceContext)deviceContext).Device.Handle;
        var cs = (DirectXGpuShaderModule)computeShaderModule;
        var hasDescriptorTable = (bindings.Count > 0);
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

        // Pack heap slots in binding-list order: each binding occupies its Count consecutive slots starting right after
        // the previous binding's, so an array binding can never overlap a later binding regardless of the chosen index
        // values (the binding index is a logical id, not the heap offset). The root signature ranges and the descriptor
        // allocator's writes both resolve a binding to its slot through this same map, so they stay in lockstep.
        layout.SlotByBinding = PackSlots(bindings: bindings, slotCount: out var slotCount);
        layout.DescriptorSlotCount = slotCount;

        layout.RootSignatureHandle = CreateRootSignature(
            device: device,
            bindings: bindings,
            hasDescriptorTable: hasDescriptorTable,
            hasRootConstants: hasRootConstants,
            rootConstantsCount: layout.RootConstantsCount,
            samplerFilter: samplerFilter,
            slotByBinding: layout.SlotByBinding
        );
        layout.PsoHandle = BuildPso(
            device: device,
            rootSignature: layout.RootSignatureHandle,
            csHandle: cs.Handle,
            csLength: cs.BytecodeLength
        );

        return new DirectXGpuComputePipeline(layout: layout);
    }

    // Packs each binding to a base heap slot in binding-list order (an array binding consuming Count slots), returning a
    // map indexed by binding index. Two bindings never share a slot, so no kernel has to hand-pick indices around array
    // spans — picking any distinct index per binding is enough.
    private static uint[] PackSlots(IReadOnlyList<GpuComputeBinding> bindings, out uint slotCount) {
        var maxBindingIndex = 0u;

        for (var index = 0; (index < bindings.Count); index++) {
            maxBindingIndex = Math.Max(val1: maxBindingIndex, val2: bindings[index].Binding);
        }

        var slotByBinding = new uint[((bindings.Count > 0) ? (maxBindingIndex + 1) : 0)];
        var nextSlot = 0u;

        for (var index = 0; (index < bindings.Count); index++) {
            var binding = bindings[index];

            slotByBinding[binding.Binding] = nextSlot;
            nextSlot += ((binding.Count > 0) ? binding.Count : 1);
        }

        slotCount = nextSlot;

        return slotByBinding;
    }
    private static nint CreateRootSignature(
        ID3D12Device* device,
        IReadOnlyList<GpuComputeBinding> bindings,
        bool hasDescriptorTable,
        bool hasRootConstants,
        uint rootConstantsCount,
        GpuSamplerFilter samplerFilter,
        uint[] slotByBinding
    ) {
        var rangeCount = bindings.Count;
        var paramCount = ((hasDescriptorTable ? 1 : 0) + (hasRootConstants ? 1 : 0));
        var ranges = stackalloc D3D12_DESCRIPTOR_RANGE[((rangeCount > 0) ? rangeCount : 1)];
        var parameters = stackalloc D3D12_ROOT_PARAMETER[2];
        var paramIndex = 0;
        var nextSrvRegister = 0u;
        var nextUavRegister = 0u;

        // One range per binding. The heap slot is the packed slot from slotByBinding (the allocator writes each
        // descriptor at that slot + its array element); shader registers are assigned per type in binding order
        // (UAVs u0,u1...; SRVs t0,t1...), an array binding consuming `Count` consecutive registers and heap slots.
        for (var index = 0; (index < bindings.Count); index++) {
            var binding = bindings[index];
            // A read-only storage buffer, an acceleration structure, and a sampled image all bind as SRVs (t#); a storage
            // image or a read-write buffer binds as a UAV (u#). On Direct3D 12 a RaytracingAccelerationStructure IS an
            // SRV, and a sampled image is an SRV read through the static sampler added to the root signature below.
            var isSrv = ((binding.Kind == GpuComputeBindingKind.StorageBufferRead) || (binding.Kind == GpuComputeBindingKind.AccelerationStructure) || (binding.Kind == GpuComputeBindingKind.SampledImage));
            var count = ((binding.Count > 0) ? binding.Count : 1);
            var rangeType = (isSrv
                ? D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV
                : D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_UAV);
            var baseRegister = (isSrv ? nextSrvRegister : nextUavRegister);

            if (isSrv) {
                nextSrvRegister += count;
            } else {
                nextUavRegister += count;
            }

            ranges[index] = new D3D12_DESCRIPTOR_RANGE {
                BaseShaderRegister = baseRegister,
                NumDescriptors = count,
                OffsetInDescriptorsFromTableStart = slotByBinding[binding.Binding],
                RangeType = rangeType,
                RegisterSpace = 0,
            };
        }

        if (hasDescriptorTable) {
            var tableParam = new D3D12_ROOT_PARAMETER {
                ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE,
                ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL,
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
                ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL,
            };

            constantsParam.Anonymous.Constants = new D3D12_ROOT_CONSTANTS {
                Num32BitValues = rootConstantsCount,
                RegisterSpace = 0,
                ShaderRegister = 0,
            };

            parameters[paramIndex++] = constantsParam;
        }

        // Each SampledImage binding reads its SRV through its OWN sampler register (s0, s1, ... in binding-list
        // order): DXC's vk::combinedImageSampler only fuses a SCALAR Texture2D+SamplerState pair (never an array), so
        // a shader with several screen-like sources declares several distinct sampler symbols at distinct registers —
        // one static sampler per SampledImage binding, all with the SAME requested filter (CLAMP-addressed, all-stage
        // visible), matches that 1:1. Existing compute pipelines (world/rt/validation/indirect) declare no
        // SampledImage binding, so they keep NumStaticSamplers = 0 and serialize byte-identically; a single-binding
        // pipeline (resample, the GamingBrick child resample) still gets exactly one static sampler at s0, unchanged.
        var sampledImageCount = 0u;

        for (var index = 0; (index < bindings.Count); index++) {
            if (bindings[index].Kind == GpuComputeBindingKind.SampledImage) {
                sampledImageCount++;
            }
        }

        var staticSamplers = stackalloc D3D12_STATIC_SAMPLER_DESC[((sampledImageCount > 0) ? (int)sampledImageCount : 1)];
        var samplerRegister = 0u;

        for (var index = 0; (index < bindings.Count); index++) {
            if (bindings[index].Kind != GpuComputeBindingKind.SampledImage) {
                continue;
            }

            staticSamplers[(int)samplerRegister] = new D3D12_STATIC_SAMPLER_DESC {
                AddressU = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
                AddressV = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
                AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
                BorderColor = D3D12_STATIC_BORDER_COLOR.D3D12_STATIC_BORDER_COLOR_OPAQUE_BLACK,
                ComparisonFunc = D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_NEVER,
                Filter = ((samplerFilter == GpuSamplerFilter.Nearest)
                    ? D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_POINT
                    : D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_LINEAR),
                MaxAnisotropy = 0,
                MaxLOD = float.MaxValue,
                MinLOD = 0f,
                MipLODBias = 0f,
                RegisterSpace = 0,
                ShaderRegister = samplerRegister,
                ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL,
            };

            samplerRegister++;
        }

        var desc = new D3D12_ROOT_SIGNATURE_DESC {
            Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_NONE,
            NumParameters = (uint)paramCount,
            NumStaticSamplers = sampledImageCount,
            pParameters = ((0 < paramCount) ? parameters : null),
            pStaticSamplers = ((sampledImageCount > 0) ? staticSamplers : null),
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
    private static nint BuildPso(ID3D12Device* device, nint rootSignature, nint csHandle, nuint csLength) {
        var psoDesc = new D3D12_COMPUTE_PIPELINE_STATE_DESC {
            CS = new D3D12_SHADER_BYTECODE {
                BytecodeLength = csLength,
                pShaderBytecode = (void*)csHandle,
            },
            pRootSignature = (ID3D12RootSignature*)rootSignature,
        };

        void* pso;
        var psoIid = ID3D12PipelineState.IID_Guid;

        device->CreateComputePipelineState(in psoDesc, in psoIid, out pso);

        return (nint)pso;
    }
}
