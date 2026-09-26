using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.Testing;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D12;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Holds the root signature a pipeline created from a <see cref="GpuPipelineLayoutDescription"/> binds through
/// to <see cref="DirectXRootLayout.Plan"/>'s tables, and a group's samplers to the device's sampler heap. The
/// serialized root signature is read back through the runtime's deserializer, with no device: every table's ranges at
/// the plan's registers, spaces and offsets, the pushed index as one root constant at <c>b0</c> in space 4, every
/// parameter at the plan's visibility, and no static sampler. On a software (WARP) device without the debug layer, the
/// gate spike's film grain and pixelate layouts create their root signatures, and a set of a group holding a sampler takes
/// its sampler table from its pool's range of the device's sampler heap; those laws skip when the host has no software
/// device that meets the floor.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGroupedLayoutLawTests {
    public static TheoryData<string> Tables => new(values: ["film grain", "film grain, pushing", "pixelate, pushing", "arrays"]);

    private static GpuPipelineLayoutDescription TableNamed(string name) => name switch {
        "film grain" => GpuGroupLayoutTables.FilmGrain(pushesIndex: false),
        "film grain, pushing" => GpuGroupLayoutTables.FilmGrain(pushesIndex: true),
        "pixelate, pushing" => GpuGroupLayoutTables.Pixelate(pushesIndex: true),
        "arrays" => GpuGroupLayoutTables.Arrays(),
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(name)),
    };
    private static string Describe(IEnumerable<DirectXRootParameter> parameters) =>
        string.Join(
            separator: " | ",
            values: parameters.Select(selector: static parameter => $"{parameter.Index} {parameter.Kind} space {parameter.Space} {parameter.Visibility} {parameter.Constants} [{string.Join(separator: ", ", values: parameter.Ranges.Select(selector: static range => $"{range.Type} x{range.Count} r{range.BaseRegister} s{range.Space} @{range.TableOffset}"))}]")
        );
    private static (string Parameters, uint StaticSamplers, D3D12_ROOT_SIGNATURE_FLAGS Flags) Read(byte[] serialized) {
        void* deserializer;
        var iid = ID3D12RootSignatureDeserializer.IID_Guid;

        fixed (byte* bytes = serialized) {
            PInvoke.D3D12CreateRootSignatureDeserializer(
                pRootSignatureDeserializerInterface: &iid,
                ppRootSignatureDeserializer: &deserializer,
                pSrcData: bytes,
                SrcDataSizeInBytes: ((nuint)serialized.Length)
            ).ThrowOnFailure();
        }

        try {
            var description = ((ID3D12RootSignatureDeserializer*)deserializer)->GetRootSignatureDesc();
            var parameters = new List<DirectXRootParameter>();

            for (var index = 0u; (index < description->NumParameters); index++) {
                var parameter = description->pParameters[index];

                if (parameter.ParameterType == D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS) {
                    var constants = parameter.Anonymous.Constants;

                    parameters.Add(item: new DirectXRootParameter(
                        Constants: new DirectXRootConstants(
                            RegisterSpace: constants.RegisterSpace,
                            ShaderRegister: constants.ShaderRegister,
                            ValueCount: constants.Num32BitValues
                        ),
                        Index: index,
                        Kind: DirectXRootParameterKind.PushIndex,
                        Ranges: [],
                        Space: constants.RegisterSpace,
                        Visibility: parameter.ShaderVisibility
                    ));

                    continue;
                }

                Assert.Equal(
                    actual: parameter.ParameterType,
                    expected: D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE
                );

                var table = parameter.Anonymous.DescriptorTable;
                var ranges = new List<DirectXDescriptorRange>();

                for (var rangeIndex = 0; (rangeIndex < table.NumDescriptorRanges); rangeIndex++) {
                    var range = table.pDescriptorRanges[rangeIndex];

                    ranges.Add(item: new DirectXDescriptorRange(
                        BaseRegister: range.BaseShaderRegister,
                        Count: range.NumDescriptors,
                        Space: range.RegisterSpace,
                        TableOffset: range.OffsetInDescriptorsFromTableStart,
                        Type: range.RangeType
                    ));
                }

                parameters.Add(item: new DirectXRootParameter(
                    Index: index,
                    Kind: ((ranges[0].Type == D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SAMPLER)
                        ? DirectXRootParameterKind.SamplerTable
                        : DirectXRootParameterKind.ViewTable),
                    Ranges: ranges,
                    Space: ranges[0].Space,
                    Visibility: parameter.ShaderVisibility
                ));
            }

            return (Describe(parameters: parameters), description->NumStaticSamplers, description->Flags);
        } finally {
            _ = ((ID3D12RootSignatureDeserializer*)deserializer)->Release();
        }
    }

    [MemberData(memberName: nameof(Tables))]
    [Theory]
    public void The_serialized_root_signature_holds_the_plans_tables_and_no_static_sampler(string name) {
        var description = TableNamed(name: name);
        var plan = DirectXRootLayout.Plan(description: description);
        var flags = ((description.Stages == GpuShaderStage.Compute)
            ? D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_NONE
            : D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT);

        Assert.Equal(
            actual: Read(serialized: DirectXRootSignatures.Serialize(
                flags: flags,
                layout: plan
            )),
            expected: (Describe(parameters: plan.Parameters), 0U, flags)
        );
    }
    [Fact]
    public void A_groups_pool_sizes_are_its_bindings_with_samplers_apart() {
        var filmGrain = GpuGroupLayoutTables.FilmGrain(pushesIndex: true);
        var arrays = GpuGroupLayoutTables.Arrays();

        Assert.Equal(
            actual: GpuDescriptorPoolSizes.ForGroups(groups: filmGrain.Groups),
            expected: new GpuDescriptorPoolSizes(
                    ConstantBufferCount: 2,
                MaxSets: 2,
                SampledImageCount: 1,
                SamplerCount: 1,
                StorageBufferCount: 0,
                StorageImageCount: 0
            )
        );
        Assert.Equal(
            actual: (GpuDescriptorPoolSizes.ForGroups(groups: filmGrain.Groups).HeapDescriptors, GpuDescriptorPoolSizes.ForGroups(groups: filmGrain.Groups).SamplerHeapDescriptors),
            expected: (3U, 1U)
        );
        Assert.Equal(
            actual: (GpuDescriptorPoolSizes.ForGroups(groups: arrays.Groups).HeapDescriptors, GpuDescriptorPoolSizes.ForGroups(groups: arrays.Groups).SamplerHeapDescriptors),
            expected: (4U, 4U)
        );
    }
    [Fact]
    public void A_warp_device_creates_the_spike_layouts_and_a_groups_samplers_come_from_the_sampler_heap() {
        using var context = DirectXTestDevices.Warp();
        var device = ((ID3D12Device*)context.Device.Handle);
        var bindings = context.Services.Bindings;
        var heaps = context.DescriptorHeaps;
        var samplersFree = heaps.Budget.FreeSamplerDescriptors;
        var filmGrain = GpuGroupLayoutTables.FilmGrain(pushesIndex: true);
        var pixelate = GpuGroupLayoutTables.Pixelate(pushesIndex: true);

        using var pixelateLayout = DirectXRootSignatures.CreateLayout(
            description: pixelate,
            device: device
        );
        using var layout = DirectXRootSignatures.CreateLayout(
            description: filmGrain,
            device: device
        );
        var plan = DirectXRootLayout.Plan(description: filmGrain);

        Assert.NotEqual(
            actual: (layout.RootSignatureHandle, pixelateLayout.RootSignatureHandle),
            expected: (0, 0)
        );
        Assert.Equal(
            actual: (layout.RootConstantsParamIndex, layout.RootConstantsCount, layout.DescriptorTableParamIndex),
            expected: (((int)plan.PushIndex!.Index), 1U, -1)
        );
        Assert.Equal(
            actual: layout.GroupHandles.Select(selector: static handle => (0 != handle)),
            expected: [true, false, false, true]
        );

        var pool = bindings.CreatePool(sizes: GpuDescriptorPoolSizes.ForGroups(groups: filmGrain.Groups), name: default);

        try {
            var admission = ((DirectXDescriptorPool)GCHandle.FromIntPtr(value: pool).Target!).Admission!;
            var frame = Set(handle: bindings.AllocateSet(
                descriptorSetLayoutHandle: layout.GroupHandles[0],
                poolHandle: pool,
                name: default
            ));
            var pass = Set(handle: bindings.AllocateSet(
                descriptorSetLayoutHandle: layout.GroupHandles[3],
                poolHandle: pool,
                name: default
            ));
            var samplerStart = admission.SamplerRanges.Single().Start;
            var samplerCpu = (DirectXConstants.GetCpuHeapStart(heap: ((ID3D12DescriptorHeap*)heaps.SamplerHeap)).ptr + (((nuint)samplerStart) * heaps.SamplerIncrement));
            var samplerGpu = (DirectXConstants.GetGpuHeapStart(heap: ((ID3D12DescriptorHeap*)heaps.SamplerHeap)).ptr + (((ulong)samplerStart) * heaps.SamplerIncrement));

            // The pass group's one sampler is the pool's one sampler descriptor, in the sampler heap; its constant
            // buffer and image follow the frame group's constant buffer in the view heap.
            Assert.Equal(
                actual: (admission.SamplerRanges.Single().Count, pass.SamplerCpuBase, pass.SamplerGpuBase),
                expected: (1U, samplerCpu, samplerGpu)
            );
            Assert.Equal(
                actual: (pass.Group!.ViewSlotCount, pass.Group.SamplerSlotCount, pass.Group.ViewTableIndex, pass.Group.SamplerTableIndex),
                expected: (2U, 1U, ((int)plan.TableOf(kind: DirectXRootParameterKind.ViewTable, ordinal: 3)!.Index), ((int)plan.TableOf(kind: DirectXRootParameterKind.SamplerTable, ordinal: 3)!.Index))
            );
            Assert.Equal(
                actual: (frame.Group!.SamplerSlotCount, (pass.CpuBase - frame.CpuBase)),
                expected: (0U, ((nuint)heaps.ViewIncrement))
            );
            Assert.Equal(
                actual: heaps.Budget.FreeSamplerDescriptors,
                expected: (samplersFree - 1U)
            );
        } finally {
            bindings.DestroyPool(poolHandle: pool);
        }

        Assert.Equal(
            actual: heaps.Budget.FreeSamplerDescriptors,
            expected: samplersFree
        );
    }

    private static DirectXDescriptorSet Set(nint handle) =>
        ((DirectXDescriptorSet)GCHandle.FromIntPtr(value: handle).Target!);
}
