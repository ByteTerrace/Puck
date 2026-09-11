using System.Numerics;

using Xunit;

using Puck.SignedDistance;
using Puck.World.Authoring.Sculpting;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <see cref="CreationBuilder"/> enforces its topological invariant (parents declared before children, ids
/// unique) AT AUTHOR TIME rather than deferring to a final pass, interns every distinct rotation by value so two
/// shapes sharing one authored rotation share one <see cref="Quaternion"/> instance, and <see cref="CreationBuilder.Mirror"/>
/// always emits <see cref="CreationBuilder.Left"/> before <see cref="CreationBuilder.Right"/>.
/// </summary>
public sealed class CreationBuilderLawTests {
    private static CreationBuilder Builder() => new(name: "rig");

    /// <summary>A parent declared before its child builds without refusal.</summary>
    [Fact]
    public void ParentDeclaredFirstBuildsCleanly() {
        var builder = Builder();

        builder.Shape(name: "root", type: SdfSolidPrimitive.Box, position: Vector3.Zero, scale: Vector3.One, material: 0);
        builder.Shape(name: "child", type: SdfSolidPrimitive.Sphere, position: Vector3.Zero, scale: Vector3.One, material: 0, parent: "root");

        var document = builder.Build();

        Assert.Equal(expected: 2, actual: document.Shapes!.Count);
        Assert.Equal(expected: "root", actual: document.Shapes![1].Parent);
    }
    /// <summary>A child naming a parent not yet declared is refused BY NAME, not left to a later pass.</summary>
    [Fact]
    public void ParentDeclaredAfterChildIsRefused() {
        var builder = Builder();
        var exception = Assert.Throws<ArgumentException>(testCode: () => builder.Shape(name: "child", type: SdfSolidPrimitive.Sphere, position: Vector3.Zero, scale: Vector3.One, material: 0, parent: "root"));

        Assert.Contains(expectedSubstring: "root", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
    /// <summary>A duplicated shape name is refused by name.</summary>
    [Fact]
    public void DuplicateNameIsRefused() {
        var builder = Builder();

        builder.Shape(name: "a", type: SdfSolidPrimitive.Box, position: Vector3.Zero, scale: Vector3.One, material: 0);

        var exception = Assert.Throws<ArgumentException>(testCode: () => builder.Shape(name: "a", type: SdfSolidPrimitive.Box, position: Vector3.Zero, scale: Vector3.One, material: 0));

        Assert.Contains(expectedSubstring: "'a'", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
    /// <summary>A duplicated explicit id is refused, naming the shape that already holds it.</summary>
    [Fact]
    public void DuplicateIdIsRefused() {
        var builder = Builder();

        builder.Shape(id: 5, name: "a", type: SdfSolidPrimitive.Box, position: Vector3.Zero, scale: Vector3.One, material: 0);

        var exception = Assert.Throws<ArgumentException>(testCode: () => builder.Shape(id: 5, name: "b", type: SdfSolidPrimitive.Box, position: Vector3.Zero, scale: Vector3.One, material: 0));

        Assert.Contains(expectedSubstring: "'a'", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
    /// <summary>A fixed id is honored, and the auto-increment counter skips it without consuming a value.</summary>
    [Fact]
    public void FixedIdsAreHonoredAndSkippedByTheCounter() {
        var builder = Builder();

        builder.FixedId(id: 7, name: "pinned");
        builder.Shape(name: "auto1", type: SdfSolidPrimitive.Box, position: Vector3.Zero, scale: Vector3.One, material: 0);
        builder.Shape(name: "pinned", type: SdfSolidPrimitive.Box, position: Vector3.Zero, scale: Vector3.One, material: 0);
        builder.Shape(name: "auto2", type: SdfSolidPrimitive.Box, position: Vector3.Zero, scale: Vector3.One, material: 0);

        Assert.Equal(expected: 100, actual: builder.Find(name: "auto1").Id);
        Assert.Equal(expected: 7, actual: builder.Find(name: "pinned").Id);
        Assert.Equal(expected: 101, actual: builder.Find(name: "auto2").Id);
    }
    /// <summary>Two calls producing the same rotation (within 4-decimal rounding) intern to the SAME named value.</summary>
    [Fact]
    public void EqualRotationsInternToTheSameName() {
        var builder = Builder();
        var first = builder.AxisAngleDegrees(axis: new Vector3(0, 0, 1), degrees: 90f);
        var second = builder.AxisAngleDegrees(axis: new Vector3(0, 0, 1), degrees: 90f);

        Assert.Equal(expected: first, actual: second);
        Assert.True(condition: builder.TryRotationName(name: out var name, value: first));
        Assert.Equal(expected: "z90", actual: name);
        Assert.Single(collection: builder.NamedRotations, predicate: r => (r.Name == "z90"));
    }
    /// <summary>A negative degree names the "m" (minus) form distinctly from its positive counterpart.</summary>
    [Fact]
    public void NegativeDegreesNameDistinctlyFromPositive() {
        var builder = Builder();

        builder.AxisAngleDegrees(axis: new Vector3(0, 0, 1), degrees: 90f);
        builder.AxisAngleDegrees(axis: new Vector3(0, 0, 1), degrees: -90f);

        Assert.Contains(collection: builder.NamedRotations, filter: r => (r.Name == "z90"));
        Assert.Contains(collection: builder.NamedRotations, filter: r => (r.Name == "zm90"));
    }
    /// <summary><see cref="CreationBuilder.Multiply"/> refuses an operand this builder never interned.</summary>
    [Fact]
    public void MultiplyRefusesAnUninternedOperand() {
        var builder = Builder();
        var foreign = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: 1f);

        Assert.Throws<ArgumentException>(testCode: () => builder.Multiply(a: foreign, b: builder.Identity));
    }
    /// <summary><see cref="CreationBuilder.Mirror"/> always runs <see cref="CreationBuilder.Left"/> before
    /// <see cref="CreationBuilder.Right"/>.</summary>
    [Fact]
    public void MirrorRunsLeftThenRight() {
        var order = new List<(float Sign, string Suffix)>();

        Builder().Mirror(both: side => order.Add(item: (side.Sign, side.Suffix)));

        Assert.Equal(expected: [(1f, "L"), (-1f, "R")], actual: order);
    }
    /// <summary>A shape carrying an un-interned literal rotation is refused, not silently accepted as unhoistable.</summary>
    [Fact]
    public void ShapeRefusesAnUninternedRotation() {
        var builder = Builder();
        var foreign = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: 1f);

        Assert.Throws<ArgumentException>(testCode: () => builder.Shape(name: "a", type: SdfSolidPrimitive.Box, position: Vector3.Zero, rotation: foreign, scale: Vector3.One, material: 0));
    }
    /// <summary><see cref="CreationBuilder.Chain"/> parents each bead to the previous one, root to tip.</summary>
    [Fact]
    public void ChainParentsEachBeadToThePrevious() {
        var builder = Builder();

        builder.Shape(name: "root", type: SdfSolidPrimitive.Box, position: Vector3.Zero, scale: Vector3.One, material: 0);
        var names = builder.Chain(
            count: 3,
            firstParent: "root",
            material: 0,
            nameOf: static i => $"bead{i}",
            positionOf: static i => new Vector3(0, i, 0),
            scaleOf: static _ => Vector3.One,
            type: SdfSolidPrimitive.Sphere
        );

        Assert.Equal(expected: ["bead0", "bead1", "bead2"], actual: names);
        Assert.Equal(expected: "root", actual: builder.Find(name: "bead0").Parent);
        Assert.Equal(expected: "bead0", actual: builder.Find(name: "bead1").Parent);
        Assert.Equal(expected: "bead1", actual: builder.Find(name: "bead2").Parent);
    }
}
