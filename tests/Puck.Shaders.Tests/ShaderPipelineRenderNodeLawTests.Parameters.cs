using System.Buffers.Binary;
using System.Text.Json;

namespace Puck.Shaders.Tests;

/// <summary>
/// Bound-parameter laws for <see cref="ShaderPipelineRenderNode"/>: a host's write of one scalar config field lands in
/// the pass's parameter block at the offset its interface places the field, as the declared type (an integer field
/// rounded and clamped), reads back, leaves every other field as bound, allocates nothing, and is refused for a vector
/// field, an unknown field and a pass that is not installed.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    private static readonly IReadOnlyDictionary<string, ShaderConfigField> BoundConfig = new Dictionary<string, ShaderConfigField>(comparer: StringComparer.Ordinal) {
        ["gain"] = new(
            Default: JsonDocument.Parse(json: "1").RootElement,
            Type: ShaderValueType.Float
        ),
        ["level"] = new(
            Default: JsonDocument.Parse(json: "7").RootElement,
            Type: ShaderValueType.Int
        ),
        ["tint"] = new(
            Default: JsonDocument.Parse(json: "[1, 1, 1]").RootElement,
            Type: ShaderValueType.Float3
        ),
    };

    private static uint OffsetOf(ShaderPipelineRenderNode node, string field) => node.Plan!.Passes
        .Single(predicate: static pass => (pass.Name == "convert"))
        .Parameters.Slots.Single(predicate: slot => (slot.Name == field)).Offset;

    [Fact]
    public void AWrittenParameterLandsAtItsFieldsOffsetAsItsDeclaredType() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            pipeline: Feedback(convertConfig: BoundConfig)
        );

        Assert.True(condition: node.TryReadParameter(
            field: "level",
            passName: "convert",
            value: out var fallback
        ));
        Assert.Equal(actual: fallback, expected: 7d);
        Assert.True(condition: node.TryWriteParameter(
            field: "gain",
            passName: "convert",
            value: 0.25d
        ));
        Assert.True(condition: node.TryWriteParameter(
            field: "level",
            passName: "convert",
            value: 41.5d
        ));
        Assert.True(condition: node.TryGetConfigSnapshot(
            bytes: out var block,
            passName: "convert"
        ));
        Assert.Equal(
            actual: BinaryPrimitives.ReadSingleLittleEndian(source: block.AsSpan(start: ((int)OffsetOf(field: "gain", node: node)))),
            expected: 0.25f
        );
        Assert.Equal(
            actual: BinaryPrimitives.ReadInt32LittleEndian(source: block.AsSpan(start: ((int)OffsetOf(field: "level", node: node)))),
            expected: 42
        );
        Assert.Equal(
            actual: BinaryPrimitives.ReadSingleLittleEndian(source: block.AsSpan(start: ((int)OffsetOf(field: "tint", node: node)))),
            expected: 1f
        );
        Assert.True(condition: node.TryReadParameter(
            field: "level",
            passName: "convert",
            value: out var written
        ));
        Assert.Equal(actual: written, expected: 42d);
    }
    [Fact]
    public void AParameterWriteRefusesAVectorFieldAnUnknownFieldAndAnUninstalledPass() {
        var gpu = new FakePipelineGpu();

        using (var empty = Node(
            gpu: gpu,
            pipelines: null
        )) {
            Assert.False(condition: empty.TryWriteParameter(
                field: "gain",
                passName: "convert",
                value: 1d
            ));
        }

        using var node = InstalledNode(
            gpu: gpu,
            pipeline: Feedback(convertConfig: BoundConfig)
        );

        Assert.False(condition: node.TryWriteParameter(field: "tint", passName: "convert", value: 1d));
        Assert.False(condition: node.TryWriteParameter(field: "missing", passName: "convert", value: 1d));
        Assert.False(condition: node.TryWriteParameter(field: "gain", passName: "missing", value: 1d));
        Assert.False(condition: node.TryWriteParameter(field: "gain", passName: "convert", value: double.NaN));
    }
    [Fact]
    public void ParameterWritesAllocateNothing() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            pipeline: Feedback(convertConfig: BoundConfig)
        );

        for (var warm = 0; (warm < 4); warm++) {
            _ = node.TryWriteParameter(field: "gain", passName: "convert", value: warm);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var repetition = 0; (repetition < 64); repetition++) {
            _ = node.TryWriteParameter(field: "gain", passName: "convert", value: repetition);
            _ = node.TryWriteParameter(field: "level", passName: "convert", value: repetition);
        }

        Assert.Equal(expected: 0L, actual: (GC.GetAllocatedBytesForCurrentThread() - before));
    }
}
