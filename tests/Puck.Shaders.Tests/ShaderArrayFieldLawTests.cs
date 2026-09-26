using System.Buffers.Binary;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for a pass's arrays (<see cref="ShaderArrayField"/>): they lay out in the World group's block, set 1, apart from
/// the pass block, in ordinal name order at 16 bytes an element, with an accessor per array in the generated
/// declarations; an array's elements are written as its element type into each row's first word, rows past the values
/// read zero, and an element type that is not a scalar or a length out of range is refused by name.
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
    public void ArraysLayOutInTheWorldGroupAtSixteenBytesAnElementWithAnAccessorEach() {
        var layout = Layout(arrays: new Dictionary<string, ShaderArrayField>(comparer: StringComparer.Ordinal) {
            ["tiles"] = new(Length: 63, Type: ShaderValueType.Int),
            ["heights"] = new(Length: 4, Type: ShaderValueType.Float),
        });
        var world = layout.Layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.World));

        Assert.Equal(expected: 1U, actual: world.Set);
        Assert.Equal(expected: ((4U + 63U) * 16U), actual: layout.WorldBlockSizeBytes);
        Assert.Equal(
            actual: layout.Arrays.Select(selector: static array => (array.Name, array.Type, array.Offset, array.Length)).ToArray(),
            expected: [("heights", ShaderValueType.Float, 0U, 4U), ("tiles", ShaderValueType.Int, 64U, 63U)]
        );
        Assert.DoesNotContain(
            collection: layout.Slots,
            filter: static slot => (slot.Name is "tiles" or "heights")
        );

        var include = ShaderInterfaceHlsl.Generate(shaderInterface: layout.Interface);

        Assert.Contains(actualString: include, expectedSubstring: "int tilesAt(uint index) { return worldGroup.tiles[index].x; }");
        Assert.Contains(actualString: include, expectedSubstring: "float heightsAt(uint index)");
    }
    [Fact]
    public void AnArrayWritesItsElementTypeIntoEachRowAndZerosPastItsValues() {
        var layout = Layout(arrays: new Dictionary<string, ShaderArrayField>(comparer: StringComparer.Ordinal) {
            ["tiles"] = new(Length: 4, Type: ShaderValueType.Int),
        });
        var block = new byte[layout.WorldBlockSizeBytes];

        block.AsSpan().Fill(value: 0xAB);
        layout.WriteArray(
            array: layout.Arrays[0],
            block: block,
            values: [3d, 2.5d, -7.6d]
        );

        Assert.Equal(
            actual: Enumerable.Range(count: 4, start: 0).Select(selector: index => BinaryPrimitives.ReadInt32LittleEndian(source: block.AsSpan(start: (index * 16)))).ToArray(),
            expected: [3, 2, -8, 0]
        );
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
