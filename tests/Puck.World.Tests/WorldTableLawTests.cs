using System.Text.Json;

using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A static table is a referenced, hash-pinned document read through <c>$table:</c>: a literal key is
/// proven present at compile, a dynamic key reads at evaluation and a missing one never holds a gate, a stale hash or
/// a duplicate key refuses at validation, and the table is not simulation state.</summary>
public sealed class WorldTableLawTests {
    // The directory every document here is read from, so each row's relative source resolves beside it.
    private static readonly string TableDirectory = Directory.CreateTempSubdirectory(prefix: "puck-world-table-law-").FullName;

    private static (string Source, string Hash) Write(TableDocument document) {
        var source = $"tables-{Guid.NewGuid():N}.table.json";

        File.WriteAllBytes(
            Path.Combine(
                path1: TableDirectory,
                path2: source
            ),
            JsonSerializer.SerializeToUtf8Bytes(
                document,
                DocumentJsonOptions.Shared
            )
        );
        return (source, ((TableCanonicalizer.Validate(document: document).Count == 0)
            ? TableCanonicalizer.Canonicalize(document).Hash
            : string.Empty));
    }
    private static (string Source, string Hash) WriteColumns(string kind, string[] columns, params (long Key, decimal[] Values)[] entries) =>
        Write(document: new TableDocument(
            TableDocument.CurrentSchema,
            kind,
            [.. entries.Select(selector: e => new TableEntryDocument(
                    e.Key,
                    Values: e.Values
                ))],
            Columns: columns
        ));
    private static (string Source, string Hash) WriteTable(string kind, params (long Key, decimal Value)[] entries) =>
        Write(document: new TableDocument(
            TableDocument.CurrentSchema,
            kind,
            [.. entries.Select(selector: e => new TableEntryDocument(
                    e.Key,
                    Value: e.Value
                ))]
        ));

    // A 2D chart flattened to a composite key computed by a binding: effectiveness[attack * 100 + defend].
    [Fact]
    public void AColumnTableIsReadByColumnAndABoundKeyComposesTheLookup() {
        var (source, hash) = WriteColumns(
            TableDocument.IntKind,
            ["power", "priority"],
            (1, [60m, 0m]),
            (2, [90m, 1m])
        );
        var (chart, chartHash) = WriteTable(
            TableDocument.IntKind,
            (102, 2m),
            (201, 0m)
        );
        var document = Fixtures.BuildDocument() with {
            DocumentDirectory = TableDirectory,
            Tables = [new TableRow(
                Hash: hash,
                Name: "moves",
                Source: source
            ), new TableRow(
                Hash: chartHash,
                Name: "chart",
                Source: chart
            )],
            StateRaw = new WorldStateSection(World: [StateFixtures.IntSlot(
                name: "move",
                value: 2L
            ), StateFixtures.IntSlot(
                name: "attack",
                value: 1L
            ), StateFixtures.IntSlot(
                name: "defend",
                value: 2L
            ), StateFixtures.IntSlot(
                name: "power",
                value: 0L
            ), StateFixtures.IntSlot(
                name: "priority",
                value: 0L
            ), StateFixtures.IntSlot(
                name: "multiplier",
                value: 0L
            )]),
            Rules = [
                new WorldRule(
                CellName.Parse(candidate: "stats"),
                [
                    new ActionEffect.SetState(
                        State: "power",
                        FromState: "$table:moves:power:$cell:move:$value"
                    ),
                    new ActionEffect.SetState(
                        State: "priority",
                        FromState: "$table:moves:priority:$cell:move:$value"
                    ),
                ]
            ),
                new WorldRule(
                CellName.Parse(candidate: "effect"),
                [new ActionEffect.SetState(
                        State: "multiplier",
                        FromState: "$table:chart:$local:pair"
                    )],
                Locals: [new RuleLocal(
                        Expression: new ExpressionProgram(Instructions: [
                        Instruction.Operand(name: "attack"), Instruction.Constant(value: 100m), Instruction.Of(operation: ExpressionOp.Multiply), Instruction.Operand(name: "defend"), Instruction.Of(operation: ExpressionOp.Add),
                    ]),
                        Kind: CellKind.Int,
                        Name: CellName.Parse(candidate: "pair")
                    )]
            ),
            ],
        };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: document,
                reason: out var reason
            ),
            userMessage: reason
        );
        using var fixture = Fixtures.FreshServer(definition: document);

        fixture.Step();
        Assert.Equal(
            90L,
            fixture.SlotValue(row: "power"
            )
        );
        Assert.Equal(
            1L,
            fixture.SlotValue(row: "priority"
            )
        );
        Assert.Equal(
            2L,
            fixture.SlotValue(row: "multiplier"
            )
        );

        var unnamed = document with {
            Rules = [new WorldRule(
                CellName.Parse(candidate: "stats"),
                [new ActionEffect.SetState(
                        State: "power",
                        FromState: "$table:moves:2"
                    )]
            )],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: unnamed,
            reason: out var columnReason
        ));
        Assert.Contains(
            actualString: columnReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "<column>"
        );
    }
    [Fact]
    public void ALiteralAndADynamicKeyReadTheTableAndAMissingKeyNeverHolds() {
        var (source, hash) = WriteTable(
            TableDocument.IntKind,
            (1, 60m),
            (2, 90m),
            (250, 5m)
        );
        var document = Fixtures.BuildDocument() with {
            DocumentDirectory = TableDirectory,
            Tables = [new TableRow(
                Hash: hash,
                Name: "power",
                Source: source
            )],
            StateRaw = new WorldStateSection(World: [StateFixtures.IntSlot(
                name: "move",
                value: 2L
            ), StateFixtures.IntSlot(
                name: "literal",
                value: 0L
            ), StateFixtures.IntSlot(
                name: "dynamic",
                value: 0L
            ), StateFixtures.IntSlot(
                name: "missing",
                value: 0L
            )]),
            Rules = [
                new WorldRule(
                CellName.Parse(candidate: "lit"),
                [new ActionEffect.SetState(
                        State: "literal",
                        FromState: "$table:power:250"
                    )]
            ),
                new WorldRule(
                CellName.Parse(candidate: "dyn"),
                [new ActionEffect.SetState(
                        State: "dynamic",
                        FromState: "$table:power:$cell:move:$value"
                    )]
            ),
                new WorldRule(
                CellName.Parse(candidate: "gap"),
                [new ActionEffect.SetState(
                        State: "missing",
                        Value: 1m
                    )],
                Gate: new ActionPredicate.CompareState(
                    State: "$table:power:$cell:missing:$value",
                    Comparison: ExpressionOp.GreaterOrEqual,
                    Value: 0m
                )
            ),
            ],
        };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: document,
                reason: out var reason
            ),
            userMessage: reason
        );
        using var fixture = Fixtures.FreshServer(definition: document);

        fixture.Step();
        Assert.Equal(
            5L,
            fixture.SlotValue(row: "literal"
            )
        );
        Assert.Equal(
            90L,
            fixture.SlotValue(row: "dynamic"
            )
        );
        Assert.Equal(
            0L,
            fixture.SlotValue(row: "missing"
            )
        );
        Assert.Contains(
            "power kind=Int entries=3",
            fixture.Server.DescribeTables(),
            StringComparison.Ordinal
        );

        var control = document with {
            StateRaw = document.StateRaw! with {
                World = [StateFixtures.IntSlot(
                name: "move",
                value: 2L
            ), StateFixtures.IntSlot(
                name: "literal",
                value: 0L
            ), StateFixtures.IntSlot(
                name: "dynamic",
                value: 0L
            ), StateFixtures.IntSlot(
                name: "missing",
                value: 1L
            )],
            },
        };
        using var held = Fixtures.FreshServer(definition: control);

        held.Step();
        Assert.Equal(
            1L,
            held.SlotValue(row: "missing"
            )
        );
    }
    [Fact]
    public void AMissingLiteralKeyAStaleHashAndADuplicateKeyRefuse() {
        var (source, hash) = WriteTable(
            TableDocument.FixedKind,
            (7, 1.5m)
        );
        var baseline = Fixtures.BuildDocument() with {
            DocumentDirectory = TableDirectory,
            Tables = [new TableRow(
                Hash: hash,
                Name: "rates",
                Source: source
            )],
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                CellName.Parse(candidate: "rate"),
                CellKind.Fixed,
                Cells: [new StateCell(
                        WorldStateRow.SlotKey,
                        CellValue.Fixed(rawBits: 0L)
                    )]
            )]),
            Rules = [new WorldRule(
                CellName.Parse(candidate: "r"),
                [new ActionEffect.SetState(
                        State: "rate",
                        FromState: "$table:rates:7"
                    )]
            )],
        };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: baseline,
                reason: out var okReason
            ),
            userMessage: okReason
        );

        var missing = baseline with {
            Rules = [new WorldRule(
                CellName.Parse(candidate: "r"),
                [new ActionEffect.SetState(
                        State: "rate",
                        FromState: "$table:rates:8"
                    )]
            )],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: missing,
            reason: out var missingReason
        ));
        Assert.Contains(
            actualString: missingReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "does not carry"
        );

        var stale = baseline with {
            Tables = [new TableRow(
                "rates",
                source,
                new string(
                    c: '0',
                    count: 64
                )
            )],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: stale,
            reason: out var staleReason
        ));
        Assert.Contains(
            actualString: staleReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "hash"
        );

        var (duplicateSource, _) = WriteTable(
            TableDocument.IntKind,
            (1, 1m),
            (1, 2m)
        );
        var duplicate = baseline with {
            Tables = [new TableRow(
                Hash: hash,
                Name: "rates",
                Source: duplicateSource
            )],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: duplicate,
            reason: out var duplicateReason
        ));
        Assert.Contains(
            actualString: duplicateReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "declared twice"
        );
    }
}
