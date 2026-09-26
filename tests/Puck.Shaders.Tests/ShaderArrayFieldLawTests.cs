using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for a pass's arrays (<see cref="ShaderArrayField"/>): each is a read-only structured buffer of its element type
/// in the World group, set 1, bound in ordinal name order, with an accessor per array in the generated declarations that
/// reads zero past the array's length; a row's elements are written as the element type four bytes apart, elements past
/// the values read zero, and an element type that is not a scalar or a length out of range is refused by name.
/// </summary>
public sealed class ShaderArrayFieldLawTests {
    private static ShaderPipelineParameterLayout Layout(IReadOnlyDictionary<string, ShaderArrayField>? arrays) => ShaderPipelineParameterLayout.Resolve(
        pass: new ShaderPipelinePass(
            Arrays: arrays,
            EntryPoint: "main",
            Kind: ShaderPipelineDocumentPassKind.Compute,
            Name: "draw",
            Outputs: [new ResourceReference(Name: "image")],
            Source: "board.hlsl"
        ),
        resources: new Dictionary<string, ShaderPipelineResource>(comparer: StringComparer.Ordinal) {
            ["image"] = new ShaderPipelineResource(
                Format: "R8G8B8A8Unorm",
                Kind: ShaderPipelineResourceKind.Image,
                Name: "image"
            ),
        }
    );

    [Fact]
    public void ArraysAreStructuredBuffersInTheWorldGroupWithABoundedAccessorEach() {
        var layout = Layout(arrays: new Dictionary<string, ShaderArrayField>(comparer: StringComparer.Ordinal) {
            ["tiles"] = new(Length: 63, Type: ShaderValueType.Int),
            ["heights"] = new(Length: 4, Type: ShaderValueType.Float),
        });
        var world = layout.Layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.World));

        Assert.Equal(expected: 1U, actual: world.Set);
        Assert.Empty(collection: world.BlockMembers);
        Assert.Equal(
            actual: world.Bindings.Select(selector: static binding => (binding.Name, binding.Binding, binding.Kind, binding.ElementStride)).ToArray(),
            expected: [("heights", 0U, GpuBindingKind.ReadOnlyBuffer, 4U), ("tiles", 1U, GpuBindingKind.ReadOnlyBuffer, 4U)]
        );
        Assert.Equal(
            actual: layout.Arrays.Select(selector: static array => (array.Name, array.Type, array.Binding, array.Length)).ToArray(),
            expected: [("heights", ShaderValueType.Float, 0U, 4U), ("tiles", ShaderValueType.Int, 1U, 63U)]
        );
        Assert.DoesNotContain(
            collection: layout.Slots,
            filter: static slot => (slot.Name is "tiles" or "heights")
        );

        var include = ShaderInterfaceHlsl.Generate(shaderInterface: layout.Interface);

        Assert.Contains(actualString: include, expectedSubstring: "[[vk::binding(1, 1)]] StructuredBuffer<int> tiles : register(t1, space1);");
        Assert.Contains(actualString: include, expectedSubstring: "int tilesAt(uint index) { return ((index < 63u) ? tiles[index] : ((int)0)); }");
        Assert.Contains(actualString: include, expectedSubstring: "float heightsAt(uint index) { return ((index < 4u) ? heights[index] : ((float)0)); }");
    }
    [Fact]
    public void AnArrayWritesItsElementTypeFourBytesApartAndZerosPastItsValues() {
        var elements = new byte[16];

        elements.AsSpan().Fill(value: 0xAB);
        ShaderPipelineParameterLayout.WriteArray(
            elements: elements,
            type: ShaderValueType.Int,
            values: [3d, 2.5d, -7.6d]
        );

        Assert.Equal(
            actual: Enumerable.Range(count: 4, start: 0).Select(selector: index => BinaryPrimitives.ReadInt32LittleEndian(source: elements.AsSpan(start: (index * 4)))).ToArray(),
            expected: [3, 2, -8, 0]
        );
        _ = Assert.Throws<ArgumentException>(testCode: () => ShaderPipelineParameterLayout.WriteArray(
            elements: new byte[6],
            type: ShaderValueType.Int,
            values: []
        ));
    }
    [Fact]
    public void AVectorElementAndALengthOutOfRangeAreRefusedByName() {
        Assert.Contains(
            actualString: Assert.Throws<InvalidDataException>(testCode: () => Layout(arrays: new Dictionary<string, ShaderArrayField>(comparer: StringComparer.Ordinal) {
                ["tiles"] = new(Length: 4, Type: ShaderValueType.Int2),
            })).Message,
            expectedSubstring: "array 'tiles' is Int2"
        );
        Assert.Contains(
            actualString: Assert.Throws<InvalidDataException>(testCode: () => Layout(arrays: new Dictionary<string, ShaderArrayField>(comparer: StringComparer.Ordinal) {
                ["tiles"] = new(Length: (ShaderArrayField.MaxLength + 1U), Type: ShaderValueType.Int),
            })).Message,
            expectedSubstring: "array 'tiles' length 4097"
        );
    }
}
