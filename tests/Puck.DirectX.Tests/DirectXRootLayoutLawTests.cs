using Puck.Abstractions.Gpu;
using Puck.Testing;
using Windows.Win32.Graphics.Direct3D12;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Holds <see cref="DirectXRootLayout.Plan"/> to the gate spike's two-group layouts and to the placement
/// rule: every group's views are one table and its samplers a second, every range sits at its group's space and its
/// binding's register, root parameter indices are dense, and the pushed index is one root constant at <c>b0</c> in
/// space 4. A description no backend may plan is refused by name before it reaches the planner.</summary>
public sealed class DirectXRootLayoutLawTests {
    private const D3D12_DESCRIPTOR_RANGE_TYPE Cbv = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_CBV;
    private const D3D12_DESCRIPTOR_RANGE_TYPE Sampler = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SAMPLER;
    private const D3D12_DESCRIPTOR_RANGE_TYPE Srv = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
    private const D3D12_DESCRIPTOR_RANGE_TYPE Uav = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_UAV;

    public static TheoryData<string> RefusalNames => new(values: GpuGroupLayoutTables.Refused.Select(selector: static refusal => refusal.Name).ToArray());

    private static string Describe(IEnumerable<DirectXRootParameter> parameters) =>
        string.Join(
            separator: " | ",
            values: parameters.Select(selector: static parameter => $"{parameter.Index} {parameter.Kind} space {parameter.Space} [{string.Join(separator: ", ", values: parameter.Ranges.Select(selector: static range => $"{range.Type} x{range.Count} r{range.BaseRegister} s{range.Space} @{range.TableOffset}"))}]")
        );
    private static string Describe(DirectXRootLayout layout) =>
        Describe(parameters: layout.Parameters);
    private static DirectXDescriptorRange Range(D3D12_DESCRIPTOR_RANGE_TYPE type, uint register, uint space, uint offset, uint count = 1) =>
        new(
            BaseRegister: register,
            Count: count,
            Space: space,
            TableOffset: offset,
            Type: type
        );
    private static string Expected(params (DirectXRootParameterKind Kind, uint Space, DirectXDescriptorRange[] Ranges)[] parameters) =>
        Describe(parameters: parameters.Select(selector: static (parameter, index) => new DirectXRootParameter(
            Index: ((uint)index),
            Kind: parameter.Kind,
            Ranges: parameter.Ranges,
            Space: parameter.Space
        )));

    [Fact]
    public void Film_grain_plans_a_frame_table_a_pass_table_and_a_pass_sampler_table() {
        Assert.Equal(
            actual: Describe(layout: DirectXRootLayout.Plan(description: GpuGroupLayoutTables.FilmGrain(pushesIndex: false))),
            expected: Expected(
                (DirectXRootParameterKind.ViewTable, 0, [Range(offset: 0, register: 0, space: 0, type: Cbv)]),
                (DirectXRootParameterKind.ViewTable, 3, [Range(offset: 0, register: 0, space: 3, type: Cbv), Range(offset: 1, register: 1, space: 3, type: Srv)]),
                (DirectXRootParameterKind.SamplerTable, 3, [Range(offset: 0, register: 2, space: 3, type: Sampler)])
            )
        );
    }
    [Fact]
    public void Pixelate_plans_a_frame_table_and_a_pass_table_with_no_sampler_table() {
        Assert.Equal(
            actual: Describe(layout: DirectXRootLayout.Plan(description: GpuGroupLayoutTables.Pixelate(pushesIndex: false))),
            expected: Expected(
                (DirectXRootParameterKind.ViewTable, 0, [Range(offset: 0, register: 0, space: 0, type: Cbv)]),
                (DirectXRootParameterKind.ViewTable, 3, [Range(offset: 0, register: 0, space: 3, type: Cbv), Range(offset: 1, register: 1, space: 3, type: Uav), Range(offset: 2, register: 2, space: 3, type: Uav)])
            )
        );
    }
    [Fact]
    public void The_pushed_index_is_the_last_parameter_at_b0_in_space_4() {
        var layout = DirectXRootLayout.Plan(description: GpuGroupLayoutTables.FilmGrain(pushesIndex: true));

        Assert.Equal(
            actual: layout.PushIndex,
            expected: layout.Parameters[^1]
        );
        Assert.Equal(
            actual: (layout.PushIndex!.Index, layout.PushIndex.Space, layout.PushIndex.DescriptorCount),
            expected: (3u, GpuPipelineLayoutDescription.PushIndexSpace, 0u)
        );
        // One 32-bit root constant at b0 in space 4: the fields D3D12_ROOT_CONSTANTS needs, so 14b builds it from the
        // plan alone.
        Assert.Equal(
            actual: layout.PushIndex.Constants,
            expected: new DirectXRootConstants(
                RegisterSpace: 4,
                ShaderRegister: 0,
                ValueCount: 1
            )
        );
        Assert.All(
            collection: layout.Parameters.Where(predicate: static parameter => (parameter.Kind != DirectXRootParameterKind.PushIndex)),
            action: static table => Assert.Null(@object: table.Constants)
        );
        Assert.Equal(
            actual: GpuPipelineLayoutDescription.PushIndexSpace,
            expected: 4u
        );
        Assert.Null(@object: DirectXRootLayout.Plan(description: GpuGroupLayoutTables.FilmGrain(pushesIndex: false)).PushIndex);
    }
    [Fact]
    public void Arrays_take_as_many_registers_and_descriptors_as_they_hold() {
        Assert.Equal(
            actual: Describe(layout: DirectXRootLayout.Plan(description: GpuGroupLayoutTables.Arrays())),
            expected: Expected(
                (DirectXRootParameterKind.ViewTable, 1, [Range(count: 3, offset: 0, register: 0, space: 1, type: Srv), Range(offset: 3, register: 5, space: 1, type: Uav)]),
                (DirectXRootParameterKind.SamplerTable, 1, [Range(count: 2, offset: 0, register: 3, space: 1, type: Sampler)]),
                (DirectXRootParameterKind.SamplerTable, 2, [Range(count: 2, offset: 0, register: 0, space: 2, type: Sampler)]),
                (DirectXRootParameterKind.PushIndex, GpuPipelineLayoutDescription.PushIndexSpace, [])
            )
        );
    }
    [Fact]
    public void Every_range_sits_at_its_groups_space_and_its_bindings_register() {
        foreach (var description in ((GpuPipelineLayoutDescription[])[GpuGroupLayoutTables.FilmGrain(pushesIndex: true), GpuGroupLayoutTables.Pixelate(pushesIndex: true), GpuGroupLayoutTables.Arrays()])) {
            var layout = DirectXRootLayout.Plan(description: description);

            Assert.Equal(
                actual: layout.Parameters.Select(selector: static parameter => parameter.Index),
                expected: Enumerable.Range(start: 0, count: layout.Parameters.Count).Select(selector: static index => ((uint)index))
            );

            foreach (var group in description.Groups) {
                foreach (var binding in group.Bindings) {
                    var table = layout.TableOf(
                        kind: ((binding.Kind == GpuBindingKind.Sampler)
                            ? DirectXRootParameterKind.SamplerTable
                            : DirectXRootParameterKind.ViewTable),
                        ordinal: group.Ordinal
                    );

                    Assert.NotNull(@object: table);
                    Assert.Single(
                        collection: table.Ranges,
                        predicate: range => ((range.BaseRegister == binding.Binding) && (range.Space == group.Ordinal) && (range.Count == binding.Count))
                    );
                }
            }
        }
    }
    [MemberData(memberName: nameof(RefusalNames))]
    [Theory]
    public void An_illegal_description_is_refused_by_name_before_planning(string name) {
        var (_, build, refusal) = GpuGroupLayoutTables.Refused.Single(predicate: entry => (entry.Name == name));
        var exception = Assert.Throws<ArgumentException>(testCode: () => DirectXRootLayout.Plan(description: build()));

        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: refusal
        );
    }
}
