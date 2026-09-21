using Puck.Maths;
using Xunit;

namespace Puck.State.Vectors.Tests;

/// <summary>The one section the arena vector law suites store: a keyed vector table and the boolean row that
/// filters it, a vector slot, the three destinations a ranking lands in, an evicting vector table, a table in a
/// wider space, a non-vector slot, and a keyed table whose envelope no score fits.</summary>
public static class VectorArenaFixture {
    /// <summary>The catalog ordinal of the boolean filter row.</summary>
    public const int Active = 5;
    /// <summary>The catalog ordinal of the envelope-bounded keyed table.</summary>
    public const int Capped = 9;
    /// <summary>The catalog ordinal of the non-vector slot.</summary>
    public const int Count = 8;
    /// <summary>The catalog ordinal of the evicting vector table.</summary>
    public const int Journal = 6;
    /// <summary>The catalog ordinal of the keyed vector table.</summary>
    public const int Memories = 0;
    /// <summary>The catalog ordinal of the keyed Fixed ranking destination.</summary>
    public const int Ranked = 3;
    /// <summary>The catalog ordinal of the keyed Int ranking destination.</summary>
    public const int Recalled = 2;
    /// <summary>The catalog ordinal of the Text ranking destination.</summary>
    public const int Reply = 4;
    /// <summary>The catalog ordinal of the vector slot.</summary>
    public const int Stance = 1;
    /// <summary>The catalog ordinal of the keyed vector table in the wider space.</summary>
    public const int Wide = 7;

    /// <summary>Builds an arena over the fixture's section.</summary>
    /// <param name="catalog">The catalog the arena stores.</param>
    /// <returns>The arena.</returns>
    public static StateArena Arena(out StateCatalog catalog) {
        var section = Section();

        catalog = StateCatalog.Compile(section: section);

        return new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
    }
    /// <summary>Builds an admissible unit vector blending two axes in the given integer proportion.</summary>
    /// <param name="first">The axis carrying <paramref name="firstWeight"/>.</param>
    /// <param name="second">The axis carrying <paramref name="secondWeight"/>.</param>
    /// <param name="firstWeight">The first axis's weight.</param>
    /// <param name="secondWeight">The second axis's weight.</param>
    /// <param name="dimensions">How many components the vector carries.</param>
    /// <returns>The vector.</returns>
    public static StateVector Blend(int first, int second, long firstWeight, long secondWeight, int dimensions = 8) {
        var components = new sbyte[dimensions];
        var sum = new long[dimensions];

        sum[first] = firstWeight;
        sum[second] = secondWeight;

        Assert.True(condition: SignedByteVectorFunctions.TryNormalize(
            components: sum,
            destination: components
        ));

        return Vector(components: components);
    }
    /// <summary>Resolves one interned cell key.</summary>
    /// <param name="catalog">The catalog holding the intern table.</param>
    /// <param name="value">The key's name.</param>
    /// <returns>The key.</returns>
    public static CellKey Key(StateCatalog catalog, string value) {
        Assert.True(condition: catalog.Keys.TryResolve(
            key: out var key,
            name: Name(value: value)
        ));

        return key;
    }
    /// <summary>Resolves an arena-owned key, including a name minted after catalog compilation.</summary>
    /// <param name="arena">The arena holding the key ledger.</param>
    /// <param name="value">The key's name.</param>
    /// <returns>The key.</returns>
    public static CellKey Key(StateArena arena, string value) {
        Assert.True(condition: arena.Keys.TryResolve(name: Name(value: value), key: out var key));
        return key;
    }
    /// <summary>Returns a validated cell name.</summary>
    /// <param name="value">The name's text.</param>
    /// <returns>The name.</returns>
    public static CellName Name(string value) => CellName.Parse(candidate: value);
    /// <summary>Reads one vector cell's components as an owned copy.</summary>
    /// <param name="arena">The arena holding the row.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key.</param>
    /// <returns>The components, or an empty array when the cell holds none.</returns>
    public static sbyte[] Read(StateArena arena, int rowOrdinal, CellKey key) => (arena.TryReadVector(
        components: out var components,
        key: key,
        rowOrdinal: rowOrdinal
    )
        ? components.ToArray()
        : []
    );
    /// <summary>Resolves the reserved key every slot row's one cell carries.</summary>
    /// <param name="catalog">The catalog holding the intern table.</param>
    /// <returns>The key.</returns>
    public static CellKey SlotKey(StateCatalog catalog) {
        Assert.True(condition: catalog.Keys.TryResolve(
            key: out var key,
            name: StateRow.SlotKey
        ));

        return key;
    }
    /// <summary>Builds the fixture's section.</summary>
    /// <returns>The section.</returns>
    public static StateSection Section() => new(
        Rows: [
            new StateRow(
                Name: Name(value: "memories"),
                Kind: CellKind.Vector,
                Space: "space8",
                Capacity: 4,
                Cells: [
                    new StateCell(
                        Key: Name(value: "north"),
                        Value: CellValue.Vector(components: Unit(axis: 0).Memory)
                    ),
                    new StateCell(
                        Key: Name(value: "east"),
                        Value: CellValue.Vector(components: Unit(axis: 1).Memory)
                    ),
                    new StateCell(
                        Key: Name(value: "up"),
                        Value: CellValue.Vector(components: Blend(
                            first: 0,
                            firstWeight: 3L,
                            second: 2,
                            secondWeight: 1L
                        ).Memory)
                    ),
                ]
            ),
            new StateRow(
                Name: Name(value: "stance"),
                Kind: CellKind.Vector,
                Space: "space8",
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Vector(components: Unit(axis: 7).Memory)
                    )]
            ),
            new StateRow(
                Name: Name(value: "recalled"),
                Kind: CellKind.Int,
                Capacity: 4
            ),
            new StateRow(
                Name: Name(value: "ranked"),
                Kind: CellKind.Fixed,
                Capacity: 4
            ),
            new StateRow(
                Name: Name(value: "reply"),
                Kind: CellKind.Text,
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Text(value: "")
                    )]
            ),
            new StateRow(
                Name: Name(value: "active"),
                Kind: CellKind.Bool,
                Capacity: 4,
                Cells: [
                    new StateCell(
                        Key: Name(value: "north"),
                        Value: CellValue.Bool(value: true)
                    ),
                    new StateCell(
                        Key: Name(value: "east"),
                        Value: CellValue.Bool(value: false)
                    ),
                    new StateCell(
                        Key: Name(value: "up"),
                        Value: CellValue.Bool(value: true)
                    ),
                ]
            ),
            new StateRow(
                Name: Name(value: "journal"),
                Kind: CellKind.Vector,
                Space: "space8",
                Capacity: 2,
                Evicts: true,
                Cells: [new StateCell(
                        Key: Name(value: "first"),
                        Value: CellValue.Vector(components: Unit(axis: 0).Memory)
                    )]
            ),
            new StateRow(
                Name: Name(value: "wide"),
                Kind: CellKind.Vector,
                Space: "space16",
                Capacity: 2,
                Cells: [new StateCell(
                        Key: Name(value: "far"),
                        Value: CellValue.Vector(components: Unit(
                            axis: 0,
                            dimensions: 16
                        ).Memory)
                    )]
            ),
            new StateRow(
                Name: Name(value: "count"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Int(value: 0L)
                    )]
            ),
            new StateRow(
                Name: Name(value: "capped"),
                Kind: CellKind.Int,
                Capacity: 4,
                Max: 10L,
                Min: 0L
            ),
        ],
        Spaces: [
            new StateSpace(
                Name: Name(value: "space8"),
                Model: "model",
                Revision: "r1",
                Dimensions: 8
            ),
            new StateSpace(
                Name: Name(value: "space16"),
                Model: "model",
                Revision: "r1",
                Dimensions: 16
            ),
        ]
    );
    /// <summary>Builds an admissible unit vector pointing along one axis.</summary>
    /// <param name="axis">Which component carries the whole length.</param>
    /// <param name="dimensions">How many components the vector carries.</param>
    /// <returns>The vector.</returns>
    public static StateVector Unit(int axis, int dimensions = 8) {
        var components = new sbyte[dimensions];

        components[axis] = 127;

        return Vector(components: components);
    }
    /// <summary>Admits components through the same door a document boundary uses.</summary>
    /// <param name="components">The components.</param>
    /// <returns>The vector.</returns>
    public static StateVector Vector(ReadOnlySpan<sbyte> components) {
        Assert.True(
            condition: StateVector.TryCreate(
                components: components,
                error: out var error,
                vector: out var vector
            ),
            userMessage: error
        );

        return vector!;
    }
}
