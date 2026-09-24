using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class PartProgramLawTests {
    private static SdfProgram Build(List<SdfInstruction> instructions, SdfInstanceRange[] instances) => new(
        instructions: instructions,
        instances: instances,
        materials: Enumerable.Repeat(
            new SdfMaterial(Albedo: Vector3.One),
            4
        ).ToArray(),
        screenSurfaces: null
    );
    private static SdfInstruction Op(SdfOp op, Vector4 data = default) => new(
        Op: op,
        Shape: 0,
        Blend: ((uint)SdfBlendOp.Union),
        Material: 0,
        Data0: data,
        Data1: Vector4.Zero
    );
    private static int PartTable(ReadOnlySpan<uint> words) {
        var segment = (((int)((words[3] + (20u * words[1])) + (2u * words[0]))) * 4);
        var instances = ((segment + 4) + (8 * ((int)words[segment])));

        return (((int)words[(instances + 1)]) * 4);
    }
    private static SdfInstanceRange Range(int first, int end) => new(
        First: first,
        End: end,
        IsDynamic: true,
        Center: Vector3.Zero,
        Radius: 8,
        Slot: 0
    );
    private static List<SdfInstruction> Scope(float outer, float inner, int slotA, int slotB, uint materialA, uint materialB) => [
        Op(SdfOp.PushField), Op(SdfOp.ResetPoint), Op(
            SdfOp.TransformDynamic,
            new Vector4(
                w: 0,
                x: slotA,
                y: 0,
                z: 0
            )
        ),
        Shape(
            material: materialA,
            radius: outer
        ), Op(SdfOp.ResetPoint), Op(
            SdfOp.TransformDynamic,
            new Vector4(
                w: 0,
                x: slotB,
                y: 0,
                z: 0
            )
        ),
        Shape(
            material: materialB,
            radius: inner
        ) with { Blend = ((uint)SdfBlendOp.Subtraction) }, Op(SdfOp.PopField)
    ];
    private static SdfInstruction Shape(float radius, uint material) => Op(
        SdfOp.ShapeBlend,
        new Vector4(
            w: 0,
            x: radius,
            y: 0,
            z: 0
        )
    )
        with { Shape = ((uint)SdfShapeType.Sphere), Material = material };

    [Fact]
    public void CapacityDoesNotDependOnSharedGeometry() {
        var shared = Scope(
            inner: 1,
            materialA: 0,
            materialB: 1,
            outer: 2,
            slotA: 1,
            slotB: 2
        );

        shared.AddRange(collection: Scope(
            inner: 1,
            materialA: 2,
            materialB: 3,
            outer: 2,
            slotA: 7,
            slotB: 8
        ));
        var distinct = Scope(
            inner: 1,
            materialA: 0,
            materialB: 1,
            outer: 2,
            slotA: 1,
            slotB: 2
        );

        distinct.AddRange(collection: Scope(
            inner: 1,
            materialA: 2,
            materialB: 3,
            outer: 3,
            slotA: 7,
            slotB: 8
        ));
        var a = Build(
            shared,
            [Range(
                    end: 8,
                    first: 0
                ), Range(
                    end: 16,
                    first: 8
                )]
        );
        var b = Build(
            distinct,
            [Range(
                    end: 8,
                    first: 0
                ), Range(
                    end: 16,
                    first: 8
                )]
        );

        Assert.True(condition: (a.Words.Length < b.Words.Length));
        Assert.Equal(
            a.PartCompilationWordCapacity,
            b.PartCompilationWordCapacity
        );
        Assert.True(condition: (a.PartCompilationWordCapacity >= b.Words.Length));
    }
    [Fact]
    public void DifferentGeometryDoesNotAlias() {
        var instructions = Scope(
            inner: 1,
            materialA: 0,
            materialB: 1,
            outer: 2,
            slotA: 1,
            slotB: 2
        );

        instructions.AddRange(collection: Scope(
            inner: 1,
            materialA: 2,
            materialB: 3,
            outer: 3,
            slotA: 7,
            slotB: 8
        ));
        var words = Build(
            instructions,
            [Range(
                    end: 8,
                    first: 0
                ), Range(
                    end: 16,
                    first: 8
                )]
        ).Words;
        var table = PartTable(words: words);

        Assert.Equal(
            2u,
            words[(table + 1)]
        );
        Assert.NotEqual(
            words[(table + 4)],
            words[(table + 8)]
        );
    }
    [Fact]
    public void DomainParametersPreventIncorrectSharing() {
        var first = Scope(
            inner: 1,
            materialA: 0,
            materialB: 1,
            outer: 2,
            slotA: 1,
            slotB: 2
        );
        var second = Scope(
            inner: 1,
            materialA: 2,
            materialB: 3,
            outer: 2,
            slotA: 7,
            slotB: 8
        );

        first.Insert(
            index: 3,
            item: Op(
                SdfOp.Scale,
                new Vector4(
                    w: 1,
                    x: 2,
                    y: 1,
                    z: 1
                )
            )
        );
        second.Insert(
            index: 3,
            item: Op(
                SdfOp.Scale,
                new Vector4(
                    w: 1,
                    x: 3,
                    y: 1,
                    z: 1
                )
            )
        );
        first.AddRange(collection: second);
        var words = Build(
            first,
            [Range(
                    end: 9,
                    first: 0
                ), Range(
                    end: 18,
                    first: 9
                )]
        ).Words;

        Assert.Equal(
            2u,
            words[(PartTable(words: words) + 1)]
        );
    }
    [Fact]
    public void GeometryIsSharedWhilePoseAndMaterialBindingsRemainIndependent() {
        var instructions = Scope(
            inner: 1,
            materialA: 0,
            materialB: 1,
            outer: 2,
            slotA: 1,
            slotB: 2
        );

        instructions.AddRange(collection: Scope(
            inner: 1,
            materialA: 2,
            materialB: 3,
            outer: 2,
            slotA: 7,
            slotB: 8
        ));
        var words = Build(
            instructions,
            [Range(
                    end: 8,
                    first: 0
                ), Range(
                    end: 16,
                    first: 8
                )]
        ).Words;
        var table = PartTable(words: words);

        Assert.NotEqual(
            actual: table,
            expected: 0
        );
        Assert.Equal(
            0x80000002u,
            words[table]
        );
        Assert.Equal(
            1u,
            words[(table + 1)]
        );
        Assert.Equal(
            2u,
            words[(table + 2)]
        );
        Assert.Equal(
            4u,
            words[(table + 3)]
        );
        Assert.Equal(
            words[(table + 4)],
            words[(table + 8)]
        );
        var firstBindings = (((int)words[(table + 5)]) * 4);
        var secondBindings = (((int)words[(table + 9)]) * 4);

        Assert.Equal(
            2u,
            words[firstBindings]
        );
        Assert.Equal(
            8u,
            words[secondBindings]
        );
        Assert.Equal(
            0u,
            words[(firstBindings + 1)]
        );
        Assert.Equal(
            2u,
            words[(secondBindings + 1)]
        );
        Assert.Equal(
            0x80000002u,
            words[(table + 6)]
        );
    }
    [Fact]
    public void IneligibleProbeStillReservesRoomForLaterCompiledParts() {
        var fallback = Scope(
            inner: 1,
            materialA: 0,
            materialB: 1,
            outer: 2,
            slotA: 1,
            slotB: 2
        );

        fallback.Insert(
            index: 3,
            item: Op(
                SdfOp.Translate,
                new Vector4(
                    w: 0,
                    x: 1,
                    y: 0,
                    z: 0
                )
            )
        );
        var compiled = Scope(
            inner: 1,
            materialA: 0,
            materialB: 1,
            outer: 2,
            slotA: 1,
            slotB: 2
        );

        compiled.Insert(
            index: 3,
            item: Op(
                SdfOp.Scale,
                new Vector4(
                    w: 1,
                    x: 2,
                    y: 1,
                    z: 1
                )
            )
        );
        var a = Build(
            fallback,
            [Range(
                    end: 9,
                    first: 0
                )]
        );
        var b = Build(
            compiled,
            [Range(
                    end: 9,
                    first: 0
                )]
        );

        Assert.Equal(
            0,
            PartTable(words: a.Words)
        );
        Assert.NotEqual(
            0,
            PartTable(words: b.Words)
        );
        Assert.True(condition: (a.PartCompilationWordCapacity >= b.Words.Length));
    }
    [Fact]
    public void NonUnionGenericScopeDisablesIndependentTracingOfOtherParts() {
        var instructions = Scope(
            inner: 1,
            materialA: 0,
            materialB: 1,
            outer: 2,
            slotA: 1,
            slotB: 2
        );
        var other = Scope(
            inner: 1,
            materialA: 2,
            materialB: 3,
            outer: 2,
            slotA: 3,
            slotB: 4
        );

        other[^1] = other[^1] with { Blend = ((uint)SdfBlendOp.Subtraction) };
        instructions.AddRange(collection: other);
        var words = Build(
            instructions,
            [Range(
                    end: 8,
                    first: 0
                ), Range(
                    end: 16,
                    first: 8
                )]
        ).Words;

        Assert.Equal(
            1u,
            words[PartTable(words: words)]
        );
    }
    [InlineData(SdfBlendOp.SmoothUnion)]
    [InlineData(SdfBlendOp.Subtraction)]
    [InlineData(SdfBlendOp.Intersection)]
    [Theory]
    public void NonUnionParentCompositionKeepsTheReferenceProgram(SdfBlendOp blend) {
        var instructions = Scope(
            inner: 1,
            materialA: 0,
            materialB: 1,
            outer: 2,
            slotA: 1,
            slotB: 2
        );

        instructions[^1] = instructions[^1] with {
            Blend = ((uint)blend),
            Data1 = new Vector4(
            w: 0f,
            x: 0.2f,
            y: 0f,
            z: 0f
        ),
        };
        Assert.Equal(
            0,
            PartTable(words: Build(
                instructions,
                [Range(
                        end: 8,
                        first: 0
                    )]
            ).Words)
        );
    }
    [Fact]
    public void ParkedPartDoesNotProduceBindings() {
        Assert.Equal(
            0,
            PartTable(words: Build(
                Scope(
                    inner: 1,
                    materialA: 0,
                    materialB: 1,
                    outer: 2,
                    slotA: 1,
                    slotB: 2
                ),
                [Range(
                        end: 8,
                        first: 0
                    ) with { Active = false }]
            ).Words)
        );
    }
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [Theory]
    public void ParticipationFlagsArePartOfGeometryIdentity(bool detail, bool secondary) {
        var instructions = Scope(
            inner: 1,
            materialA: 0,
            materialB: 1,
            outer: 2,
            slotA: 1,
            slotB: 2
        );
        var second = Scope(
            inner: 1,
            materialA: 2,
            materialB: 3,
            outer: 2,
            slotA: 7,
            slotB: 8
        );

        second[3] = second[3] with { Detail = detail, Secondary = secondary };
        instructions.AddRange(collection: second);
        var words = Build(
            instructions,
            [Range(
                    end: 8,
                    first: 0
                ), Range(
                    end: 16,
                    first: 8
                )]
        ).Words;
        var table = PartTable(words: words);

        Assert.Equal(
            2u,
            words[(table + 1)]
        );
        var leaf = (((int)words[(table + 8)]) * 4);
        var shape = (((int)(words[leaf] + 1u)) * 4);

        Assert.Equal(
            ((uint)SdfShapeType.Sphere) | (detail
            ? 0x80000000u
            : 0u)
            | (secondary
            ? 0u
            : 0x40000000u),
            words[(shape + 1)]
        );
        var segment = (((int)((words[3] + (20u * words[1])) + (2u * words[0]))) * 4);
        var instances = ((segment + 4) + (8 * ((int)words[segment])));

        Assert.Equal(
            (detail
            ? 0u
            : 1u),
            words[(instances + 2)]
        );
    }
    [Fact]
    public void PointStateUsedAfterTheScopePreventsCompilation() {
        var instructions = Scope(
            inner: 1,
            materialA: 0,
            materialB: 1,
            outer: 2,
            slotA: 1,
            slotB: 2
        );

        instructions.Add(item: Shape(
            material: 0,
            radius: 1
        ));
        Assert.Equal(
            0,
            PartTable(words: Build(
                instructions,
                [Range(
                        end: 8,
                        first: 0
                    )]
            ).Words)
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void PolygonSharingUsesVertexContentInsteadOfItsPackedPointer(bool different) {
        var first = Scope(
            inner: 1,
            materialA: 0,
            materialB: 1,
            outer: 2,
            slotA: 1,
            slotB: 2
        );
        var second = Scope(
            inner: 1,
            materialA: 2,
            materialB: 3,
            outer: 2,
            slotA: 7,
            slotB: 8
        );

        first[3] = first[3] with {
            Shape = ((uint)SdfShapeType.ConvexPolygon),
            Data0 = new Vector4(
            w: 1,
            x: 0,
            y: 0,
            z: 0
        ),
            Data1 = new Vector4(
            w: 0,
            x: 0,
            y: 1,
            z: 0
        ),
        };
        second[3] = first[3] with { Material = 2 };
        first.AddRange(collection: second);
        Vector2[] vertices = [new(
                x: -1,
                y: -1
            ), new(
                x: -1,
                y: 1
            ), new(
                x: 1,
                y: 1
            ), new(
                x: 1,
                y: -1
            )];
        var other = vertices.Select(selector: vertex => (vertex * (different
            ? 2f
            : 1f))).ToArray();
        var words = new SdfProgram(
            instructions: first,
            instances: [Range(
                    end: 8,
                    first: 0
                ), Range(
                    end: 16,
                    first: 8
                )],
            materials: Enumerable.Repeat(
                new SdfMaterial(Albedo: Vector3.One),
                4
            ).ToArray(),
            convexPolygonProfiles: [(3, vertices), (11, other)]
        ).Words;
        var table = PartTable(words: words);

        Assert.Equal(
            (different
            ? 2u
            : 1u),
            words[(table + 1)]
        );
        var data = (((int)words[2]) * 4);

        Assert.NotEqual(
            words[(data + (3 * 8))],
            words[(data + (11 * 8))]
        );
    }
    [InlineData(SdfBlendOp.Union, true)]
    [InlineData(SdfBlendOp.SmoothUnion, false)]
    [InlineData(SdfBlendOp.Subtraction, false)]
    [InlineData(SdfBlendOp.Intersection, false)]
    [Theory]
    public void RootCompositionControlsIndependentTracingWithoutDiscardingCompiledParts(SdfBlendOp blend, bool independent) {
        var instructions = Scope(
            inner: 1,
            materialA: 0,
            materialB: 1,
            outer: 2,
            slotA: 1,
            slotB: 2
        );

        instructions.Add(item: Op(SdfOp.ResetPoint));
        instructions.Add(item: Shape(
            material: 0,
            radius: 1
        ) with {
            Blend = ((uint)blend),
            Data1 = new Vector4(
            w: 0,
            x: 0.2f,
            y: 0,
            z: 0
        ),
        });
        var words = Build(
            instructions,
            [Range(
                    end: 8,
                    first: 0
                )]
        ).Words;
        var header = words[PartTable(words: words)];

        Assert.Equal(
            actual: header & 0x7FFFFFFFu,
            expected: 1u
        );
        Assert.Equal(
            actual: ((header & 0x80000000u) != 0),
            expected: independent
        );
    }
    [InlineData(SdfOp.Onion)]
    [InlineData(SdfOp.Dilate)]
    [Theory]
    public void RootFieldModifierDisablesIndependentTracingWithoutDiscardingCompiledParts(SdfOp modifier) {
        var instructions = Scope(
            inner: 1,
            materialA: 0,
            materialB: 1,
            outer: 2,
            slotA: 1,
            slotB: 2
        );

        instructions.Add(item: Op(SdfOp.ResetPoint));
        instructions.Add(item: Op(
            modifier,
            new Vector4(
                w: 0,
                x: 0.2f,
                y: 0,
                z: 0
            )
        ));
        instructions.Add(item: Shape(
            material: 0,
            radius: 1
        ));
        var words = Build(
            instructions,
            [Range(
                    end: 8,
                    first: 0
                )]
        ).Words;

        Assert.Equal(
            1u,
            words[PartTable(words: words)]
        );
    }
    [Fact]
    public void UnsupportedTransformChainRetainsReferenceExecution() {
        var instructions = Scope(
            inner: 1,
            materialA: 0,
            materialB: 1,
            outer: 2,
            slotA: 1,
            slotB: 2
        );

        instructions.Insert(
            index: 3,
            item: Op(
                SdfOp.Translate,
                new Vector4(
                    w: 0,
                    x: 1,
                    y: 0,
                    z: 0
                )
            )
        );
        Assert.Equal(
            0,
            PartTable(words: Build(
                instructions,
                [Range(
                        end: 9,
                        first: 0
                    )]
            ).Words)
        );
    }
}
