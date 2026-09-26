using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe partial class DirectXGpuPipelineFactory {
    /// <inheritdoc/>
    /// <remarks>A compute pipeline's root signature has one descriptor table mirroring the neutral binding list.
    /// The descriptor table holds one range per binding, with each range's slot in the heap fixed at its binding
    /// index (<c>OffsetInDescriptorsFromTableStart = binding</c>, matching how <see cref="DirectXGpuBindings"/> writes a
    /// descriptor at <c>CpuBase + binding * size</c>). A UAV binding (a storage image or a read-write buffer) takes a
    /// <c>u#</c> register and an SRV binding (a read-only buffer) a <c>t#</c>, each at its binding number. An array
    /// binding (<see cref="GpuComputeBinding.Count"/> &gt; 1) consumes that many consecutive registers and heap slots. Every
    /// parameter is <c>SHADER_VISIBILITY_ALL</c> (the compute visibility class), the root signature holds no sampler, and
    /// the input-layout flag is dropped. Push constants are eight 32-bit root constants at <c>b0</c>.
    /// <para>A description with a <see cref="GpuComputePipelineDescription.Layout"/> takes none of that: its root
    /// signature is <see cref="DirectXRootSignatures.CreateLayout"/>'s, with its samplers in sampler tables rather than
    /// static samplers.</para>
    /// </remarks>
    public IGpuComputePipeline Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description, in GpuObjectName name) =>
        Named(
            name: in name,
            pipeline: CreateCompute(
                computeShaderModule: computeShaderModule,
                description: description
            )
        );

    private DirectXGpuPipeline CreateCompute(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) {
        ArgumentNullException.ThrowIfNull(computeShaderModule);
        ArgumentNullException.ThrowIfNull(description);

        if (description.Layout is not null) {
            var grouped = DirectXRootSignatures.CreateLayout(
                description: description.RequireLayout(),
                device: ((ID3D12Device*)deviceContext.Device.Handle)
            );
            var module = ((DirectXGpuShaderModule)computeShaderModule);

            try {
                grouped.PsoHandle = BuildPso(
                    csHandle: module.Handle,
                    csLength: module.BytecodeLength,
                    device: ((ID3D12Device*)deviceContext.Device.Handle),
                    library: deviceContext.PipelineLibrary,
                    rootSignature: grouped.RootSignatureHandle,
                    rootSignatureBlob: grouped.RootSignatureBlob
                );
            } catch {
                grouped.Dispose();
                throw;
            }

            return new DirectXGpuPipeline(layout: grouped);
        }

        var bindings = description.Bindings;
        var pushConstantBinding = description.PushConstantBinding;

        ArgumentNullException.ThrowIfNull(bindings);
        GpuComputeBinding.ValidateSet(bindings: bindings);

        var device = ((ID3D12Device*)deviceContext.Device.Handle);
        var cs = ((DirectXGpuShaderModule)computeShaderModule);
        var hasDescriptorTable = (bindings.Count > 0);
        var hasRootConstants = (pushConstantBinding is not null);
        var layout = DirectXPipelineLayout.CreateForParameters(
            hasDescriptorTable: hasDescriptorTable,
            pushConstantBinding: pushConstantBinding
        );

        // Pack heap slots in binding-list order: each binding occupies its Count consecutive slots starting right after
        // the previous binding's, so an array binding can never overlap a later binding regardless of the chosen index
        // values (the binding index is a logical id, not the heap offset). The root signature ranges and the bindings'
        // writes both resolve a binding to its slot through this same map, so they stay in lockstep.
        layout.SlotByBinding = PackSlots(
            bindings: bindings,
            slotCount: out var slotCount
        );
        layout.DescriptorSlotCount = slotCount;

        layout.RootSignatureHandle = CreateRootSignature(
            bindings: bindings,
            device: device,
            hasDescriptorTable: hasDescriptorTable,
            hasRootConstants: hasRootConstants,
            rootConstantsCount: layout.RootConstantsCount,
            serialized: out layout.RootSignatureBlob,
            slotByBinding: layout.SlotByBinding
        );
        layout.PsoHandle = BuildPso(
            device: device,
            library: deviceContext.PipelineLibrary,
            rootSignatureBlob: layout.RootSignatureBlob,
            rootSignature: layout.RootSignatureHandle,
            csHandle: cs.Handle,
            csLength: cs.BytecodeLength
        );

        return new DirectXGpuPipeline(layout: layout);
    }
    // Packs each binding to a base heap slot in binding-list order (an array binding consuming Count slots), returning a
    // map indexed by binding index. Two bindings never share a slot, so no kernel has to hand-pick indices around array
    // spans — picking any distinct index per binding is enough.
    private static uint[] PackSlots(IReadOnlyList<GpuComputeBinding> bindings, out uint slotCount) {
        var maxBindingIndex = 0u;

        for (var index = 0; (index < bindings.Count); index++) {
            maxBindingIndex = Math.Max(
                val1: maxBindingIndex,
                val2: bindings[index].Binding
            );
        }

        var slotByBinding = new uint[((bindings.Count > 0)
            ? (maxBindingIndex + 1)
            : 0)];
        var nextSlot = 0u;

        for (var index = 0; (index < bindings.Count); index++) {
            var binding = bindings[index];

            slotByBinding[binding.Binding] = nextSlot;
            nextSlot = checked((nextSlot + binding.Count));
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
        uint[] slotByBinding,
        out byte[] serialized
    ) {
        var rangeCount = bindings.Count;
        var paramCount = ((hasDescriptorTable
            ? 1
            : 0) + (hasRootConstants
            ? 1
            : 0));
        var ranges = stackalloc D3D12_DESCRIPTOR_RANGE[((rangeCount > 0)
            ? rangeCount
            : 1)];
        var parameters = stackalloc D3D12_ROOT_PARAMETER[2];
        var paramIndex = 0;

        // One range per binding. The heap slot is the packed slot from slotByBinding (DirectXGpuBindings writes each
        // descriptor at that slot + its array element); a shader register is the binding number, an array binding
        // consuming `Count` consecutive registers and heap slots.
        for (var index = 0; (index < bindings.Count); index++) {
            var binding = bindings[index];
            // A read-only buffer binds as an SRV (t#); a storage image or a read-write buffer binds as a UAV (u#).
            var isSrv = (binding.Kind == GpuBindingKind.ReadOnlyBuffer);
            var count = binding.Count;
            var rangeType = (isSrv
                ? D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV
                : D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_UAV
            );
            ranges[index] = new D3D12_DESCRIPTOR_RANGE {
                BaseShaderRegister = binding.Binding,
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
                NumDescriptorRanges = ((uint)rangeCount),
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

        var desc = new D3D12_ROOT_SIGNATURE_DESC {
            Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_NONE,
            NumParameters = ((uint)paramCount),
            NumStaticSamplers = 0,
            pParameters = ((0 < paramCount)
            ? parameters
            : null),
            pStaticSamplers = null,
        };

        return DirectXRootSignatures.Create(
            description: in desc,
            device: device,
            serialized: out serialized
        );
    }
    private static nint BuildPso(ID3D12Device* device, DirectXPipelineLibrary? library, nint rootSignature, byte[] rootSignatureBlob, nint csHandle, nuint csLength) {
        var psoDesc = new D3D12_COMPUTE_PIPELINE_STATE_DESC {
            CS = new D3D12_SHADER_BYTECODE {
                BytecodeLength = csLength,
                pShaderBytecode = ((void*)csHandle),
            },
            pRootSignature = ((ID3D12RootSignature*)rootSignature),
        };

        if (library is not null) {
            return library.CreateComputePipeline(
                description: in psoDesc,
                device: device,
                identity: [new ReadOnlySpan<byte>(
                    length: checked((int)csLength),
                    pointer: ((void*)csHandle)
                ).ToArray(), rootSignatureBlob]
            );
        }

        void* pso;
        var psoIid = ID3D12PipelineState.IID_Guid;

        device->CreateComputePipelineState(
            pDesc: in psoDesc,
            ppPipelineState: out pso,
            riid: in psoIid
        );

        return ((nint)pso);
    }
}
