using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Embeddings;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>
/// Tests covering Defect 11 resolution: lowering of SQL embed(...) literals to vector literals via EmbeddingLock,
/// ensuring byte-identical lowering against native vector(...) literals and strict diagnostic reporting.
/// </summary>
public sealed class StateSqlEmbeddingLoweringTests {
    private const string SampleVectorBase64 = "fwAAAAAA"; // 8-byte vector

    private static (JsonObject? Json, DiagnosticBag Diagnostics) LowerWithLock(string body, EmbeddingLock? lockFile = null) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            embeddings: lockFile,
            source: body
        );

        return (compilation.Json, compilation.Diagnostics);
    }

    private static EmbeddingLock CreateSampleLock() {
        var lockFile = new EmbeddingLock();
        var space = new EmbeddingLockSpace(
            dimensions: 8,
            model: "puck-fixture-v1",
            revision: "1"
        );
        var hash = EmbeddingLock.ComputeTextHash(text: "hello world");
        space.Entries[hash] = new EmbeddingLockEntry(Text: "hello world", Vector: SampleVectorBase64);
        lockFile.Spaces["lore"] = space;
        return lockFile;
    }

    [Fact]
    public void SqlEmbedLiteralLowersIdenticalToVectorLiteralInInsert() {
        var lockFile = CreateSampleLock();

        var sqlBodyWithEmbed = """
            schema: "puck.world.definition.v1"

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
                INSERT INTO lore_table (id, v) VALUES ('k1', embed('hello world'));
            }
            """;

        var sqlBodyWithVector = $$"""
            schema: "puck.world.definition.v1"

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
                INSERT INTO lore_table (id, v) VALUES ('k1', vector('{{SampleVectorBase64}}'));
            }
            """;

        var (embedJson, embedDiag) = LowerWithLock(body: sqlBodyWithEmbed, lockFile: lockFile);
        var (vectorJson, vectorDiag) = LowerWithLock(body: sqlBodyWithVector, lockFile: lockFile);

        Assert.False(condition: embedDiag.HasErrors, userMessage: embedDiag.FormatReport("Embed errors"));
        Assert.False(condition: vectorDiag.HasErrors, userMessage: vectorDiag.FormatReport("Vector errors"));

        Assert.NotNull(@object: embedJson);
        Assert.NotNull(@object: vectorJson);

        var mismatch = JsonMismatch.Find(
            actual: embedJson,
            expected: vectorJson,
            path: "$"
        );

        Assert.Null(mismatch);
    }

    [Fact]
    public void SqlEmbedLiteralInUpdateLowersIdenticalToVectorLiteral() {
        var lockFile = CreateSampleLock();

        var sqlBodyWithEmbed = """
            schema: "puck.world.definition.v1"

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
                INSERT INTO lore_table (id, v) VALUES ('k1', vector('fwAAAAAA'));
                UPDATE lore_table SET v = embed('hello world') WHERE id = 'k1';
            }
            """;

        var sqlBodyWithVector = $$"""
            schema: "puck.world.definition.v1"

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
                INSERT INTO lore_table (id, v) VALUES ('k1', vector('fwAAAAAA'));
                UPDATE lore_table SET v = vector('{{SampleVectorBase64}}') WHERE id = 'k1';
            }
            """;

        var (embedJson, embedDiag) = LowerWithLock(body: sqlBodyWithEmbed, lockFile: lockFile);
        var (vectorJson, vectorDiag) = LowerWithLock(body: sqlBodyWithVector, lockFile: lockFile);

        Assert.False(condition: embedDiag.HasErrors, userMessage: embedDiag.FormatReport("Embed errors"));
        Assert.False(condition: vectorDiag.HasErrors, userMessage: vectorDiag.FormatReport("Vector errors"));

        Assert.NotNull(@object: embedJson);
        Assert.NotNull(@object: vectorJson);

        var mismatch = JsonMismatch.Find(
            actual: embedJson,
            expected: vectorJson,
            path: "$"
        );

        Assert.Null(mismatch);
    }

    [Fact]
    public void SqlEmbedMissingFromLockReportsPuck079() {
        var lockFile = CreateSampleLock();

        var body = """
            schema: "puck.world.definition.v1"

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
                INSERT INTO lore_table (id, v) VALUES ('k1', embed('unlocked query text'));
            }
            """;

        var (_, diag) = LowerWithLock(body: body, lockFile: lockFile);

        Assert.True(condition: diag.HasErrors);
        Assert.Contains(diag, d => d.Code == PuckDiagnosticCodes.EmbeddingLockMissing);
    }

    [Fact]
    public void SqlEmbedStaleSpaceInLockReportsPuck085() {
        var lockFile = new EmbeddingLock();
        var staleSpace = new EmbeddingLockSpace(
            dimensions: 8,
            model: "old-model-revision",
            revision: "1"
        );
        var hash = EmbeddingLock.ComputeTextHash(text: "hello world");
        staleSpace.Entries[hash] = new EmbeddingLockEntry(Text: "hello world", Vector: SampleVectorBase64);
        lockFile.Spaces["lore"] = staleSpace;

        var body = """
            schema: "puck.world.definition.v1"

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
                INSERT INTO lore_table (id, v) VALUES ('k1', embed('hello world'));
            }
            """;

        var (_, diag) = LowerWithLock(body: body, lockFile: lockFile);

        Assert.True(condition: diag.HasErrors);
        Assert.Contains(diag, d => d.Code == PuckDiagnosticCodes.EmbeddingLockStale);
    }

    [Fact]
    public void SqlEmbedAmbiguousSpaceReportsPuck080() {
        var lockFile = new EmbeddingLock();
        lockFile.Spaces["space1"] = new EmbeddingLockSpace(dimensions: 8, model: "m1", revision: "1");
        lockFile.Spaces["space2"] = new EmbeddingLockSpace(dimensions: 8, model: "m2", revision: "1");

        var body = """
            schema: "puck.world.definition.v1"

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
                    v  VECTOR
                );
                INSERT INTO lore_table (id, v) VALUES ('k1', embed('hello world'));
            }
            """;

        var (_, diag) = LowerWithLock(body: body, lockFile: lockFile);

        Assert.True(condition: diag.HasErrors);
        Assert.Contains(diag, d => d.Code == PuckDiagnosticCodes.EmbeddingSpaceAmbiguous);
    }

    [Fact]
    public void SqlEmbedExplicitSpaceResolvesSuccessfully() {
        var lockFile = new EmbeddingLock();
        var space1 = new EmbeddingLockSpace(dimensions: 8, model: "m1", revision: "1");
        var space2 = new EmbeddingLockSpace(dimensions: 8, model: "m2", revision: "1");

        var hash = EmbeddingLock.ComputeTextHash(text: "hello world");
        space2.Entries[hash] = new EmbeddingLockEntry(Text: "hello world", Vector: SampleVectorBase64);

        lockFile.Spaces["space1"] = space1;
        lockFile.Spaces["space2"] = space2;

        var body = """
            schema: "puck.world.definition.v1"

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
                    v  VECTOR(space2)
                );
                INSERT INTO lore_table (id, v) VALUES ('k1', embed('hello world', space: space2));
            }
            """;

        var (json, diag) = LowerWithLock(body: body, lockFile: lockFile);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport("Errors"));
        Assert.NotNull(@object: json);
    }
}
