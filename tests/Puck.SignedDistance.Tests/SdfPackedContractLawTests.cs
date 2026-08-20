using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>Laws over the packed-program admission contract: the screen material sentinel band, the
/// <see cref="SdfProgram"/> constructor's lane/range domains, the screen frame's orthonormality, and the immutability
/// of the typed stream the CPU interpreter walks. Every denial is paired with the control that isolates it — the same
/// construction with only the refused property corrected.</summary>
public sealed class SdfPackedContractLawTests {
    private static readonly SdfMaterial[] OneMaterial = [new SdfMaterial(Albedo: Vector3.One)];

    private static SdfProgramBuilder NewBuilder() {
        var builder = new SdfProgramBuilder();

        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return builder;
    }
    private static SdfInstruction Shape(uint shape = ((uint)SdfShapeType.Sphere), uint blend = ((uint)SdfBlendOp.Union), uint material = 0u) => new(
        Blend: blend,
        Data0: new Vector4(
            w: 0f,
            x: 1f,
            y: 0f,
            z: 0f
        ),
        Data1: Vector4.Zero,
        Material: material,
        Op: SdfOp.ShapeBlend,
        Shape: shape
    );
    private static SdfProgram Build(IReadOnlyList<SdfInstruction> instructions, IReadOnlyList<SdfInstanceRange>? instances = null, IReadOnlyList<SdfScreenSurface>? screenSurfaces = null) => new(
        instances: instances,
        instructions: instructions,
        materials: OneMaterial,
        screenSurfaces: screenSurfaces
    );
    private static SdfScreenSurface Screen(int index = 0, Vector3? up = null) => new(
        HalfHeight: 1f,
        HalfWidth: 1f,
        Origin: Vector3.Zero,
        Right: Vector3.UnitX,
        ScreenIndex: index,
        Up: (up ?? Vector3.UnitY)
    );

    /// <summary>An indexed screen sentinel decodes to a direct screenSurfaces/decal index in the shader, so the builder
    /// admits one only for an index a declared surface occupies.</summary>
    [Fact]
    public void BuildRefusesAScreenSentinelNamingAnUndeclaredSurface() {
        var undeclared = NewBuilder()
            .ScreenSlab(
                halfExtents: Vector3.One,
                round: 0f,
                screenIndex: 3,
                worldOrigin: Vector3.Zero,
                worldRight: Vector3.UnitX,
                worldUp: Vector3.UnitY
            )
            .Sphere(
                material: ((SdfProgramBuilder.ScreenMaterialId + 1) + 7),
                radius: 1f
            );

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => undeclared.Build());

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "screen index 7",
            comparisonType: StringComparison.Ordinal
        );

        // Control: the SAME program with the sentinel naming the surface it actually declared builds.
        _ = NewBuilder()
            .ScreenSlab(
                halfExtents: Vector3.One,
                round: 0f,
                screenIndex: 3,
                worldOrigin: Vector3.Zero,
                worldRight: Vector3.UnitX,
                worldUp: Vector3.UnitY
            )
            .Sphere(
                material: ((SdfProgramBuilder.ScreenMaterialId + 1) + 3),
                radius: 1f
            )
            .Build();
    }

    /// <summary>The sentinel band has no top: an arbitrarily large material id decodes to an arbitrarily large screen
    /// index, which is exactly the unguarded read the band's bound exists to refuse.</summary>
    [Fact]
    public void BuildRefusesAnArbitrarilyLargeScreenSentinel() {
        var builder = NewBuilder()
            .Sphere(
                material: int.MaxValue,
                radius: 1f
            );

        _ = Assert.Throws<InvalidOperationException>(testCode: () => builder.Build());

        // Control: the PLAIN sentinel reads no side table at all and stays admissible on any shape.
        _ = NewBuilder()
            .Sphere(
                material: SdfProgramBuilder.ScreenMaterialId,
                radius: 1f
            )
            .Build();
    }

    /// <summary>The typed <see cref="SdfProgram.Instructions"/> seam and the packed <see cref="SdfProgram.Words"/> are
    /// two spellings of one program; a post-construction mutation through a downcast would desync the CPU interpreter
    /// from the GPU without any error.</summary>
    [Fact]
    public void TheTypedStreamCannotBeMutatedThroughADowncast() {
        var program = Build(instructions: [Shape()]);

        _ = Assert.Single(collection: program.Instructions);
        _ = Assert.Throws<NotSupportedException>(testCode: () => ((IList<SdfInstruction>)program.Instructions)[0] = Shape(shape: ((uint)SdfShapeType.Box)));
        _ = Assert.Throws<NotSupportedException>(testCode: () => ((IList<SdfInstanceRange>)program.Instances).Clear());
        _ = Assert.Throws<NotSupportedException>(testCode: () => ((IList<SdfScreenSurface>)program.ScreenSurfaces).Clear());

        // Control: reading the same seam still works, and still describes the packed stream.
        Assert.Equal(
            actual: ((SdfShapeType)program.Instructions[0].Shape),
            expected: SdfShapeType.Sphere
        );
    }

    /// <summary>The constructor snapshots its inputs, so a list whose enumerator and indexer disagree cannot pack words
    /// describing a different program from the one the typed seam reports.</summary>
    [Fact]
    public void TheTypedStreamAndThePackedWordsAgreeForAHostileInputList() {
        var hostile = new AlternatingList(
            enumerated: Shape(),
            indexed: Shape(shape: ((uint)SdfShapeType.Box))
        );
        var program = Build(instructions: hostile);
        // Header word 0 is (instructionCount, ...); instruction headers start one uvec4 in, lane 1 = the shape.
        var packedShape = program.Words[5];

        Assert.Equal(
            actual: packedShape,
            expected: program.Instructions[0].Shape
        );

        // Control: an honest list packs the shape it reports, so the assertion above is not vacuous.
        var honest = Build(instructions: [Shape(shape: ((uint)SdfShapeType.Box))]);

        Assert.Equal(
            actual: honest.Words[5],
            expected: ((uint)SdfShapeType.Box)
        );
    }

    [Fact]
    public void TheConstructorRefusesAnInstanceRangeOutsideTheStream() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Build(
            instances: [new SdfInstanceRange(
                Center: Vector3.Zero,
                End: 0,
                First: 1,
                IsDynamic: false,
                Radius: 1f,
                Slot: 0
            )],
            instructions: [Shape()]
        ));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Build(
            instances: [new SdfInstanceRange(
                Center: Vector3.Zero,
                End: 9,
                First: 0,
                IsDynamic: false,
                Radius: 1f,
                Slot: 0
            )],
            instructions: [Shape()]
        ));

        // Control: the same instance covering the stream it owns is admitted.
        _ = Build(
            instances: [new SdfInstanceRange(
                Center: Vector3.Zero,
                End: 1,
                First: 0,
                IsDynamic: false,
                Radius: 1f,
                Slot: 0
            )],
            instructions: [Shape()]
        );
    }

    [Fact]
    public void TheConstructorRefusesAnUndeclaredShapeBlendOrMaterialLane() {
        _ = Assert.Throws<ArgumentException>(testCode: () => Build(instructions: [Shape(shape: 250u)]));
        _ = Assert.Throws<ArgumentException>(testCode: () => Build(instructions: [Shape(blend: 250u)]));
        _ = Assert.Throws<ArgumentException>(testCode: () => Build(instructions: [Shape(material: 4242u)]));
        _ = Assert.Throws<ArgumentException>(testCode: () => Build(instructions: [Shape(material: (((uint)SdfProgramBuilder.ScreenMaterialId) + 1u))]));

        // Control: every lane inside its domain, and the plain screen sentinel, are admitted.
        _ = Build(instructions: [Shape(
            blend: ((uint)SdfBlendOp.SmoothUnion),
            material: 0u,
            shape: ((uint)SdfShapeType.Box)
        )]);
        _ = Build(instructions: [Shape(material: ((uint)SdfProgramBuilder.ScreenMaterialId))]);
    }

    [Fact]
    public void TheConstructorRefusesAnUnpackableScreenIndex() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Build(
            instructions: [Shape()],
            screenSurfaces: [Screen(index: SdfProgramBuilder.MaxScreenSurfaces)]
        ));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Build(
            instructions: [Shape()],
            screenSurfaces: [Screen(index: -1)]
        ));

        // The packed table is indexed BY screen index, so a duplicate is one surface silently overwriting another.
        _ = Assert.Throws<ArgumentException>(testCode: () => Build(
            instructions: [Shape()],
            screenSurfaces: [Screen(index: 3), Screen(index: 3)]
        ));

        // Control: two distinct in-range indices pack two distinct slots.
        _ = Build(
            instructions: [Shape()],
            screenSurfaces: [Screen(index: 3), Screen(index: 4)]
        );
    }

    /// <summary>The shader projects a hit onto the frame's two axes while the slab's geometry and the collider ride the
    /// frame derived from them, so only an orthonormal pair describes one solid.</summary>
    [Fact]
    public void TheConstructorRefusesASkewedScreenFrame() {
        var skew = Vector3.Normalize(value: new Vector3(
            x: 1f,
            y: 1f,
            z: 0f
        ));

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Build(
            instructions: [Shape()],
            screenSurfaces: [Screen(up: skew)]
        ));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Build(
            instructions: [Shape()],
            screenSurfaces: [Screen(up: (2f * Vector3.UnitY))]
        ));

        // Control: the orthonormal pair the skewed one is a rotation away from is admitted.
        _ = Build(
            instructions: [Shape()],
            screenSurfaces: [Screen(up: Vector3.UnitY)]
        );
    }

    [Fact]
    public void ScreenSlabRefusesASkewedFrame() {
        var builder = NewBuilder();
        var skew = Vector3.Normalize(value: new Vector3(
            x: 1f,
            y: 1f,
            z: 0f
        ));

        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.ScreenSlab(
            halfExtents: Vector3.One,
            round: 0f,
            screenIndex: 0,
            worldOrigin: Vector3.Zero,
            worldRight: Vector3.UnitX,
            worldUp: skew
        ));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "orthogonal",
            comparisonType: StringComparison.Ordinal
        );

        // Control: the orthogonal pair builds, and the packed surface keeps the axes it was given.
        var program = NewBuilder()
            .ScreenSlab(
                halfExtents: Vector3.One,
                round: 0f,
                screenIndex: 0,
                worldOrigin: Vector3.Zero,
                worldRight: Vector3.UnitX,
                worldUp: Vector3.UnitY
            )
            .Build();

        Assert.Equal(
            actual: program.ScreenSurfaces[0].Up,
            expected: Vector3.UnitY
        );
    }

    // A list whose enumerator and indexer report different instructions: the shape a lazily-projected or concurrently
    // mutated IReadOnlyList can take between two reads of the same index.
    private sealed class AlternatingList(SdfInstruction enumerated, SdfInstruction indexed) : IReadOnlyList<SdfInstruction> {
        public int Count => 1;

        public SdfInstruction this[int index] => indexed;

        public IEnumerator<SdfInstruction> GetEnumerator() {
            yield return enumerated;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
