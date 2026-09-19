using System.Reflection;
using Xunit;

namespace Puck.State.Vectors.Tests;

/// <summary>CONTRACT UNDER TEST: the typed view opens only over a stored vector row, reads and writes exactly the
/// components the arena's own doors do, and carries the same dimension count for every cell of its row; and the
/// union this project declares is closed the way every union in the tree is.</summary>
public sealed class VectorColumnLawTests {
    [Fact]
    public void AColumnOpensOnlyOverAStoredVectorRow() {
        var arena = VectorArenaFixture.Arena(catalog: out _);

        Assert.True(condition: VectorColumn.TryOpen(
            arena: arena,
            column: out var column,
            refusal: out _,
            rowOrdinal: VectorArenaFixture.Memories
        ));
        Assert.Equal(
            actual: column.Dimensions,
            expected: 8
        );
        Assert.Equal(
            actual: column.Count,
            expected: 3
        );
        Assert.Equal(
            actual: column.Shape,
            expected: RowShape.Keyed
        );
        Assert.False(condition: VectorColumn.TryOpen(
            arena: arena,
            column: out _,
            refusal: out var notVector,
            rowOrdinal: VectorArenaFixture.Count
        ));
        Assert.Equal(
            actual: notVector.Code,
            expected: RuleRefusal.VectorOperandNotVector
        );
        Assert.False(condition: VectorColumn.TryOpen(
            arena: arena,
            column: out _,
            refusal: out var unknown,
            rowOrdinal: arena.Layout.RowCount
        ));
        Assert.Equal(
            actual: unknown.Code,
            expected: RuleRefusal.StateRowUnknown
        );
    }
    // The view is a typed door onto the same storage, not a copy of it: what it reads is what the arena's own
    // vector door reads, and what it writes the arena reads back.
    [Fact]
    public void AColumnReadsAndWritesTheSameComponentsTheArenaDoes() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);

        Assert.True(condition: VectorColumn.TryOpen(
            arena: arena,
            column: out var column,
            refusal: out _,
            rowOrdinal: VectorArenaFixture.Memories
        ));

        var north = VectorArenaFixture.Key(
            catalog: catalog,
            value: "north"
        );

        Assert.True(condition: column.TryRead(
            components: out var viewed,
            key: north
        ));
        Assert.True(condition: column.TryReadMemory(
            components: out var opaque,
            key: north
        ));
        Assert.Equal(
            actual: opaque.ToArray(),
            expected: viewed.ToArray()
        );
        Assert.Equal(
            actual: VectorArenaFixture.Read(
                arena: arena,
                key: north,
                rowOrdinal: VectorArenaFixture.Memories
            ),
            expected: viewed.ToArray()
        );

        var replacement = VectorArenaFixture.Unit(axis: 5);

        Assert.True(condition: column.TryWrite(
            components: replacement.Components,
            key: north,
            reason: out var reason
        ), userMessage: reason);
        Assert.Equal(
            actual: VectorArenaFixture.Read(
                arena: arena,
                key: north,
                rowOrdinal: VectorArenaFixture.Memories
            ),
            expected: replacement.Components.ToArray()
        );
    }
    [Fact]
    public void AColumnRefusesAWriteOfTheWrongComponentCount() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);

        Assert.True(condition: VectorColumn.TryOpen(
            arena: arena,
            column: out var column,
            refusal: out _,
            rowOrdinal: VectorArenaFixture.Memories
        ));
        Assert.False(condition: column.TryWrite(
            components: VectorArenaFixture.Unit(
                axis: 0,
                dimensions: 16
            ).Components,
            key: VectorArenaFixture.Key(
                catalog: catalog,
                value: "north"
            ),
            reason: out var reason
        ));
        Assert.False(condition: string.IsNullOrEmpty(value: reason));
    }
    [Fact]
    public void ADefaultColumnViewsNothing() {
        var column = default(VectorColumn);

        Assert.False(condition: column.IsOpen);
        Assert.Equal(
            actual: column.Count,
            expected: 0
        );
        Assert.False(condition: column.TryRead(
            components: out _,
            key: default
        ));
        Assert.Throws<InvalidOperationException>(testCode: () => column.Arena);
    }
    // The same closure law the library's own unions answer to, over the assembly this project ships: a case
    // declared anywhere else fails here.
    [Fact]
    public void EveryUnionThisProjectDeclaresIsClosed() {
        var library = typeof(VectorSource).Assembly;
        var unions = library
            .GetTypes()
            .Where(predicate: static type => (type.GetCustomAttribute<UnionAttribute>(inherit: false) is not null))
            .ToArray();

        Assert.NotEmpty(collection: unions);

        foreach (var union in unions) {
            Assert.True(
                condition: (union.IsValueType
                    ? typeof(IUnion).IsAssignableFrom(c: union)
                    : union.IsAbstract
                ),
                userMessage: $"{union.FullName} is neither an abstract case hierarchy nor a value carrier."
            );

            if (union.IsValueType) {
                Assert.True(
                    condition: union.GetCustomAttributesData().Any(predicate: static data => string.Equals(
                    a: data.AttributeType.FullName,
                    b: "System.Runtime.CompilerServices.IsReadOnlyAttribute",
                    comparisonType: StringComparison.Ordinal
                )),
                    userMessage: $"{union.FullName} is a [Union] struct that is not readonly."
                );
            }
        }

        Assert.Null(@object: ((IUnion)default(VectorSource)).Value);
        Assert.NotNull(@object: ((IUnion)VectorSource.Literal(components: VectorArenaFixture.Unit(axis: 0).Memory)).Value);
        Assert.NotNull(@object: ((IUnion)VectorSource.Cell(
            key: default,
            rowOrdinal: 0
        )).Value);
    }
    [Fact]
    public void AVectorSourceAnswersOnlyTheCaseItHolds() {
        var cell = VectorSource.Cell(
            key: default,
            rowOrdinal: 3
        );
        var literal = VectorSource.Literal(components: VectorArenaFixture.Unit(axis: 1).Memory);

        Assert.True(condition: cell.IsCell);
        Assert.False(condition: cell.IsLiteral);
        Assert.Equal(
            actual: cell.Address.RowOrdinal,
            expected: 3
        );
        Assert.Throws<InvalidOperationException>(testCode: () => cell.Components);
        Assert.True(condition: literal.IsLiteral);
        Assert.Throws<InvalidOperationException>(testCode: () => literal.Address);
        Assert.False(condition: default(VectorSource).HasValue);
        Assert.Throws<InvalidOperationException>(testCode: () => default(VectorSource).Address);
    }
}
