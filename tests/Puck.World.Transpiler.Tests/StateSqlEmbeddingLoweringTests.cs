using Puck.State;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Embeddings;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>SQL <c>embed(...)</c> literals lower through the embedding lock to exactly the vector literal the lock
/// records, and a text the lock cannot resolve is refused by name.</summary>
public sealed class StateSqlEmbeddingLoweringTests {
    // One `lore` space, a table of its vectors, and then `statements` inside the same sql block.
    private static string Lore(string statements) => $$"""
        state {
          spaces [
            {
              name: "lore"
              dimensions: 8
              model: "puck-fixture-v1"
              revision: "1"
            }
          ]
        }

        sql {
            CREATE TABLE lore_table (
                id TEXT PRIMARY KEY,
                v  VECTOR(lore)
            );
            {{statements}}
        }
        """;
    // Two spaces, the second carrying the embedded text, and then `statements` inside the same sql block.
    private static string TwoSpaces(string column, string statements) => $$"""
        state {
          spaces [
            {
              name: "space1"
              dimensions: 8
              model: "m1"
              revision: "1"
            }
            {
              name: "space2"
              dimensions: 8
              model: "m2"
              revision: "1"
            }
          ]
        }

        sql {
            CREATE TABLE lore_table (
                id TEXT PRIMARY KEY,
                v  {{column}}
            );
            {{statements}}
        }
        """;
    private static EmbeddingLock TwoSpaceLock() {
        var lockFile = new EmbeddingLock();
        var space2 = new EmbeddingLockSpace(identity: new EmbeddingIdentity(Dimensions: 8, Model: "m2", Revision: "1"));

        space2.Entries[EmbeddingText.Hash(text: "hello world").Hex] = new EmbeddingLockEntry(Text: "hello world", Vector: WorldSources.SampleVector);
        lockFile.Spaces["space1"] = new EmbeddingLockSpace(identity: new EmbeddingIdentity(Dimensions: 8, Model: "m1", Revision: "1"));
        lockFile.Spaces["space2"] = space2;

        return lockFile;
    }

    // A refusal about an embedded text inside SQL is drawn on the `embed` itself.
    private static readonly Dictionary<string, Refusal> Refusals = new(comparer: StringComparer.Ordinal) {
        ["PUCK079: a text the lock does not carry"] = new(
            Body: Lore(statements: "INSERT INTO lore_table (id, v) VALUES ('k1', embed('unlocked query text'));"),
            Code: PuckDiagnosticCodes.EmbeddingLockMissing,
            Needle: "embed('unlocked query text')"
        ) { Alone = true, Embeddings = WorldSources.LoreLock("puck-fixture-v1", "hello world") },
        ["PUCK080: a lock recorded under another model"] = new(
            Body: Lore(statements: "INSERT INTO lore_table (id, v) VALUES ('k1', embed('hello world'));"),
            Code: PuckDiagnosticCodes.EmbeddingLockStale,
            Needle: "embed('hello world')"
        ) { Alone = true, Embeddings = WorldSources.LoreLock("old-model-revision", "hello world") },
        ["PUCK085: an embed with two spaces to choose from and none named"] = new(
            Body: TwoSpaces(column: "VECTOR", statements: "INSERT INTO lore_table (id, v) VALUES ('k1', embed('hello world'));"),
            Code: PuckDiagnosticCodes.EmbeddingSpaceAmbiguous,
            Needle: "embed('hello world')"
        ) { Alone = true, Embeddings = TwoSpaceLock() },
        // Six components written into an eight-dimension space.
        ["PUCK081: an inserted vector shorter than its space"] = new(
            Body: Lore(statements: "INSERT INTO lore_table (id, v) VALUES ('k1', vector('fwAAAAAA'));"),
            Code: PuckDiagnosticCodes.VectorLiteralInvalid,
            Needle: "vector('fwAAAAAA')"
        ) { Alone = true, Mentions = "dimensions 8" },
        ["PUCK081: an updated vector shorter than its space"] = new(
            Body: Lore(statements: "CREATE RULE r EVERY TICK AS\n        UPDATE lore_table SET v = vector('fwAAAAAA') WHERE id = 'k1';"),
            Code: PuckDiagnosticCodes.VectorLiteralInvalid,
            Needle: "vector('fwAAAAAA')"
        ) { Alone = true, Mentions = "dimensions 8" },
    };

    public static TheoryData<string> RefusalNames() => new(values: Refusals.Keys);
    // The first statement of each pair writes `embed`, the second the vector the lock records for its text.
    [InlineData("INSERT INTO lore_table (id, v) VALUES ('k1', embed('hello world'));", (("INSERT INTO lore_table (id, v) VALUES ('k1', vector('" + WorldSources.SampleVector) + "'));"))]
    [InlineData((("INSERT INTO lore_table (id, v) VALUES ('k1', vector('" + WorldSources.SampleVector) + "'));\n    UPDATE lore_table SET v = embed('hello world') WHERE id = 'k1';"), (((("INSERT INTO lore_table (id, v) VALUES ('k1', vector('" + WorldSources.SampleVector) + "'));\n    UPDATE lore_table SET v = vector('") + WorldSources.SampleVector) + "') WHERE id = 'k1';"))]
    [Theory]
    public void ASqlEmbedLowersIdenticallyToTheVectorItsLockRecords(string embedded, string written) {
        var lockFile = WorldSources.LoreLock("puck-fixture-v1", "hello world");

        var (embedJson, embedDiagnostics) = WorldSources.Lower(body: Lore(statements: embedded), embeddings: lockFile);
        var (vectorJson, vectorDiagnostics) = WorldSources.Lower(body: Lore(statements: written), embeddings: lockFile);

        Assert.False(condition: embedDiagnostics.HasErrors, userMessage: embedDiagnostics.FormatReport("Embed errors"));
        Assert.False(condition: vectorDiagnostics.HasErrors, userMessage: vectorDiagnostics.FormatReport("Vector errors"));
        Assert.Null(@object: JsonMismatch.Find(
            actual: embedJson,
            expected: vectorJson,
            path: "$"
        ));
    }
    [MemberData(nameof(RefusalNames))]
    [Theory]
    public void ARefusedSqlEmbedNamesItsCodeAndLine(string name) => WorldSources.AssertRefused(
        label: name,
        refusal: Refusals[name]
    );
    [Fact]
    public void ASqlEmbedNamingItsSpaceResolvesAmongSeveral() {
        var (_, diagnostics) = WorldSources.Lower(
            body: TwoSpaces(column: "VECTOR(space2)", statements: "INSERT INTO lore_table (id, v) VALUES ('k1', embed('hello world', space: space2));"),
            embeddings: TwoSpaceLock()
        );

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport("Errors"));
    }
}
