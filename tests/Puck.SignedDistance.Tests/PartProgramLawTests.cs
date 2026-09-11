using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class PartProgramLawTests {
    [Fact]
    public void GeometryIsSharedWhilePoseAndMaterialBindingsRemainIndependent() {
        var instructions = Scope(2, 1, 1, 2, 0, 1);
        instructions.AddRange(Scope(2, 1, 7, 8, 2, 3));
        var words = Build(instructions, [Range(0, 8), Range(8, 16)]).Words;
        var table = PartTable(words);
        Assert.NotEqual(0, table);
        Assert.Equal(0x80000002u, words[table]);
        Assert.Equal(1u, words[table + 1]);
        Assert.Equal(2u, words[table + 2]);
        Assert.Equal(4u, words[table + 3]);
        Assert.Equal(words[table + 4], words[table + 8]);
        var firstBindings = (int)words[table + 5] * 4;
        var secondBindings = (int)words[table + 9] * 4;
        Assert.Equal(2u, words[firstBindings]);
        Assert.Equal(8u, words[secondBindings]);
        Assert.Equal(0u, words[firstBindings + 1]);
        Assert.Equal(2u, words[secondBindings + 1]);
        Assert.Equal(0x80000002u, words[table + 6]);
    }

    [Fact]
    public void DifferentGeometryDoesNotAlias() {
        var instructions = Scope(2, 1, 1, 2, 0, 1);
        instructions.AddRange(Scope(3, 1, 7, 8, 2, 3));
        var words = Build(instructions, [Range(0, 8), Range(8, 16)]).Words;
        var table = PartTable(words);
        Assert.Equal(2u, words[table + 1]);
        Assert.NotEqual(words[table + 4], words[table + 8]);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void ParticipationFlagsArePartOfGeometryIdentity(bool detail, bool secondary) {
        var instructions = Scope(2, 1, 1, 2, 0, 1);
        var second = Scope(2, 1, 7, 8, 2, 3);
        second[3] = second[3] with { Detail = detail, Secondary = secondary };
        instructions.AddRange(second);
        var words = Build(instructions, [Range(0, 8), Range(8, 16)]).Words;
        var table = PartTable(words);
        Assert.Equal(2u, words[table + 1]);
        var leaf = (int)words[table + 8] * 4;
        var shape = (int)(words[leaf] + 1u) * 4;
        Assert.Equal((uint)SdfShapeType.Sphere | (detail ? 0x80000000u : 0u)
            | (secondary ? 0u : 0x40000000u), words[shape + 1]);
        var segment = (int)(words[3] + 20u * words[1] + 2u * words[0]) * 4;
        var instances = segment + 4 + 8 * (int)words[segment];
        Assert.Equal(detail ? 0u : 1u, words[instances + 2]);
    }

    [Theory]
    [InlineData(SdfBlendOp.SmoothUnion)]
    [InlineData(SdfBlendOp.Subtraction)]
    [InlineData(SdfBlendOp.Intersection)]
    public void NonUnionParentCompositionKeepsTheReferenceProgram(SdfBlendOp blend) {
        var instructions = Scope(2, 1, 1, 2, 0, 1);
        instructions[^1] = instructions[^1] with { Blend = (uint)blend, Data1 = new Vector4(0.2f, 0f, 0f, 0f) };
        Assert.Equal(0, PartTable(Build(instructions, [Range(0, 8)]).Words));
    }

    [Fact]
    public void PointStateUsedAfterTheScopePreventsCompilation() {
        var instructions = Scope(2, 1, 1, 2, 0, 1);
        instructions.Add(Shape(1, 0));
        Assert.Equal(0, PartTable(Build(instructions, [Range(0, 8)]).Words));
    }

    [Fact]
    public void ParkedPartDoesNotProduceBindings() {
        Assert.Equal(0, PartTable(Build(Scope(2, 1, 1, 2, 0, 1), [Range(0, 8) with { Active = false }]).Words));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PolygonSharingUsesVertexContentInsteadOfItsPackedPointer(bool different) {
        var first = Scope(2, 1, 1, 2, 0, 1);
        var second = Scope(2, 1, 7, 8, 2, 3);
        first[3] = first[3] with { Shape = (uint)SdfShapeType.ConvexPolygon,
            Data0 = new Vector4(0, 0, 0, 1), Data1 = new Vector4(0, 1, 0, 0) };
        second[3] = first[3] with { Material = 2 };
        first.AddRange(second);
        Vector2[] vertices = [new(-1, -1), new(-1, 1), new(1, 1), new(1, -1)];
        var other = vertices.Select(vertex => vertex * (different ? 2f : 1f)).ToArray();
        var words = new SdfProgram(instructions: first, instances: [Range(0, 8), Range(8, 16)],
            materials: Enumerable.Repeat(new SdfMaterial(Albedo: Vector3.One), 4).ToArray(),
            convexPolygonProfiles: [(3, vertices), (11, other)]).Words;
        var table = PartTable(words);
        Assert.Equal(different ? 2u : 1u, words[table + 1]);
        var data = (int)words[2] * 4;
        Assert.NotEqual(words[data + 3 * 8], words[data + 11 * 8]);
    }

    [Fact]
    public void DomainParametersPreventIncorrectSharing() {
        var first = Scope(2, 1, 1, 2, 0, 1);
        var second = Scope(2, 1, 7, 8, 2, 3);
        first.Insert(3, Op(SdfOp.Scale, new Vector4(2, 1, 1, 1)));
        second.Insert(3, Op(SdfOp.Scale, new Vector4(3, 1, 1, 1)));
        first.AddRange(second);
        var words = Build(first, [Range(0, 9), Range(9, 18)]).Words;
        Assert.Equal(2u, words[PartTable(words) + 1]);
    }

    [Fact]
    public void UnsupportedTransformChainRetainsReferenceExecution() {
        var instructions = Scope(2, 1, 1, 2, 0, 1);
        instructions.Insert(3, Op(SdfOp.Translate, new Vector4(1, 0, 0, 0)));
        Assert.Equal(0, PartTable(Build(instructions, [Range(0, 9)]).Words));
    }

    [Fact]
    public void CapacityDoesNotDependOnSharedGeometry() {
        var shared = Scope(2, 1, 1, 2, 0, 1);
        shared.AddRange(Scope(2, 1, 7, 8, 2, 3));
        var distinct = Scope(2, 1, 1, 2, 0, 1);
        distinct.AddRange(Scope(3, 1, 7, 8, 2, 3));
        var a = Build(shared, [Range(0, 8), Range(8, 16)]);
        var b = Build(distinct, [Range(0, 8), Range(8, 16)]);
        Assert.True(a.Words.Length < b.Words.Length);
        Assert.Equal(a.PartCompilationWordCapacity, b.PartCompilationWordCapacity);
        Assert.True(a.PartCompilationWordCapacity >= b.Words.Length);
    }

    [Fact]
    public void IneligibleProbeStillReservesRoomForLaterCompiledParts() {
        var fallback = Scope(2, 1, 1, 2, 0, 1);
        fallback.Insert(3, Op(SdfOp.Translate, new Vector4(1, 0, 0, 0)));
        var compiled = Scope(2, 1, 1, 2, 0, 1);
        compiled.Insert(3, Op(SdfOp.Scale, new Vector4(2, 1, 1, 1)));
        var a = Build(fallback, [Range(0, 9)]);
        var b = Build(compiled, [Range(0, 9)]);
        Assert.Equal(0, PartTable(a.Words));
        Assert.NotEqual(0, PartTable(b.Words));
        Assert.True(a.PartCompilationWordCapacity >= b.Words.Length);
    }

    [Theory]
    [InlineData(SdfBlendOp.Union, true)]
    [InlineData(SdfBlendOp.SmoothUnion, false)]
    [InlineData(SdfBlendOp.Subtraction, false)]
    [InlineData(SdfBlendOp.Intersection, false)]
    public void RootCompositionControlsIndependentTracingWithoutDiscardingCompiledParts(SdfBlendOp blend, bool independent) {
        var instructions = Scope(2, 1, 1, 2, 0, 1);
        instructions.Add(Op(SdfOp.ResetPoint));
        instructions.Add(Shape(1, 0) with { Blend = (uint)blend, Data1 = new Vector4(0.2f, 0, 0, 0) });
        var words = Build(instructions, [Range(0, 8)]).Words;
        var header = words[PartTable(words)];
        Assert.Equal(1u, header & 0x7FFFFFFFu);
        Assert.Equal(independent, (header & 0x80000000u) != 0);
    }

    [Theory]
    [InlineData(SdfOp.Onion)]
    [InlineData(SdfOp.Dilate)]
    public void RootFieldModifierDisablesIndependentTracingWithoutDiscardingCompiledParts(SdfOp modifier) {
        var instructions = Scope(2, 1, 1, 2, 0, 1);
        instructions.Add(Op(SdfOp.ResetPoint));
        instructions.Add(Op(modifier, new Vector4(0.2f, 0, 0, 0)));
        instructions.Add(Shape(1, 0));
        var words = Build(instructions, [Range(0, 8)]).Words;
        Assert.Equal(1u, words[PartTable(words)]);
    }

    [Fact]
    public void NonUnionGenericScopeDisablesIndependentTracingOfOtherParts() {
        var instructions = Scope(2, 1, 1, 2, 0, 1);
        var other = Scope(2, 1, 3, 4, 2, 3);
        other[^1] = other[^1] with { Blend = (uint)SdfBlendOp.Subtraction };
        instructions.AddRange(other);
        var words = Build(instructions, [Range(0, 8), Range(8, 16)]).Words;
        Assert.Equal(1u, words[PartTable(words)]);
    }

    private static List<SdfInstruction> Scope(float outer, float inner, int slotA, int slotB, uint materialA, uint materialB) => [
        Op(SdfOp.PushField), Op(SdfOp.ResetPoint), Op(SdfOp.TransformDynamic, new Vector4(slotA, 0, 0, 0)),
        Shape(outer, materialA), Op(SdfOp.ResetPoint), Op(SdfOp.TransformDynamic, new Vector4(slotB, 0, 0, 0)),
        Shape(inner, materialB) with { Blend = (uint)SdfBlendOp.Subtraction }, Op(SdfOp.PopField)
    ];

    private static SdfInstruction Shape(float radius, uint material) => Op(SdfOp.ShapeBlend, new Vector4(radius, 0, 0, 0))
        with { Shape = (uint)SdfShapeType.Sphere, Material = material };
    private static SdfInstruction Op(SdfOp op, Vector4 data = default) => new(
        Op: op, Shape: 0, Blend: (uint)SdfBlendOp.Union, Material: 0, Data0: data, Data1: Vector4.Zero);
    private static SdfInstanceRange Range(int first, int end) => new(
        First: first, End: end, IsDynamic: true, Center: Vector3.Zero, Radius: 8, Slot: 0);
    private static SdfProgram Build(List<SdfInstruction> instructions, SdfInstanceRange[] instances) => new(
        instructions: instructions, instances: instances,
        materials: Enumerable.Repeat(new SdfMaterial(Albedo: Vector3.One), 4).ToArray(), screenSurfaces: null);
    private static int PartTable(ReadOnlySpan<uint> words) {
        var segment = (int)(words[3] + 20u * words[1] + 2u * words[0]) * 4;
        var instances = segment + 4 + 8 * (int)words[segment];
        return (int)words[instances + 1] * 4;
    }
}
