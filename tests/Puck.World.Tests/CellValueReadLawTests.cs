using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: one (row, key) read answers one <see cref="CellValue"/> carrying the case the row's kind declares —
/// including the vector case, which the scalar raw/text pair answers absent for — and the carrier holds no case
/// exactly when the row declares no cell under that key. A reader switching on the carrier's own kind therefore
/// cannot reach a payload the row does not carry.
/// </summary>
public sealed class CellValueReadLawTests {
    private sealed class ConsoleAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;

            return true;
        }
    }

    private const ulong Tick = 7UL;

    private static StateVector Sample(int dimensions = 8) {
        var components = new sbyte[dimensions];

        components[0] = 127;
        components[(dimensions - 1)] = -3;
        if (!StateVector.TryCreate(
            components: components,
            error: out var error,
            vector: out var vector
        )) {
            throw new InvalidOperationException(message: error);
        }

        return vector;
    }

    private static WorldDefinition Document() => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(
            Spaces: [new StateSpace(
                    Dimensions: 8,
                    Model: "test-model",
                    Name: CellName.Parse(candidate: "lore"),
                    Revision: "1"
                )],
            World: [
                new WorldStateRow(
                Name: CellName.Parse(candidate: "score"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 12L)
                    )]
            ),
                new WorldStateRow(
                Name: CellName.Parse(candidate: "flag"),
                Kind: CellKind.Bool,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Bool(value: true)
                    )]
            ),
                // A text cell carrying no string: the row declares the cell, so the read is present — the case the
                // sibling-nullable pair cannot tell from an absent cell.
                new WorldStateRow(
                Name: CellName.Parse(candidate: "caption"),
                Kind: CellKind.Text,
                Capacity: 2,
                Cells: [
                    new StateCell(
                        Key: CellName.Parse(candidate: "a"),
                        Value: CellValue.Text(value: "hello")
                    ),
                    new StateCell(Key: CellName.Parse(candidate: "b"), Value: CellValue.Text(value: "")),
                ]
            ),
                new WorldStateRow(
                Name: CellName.Parse(candidate: "memories"),
                Kind: CellKind.Vector,
                Capacity: 2,
                Space: "lore",
                Cells: [new StateCell(
                        Key: CellName.Parse(candidate: "m1"),
                        Value: CellValue.Vector(components: Sample().Memory)
                    )]
            ),
                // A vector row that carries a slot cell: the one shape a key-resolution fallback could answer with a
                // cell the caller never addressed.
                new WorldStateRow(
                Name: CellName.Parse(candidate: "anchor"),
                Kind: CellKind.Vector,
                Space: "lore",
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Vector(components: Sample().Memory)
                    )]
            ),
            ]
        ),
    };

    private static CellValue Read(WorldDefinition definition, string row, string? key = null) {
        Assert.True(condition: WorldStateReader.TryReadValue(
            definition: definition,
            key: key,
            row: out _,
            rowName: row,
            value: out var value,
            tick: Tick,
            engineTick: Tick
        ));

        return value;
    }

    [Fact]
    public void EachRowKindAnswersItsOwnCaseAndNothingElse() {
        var definition = Document();

        Assert.Equal(
            actual: Read(
                definition: definition,
                row: "score"
            ).AsInt,
            expected: 12L
        );
        Assert.True(condition: Read(
            definition: definition,
            row: "flag"
        ).AsBool);
        Assert.Equal(
            actual: Read(
                definition: definition,
                key: "a",
                row: "caption"
            ).AsText,
            expected: "hello"
        );
        // Reaching for a case the carrier does not hold is a refusal, never a neutral zero.
        _ = Assert.Throws<InvalidOperationException>(testCode: () => Read(
            definition: definition,
            row: "score"
        ).AsText);
    }

    [Fact]
    public void AVectorCellAnswersItsComponentsWhereTheScalarPairAnswersAbsent() {
        var definition = Document();
        var value = Read(
            definition: definition,
            key: "m1",
            row: "memories"
        );

        Assert.Equal(
            actual: value.Kind,
            expected: CellKind.Vector
        );
        Assert.Equal(
            actual: value.AsVector.Length,
            expected: 8
        );
        Assert.Equal(
            actual: value.AsVector.Span[0],
            expected: ((sbyte)127)
        );

        // The pair this carrier replaces reads the same cell as nothing at all.
        _ = WorldStateReader.TryRead(
            definition: definition,
            key: "m1",
            rawValue: out var raw,
            row: out _,
            rowName: "memories",
            text: out var text,
            tick: Tick,
            engineTick: Tick
        );

        Assert.Null(@object: raw);
        Assert.Null(@object: text);
    }

    [Fact]
    public void AnAbsentCellHoldsNoCaseAndADeclaredEmptyTextCellDoesNot() {
        var definition = Document();

        Assert.False(condition: Read(
            definition: definition,
            key: "missing",
            row: "caption"
        ).HasValue);
        Assert.False(condition: Read(
            definition: definition,
            key: "m2",
            row: "memories"
        ).HasValue);

        var declared = Read(
            definition: definition,
            key: "b",
            row: "caption"
        );

        Assert.True(condition: declared.HasValue);
        // The carrier spells a text cell with no string as the empty string; absence is HasValue alone.
        Assert.Equal(
            actual: declared.AsText,
            expected: string.Empty
        );
    }

    [Fact]
    public void AKeyNoNameParsesFromIsAbsentOnEveryRowKindIncludingAVectorSlotRow() {
        var definition = Document();

        Assert.False(condition: Read(
            definition: definition,
            key: "a*b",
            row: "score"
        ).HasValue);
        Assert.False(condition: Read(
            definition: definition,
            key: "a*b",
            row: "anchor"
        ).HasValue);
        Assert.False(condition: Read(
            definition: definition,
            key: "a*b",
            row: "memories"
        ).HasValue);

        using var row = HostRow.Build(
            definition: definition,
            name: "boot"
        );
        var registry = new CommandRegistry(modules: [new WorldStateCommandModule(
            authority: new ConsoleAuthority(instance: row.Instance),
            echoes: new WorldDeferredVerbEchoes(),
            link: row.Instance.Link
        )]);
        var result = registry.Submit(line: "world.state anchor a*b");

        Assert.Contains(
            actualString: result.Output,
            expectedSubstring: "no such cell"
        );
    }
}
