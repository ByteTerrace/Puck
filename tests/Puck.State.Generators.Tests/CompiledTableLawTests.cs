using Xunit;

namespace Puck.State.Generators.Tests;

/// <summary>Proves a compiled table answers a lookup it cannot serve as not found rather than faulting.</summary>
public sealed class CompiledTableLawTests {
    // A column name a table does not declare resolves to an index outside its columns; a lookup through it finds
    // nothing, the same answer an absent key gives.
    [Fact]
    public void AColumnTheTableDoesNotHaveIsNotFound() {
        Assert.True(
            condition: CompiledTable.TryCompile(
                document: new TableDocument(
                    Entries: [new TableEntryDocument(
                        Key: 1L,
                        Value: 5m
                    )],
                    Kind: TableDocument.IntKind,
                    Schema: TableDocument.CurrentSchema
                ),
                error: out var error,
                name: "prices",
                table: out var table
            ),
            userMessage: error
        );

        Assert.True(condition: table!.TryLookup(
            column: 0,
            key: 1L,
            raw: out var found
        ));
        Assert.Equal(
            actual: found,
            expected: 5L
        );
        Assert.False(condition: table.TryLookup(
            column: -1,
            key: 1L,
            raw: out _
        ));
        Assert.False(condition: table.TryLookup(
            column: 1,
            key: 1L,
            raw: out _
        ));
    }
}
