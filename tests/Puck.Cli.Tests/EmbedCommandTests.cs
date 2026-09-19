using Puck.World.Transpiler.Embeddings;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class EmbedCommandTests {
    private sealed class EmbedTestDirectory : IDisposable {
        public string Path { get; } = Directory.CreateTempSubdirectory(prefix: "puck embed tests ").FullName;

        public void Dispose() {
            try {
                Directory.Delete(
                    path: Path,
                    recursive: true
                );
            } catch {
                // Ignore cleanup errors on disposal
            }
        }
    }

    [Fact]
    public async Task EmbedGeneratesLockAndEnablesCleanCompilationAsync() {
        using var dir = new EmbedTestDirectory();
        var puckPath = Path.Combine(dir.Path, "world.puck");
        var lockPath = Path.Combine(dir.Path, "world.embeddings.json");

        var puckSource = """
            schema: "puck.world.definition.v1"

            state {
              spaces [
                {
                  name: "lore"
                  dimensions: 8
                  model: "puck-fixture"
                  revision: "1"
                }
              ]
            }

            sql {
                CREATE TABLE lore_table (
                    id TEXT PRIMARY KEY,
                    v  VECTOR(lore)
                );
                INSERT INTO lore_table (id, v) VALUES ('k1', embed('hello world')), ('k2', embed('farewell'));
            }
            """;

        await File.WriteAllTextAsync(puckPath, puckSource, cancellationToken: TestContext.Current.CancellationToken);

        // 1. Compile without lock should fail with exit code 1 due to missing lock (PUCK079)
        var originalErr = Console.Error;
        using var errSw = new StringWriter();
        Console.SetError(errSw);
        int compileBeforeEmbedCode;
        try {
            compileBeforeEmbedCode = await PuckRootCommand.InvokeAsync(args: ["compile", puckPath]);
        } finally {
            Console.SetError(originalErr);
        }
        Assert.Equal(expected: 1, actual: compileBeforeEmbedCode);
        Assert.Contains(expectedSubstring: "PUCK079", actualString: errSw.ToString(), comparisonType: StringComparison.Ordinal);

        // 2. Check before embed should report missing entries and exit 1
        var checkBeforeEmbedCode = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath, "--check"]);
        Assert.Equal(expected: 1, actual: checkBeforeEmbedCode);

        // 3. puck embed generates lock file deterministically
        var embedExitCode = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath]);
        Assert.Equal(expected: 0, actual: embedExitCode);
        Assert.True(File.Exists(lockPath));

        var lockFile = EmbeddingLock.TryLoad(puckPath);
        Assert.NotNull(lockFile);
        Assert.True(lockFile.Spaces.ContainsKey("lore"));
        var space = lockFile.Spaces["lore"];
        Assert.Equal(expected: 8, actual: space.Dimensions);
        Assert.Equal(expected: "puck-fixture", actual: space.Model);
        Assert.True(lockFile.TryGet(spaceName: "lore", text: "hello world", vectorBase64Url: out var v1));
        Assert.True(lockFile.TryGet(spaceName: "lore", text: "farewell", vectorBase64Url: out var v2));
        Assert.NotEmpty(v1);
        Assert.NotEmpty(v2);

        // 4. puck embed --check should now exit 0 without errors
        var checkAfterEmbedCode = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath, "--check"]);
        Assert.Equal(expected: 0, actual: checkAfterEmbedCode);

        // 5. puck compile should now succeed with exit code 0
        var compileAfterEmbedCode = await PuckRootCommand.InvokeAsync(args: ["compile", puckPath]);
        Assert.Equal(expected: 0, actual: compileAfterEmbedCode);

        // 6. Running embed again should be idempotent
        var lockJsonBefore = await File.ReadAllTextAsync(lockPath, cancellationToken: TestContext.Current.CancellationToken);
        var secondEmbedCode = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath]);
        Assert.Equal(expected: 0, actual: secondEmbedCode);
        var lockJsonAfter = await File.ReadAllTextAsync(lockPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected: lockJsonBefore, actual: lockJsonAfter);

        // 7. Generated fixture lock matches committed expected file byte-for-byte
        var expectedLockPath = Path.Combine(AppContext.BaseDirectory, "Assets", "expected_fixture.embeddings.json");
        Assert.True(File.Exists(expectedLockPath));
        var generatedBytes = await File.ReadAllBytesAsync(lockPath, cancellationToken: TestContext.Current.CancellationToken);
        var expectedBytes = await File.ReadAllBytesAsync(expectedLockPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expectedBytes, generatedBytes);
    }

    [Fact]
    public async Task EmbedPrunesUnusedEntriesAndSpacesAsync() {
        using var dir = new EmbedTestDirectory();
        var puckPath = Path.Combine(dir.Path, "prune_test.puck");
        var lockPath = Path.Combine(dir.Path, "prune_test.embeddings.json");

        var initialSource = """
            schema: "puck.world.definition.v1"

            state {
              spaces [
                {
                  name: "spaceA"
                  dimensions: 8
                  model: "puck-fixture"
                  revision: "1"
                }
                {
                  name: "spaceB"
                  dimensions: 8
                  model: "puck-fixture"
                  revision: "1"
                }
              ]
            }

            sql {
                CREATE TABLE tA (
                    id TEXT PRIMARY KEY,
                    v  VECTOR(spaceA)
                );
                CREATE TABLE tB (
                    id TEXT PRIMARY KEY,
                    v  VECTOR(spaceB)
                );
                INSERT INTO tA (id, v) VALUES ('k1', embed('keep_me', space: spaceA)), ('k2', embed('drop_me', space: spaceA));
                INSERT INTO tB (id, v) VALUES ('k1', embed('drop_space', space: spaceB));
            }
            """;

        await File.WriteAllTextAsync(puckPath, initialSource, cancellationToken: TestContext.Current.CancellationToken);
        var initialEmbedCode = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath]);
        Assert.Equal(expected: 0, actual: initialEmbedCode);

        var lockFile = EmbeddingLock.TryLoad(puckPath);
        Assert.NotNull(lockFile);
        Assert.True(lockFile.Spaces.ContainsKey("spaceA"));
        Assert.True(lockFile.Spaces.ContainsKey("spaceB"));
        Assert.True(lockFile.TryGet(spaceName: "spaceA", text: "keep_me", vectorBase64Url: out _));
        Assert.True(lockFile.TryGet(spaceName: "spaceA", text: "drop_me", vectorBase64Url: out _));
        Assert.True(lockFile.TryGet(spaceName: "spaceB", text: "drop_space", vectorBase64Url: out _));

        // Now update source: remove spaceB entirely, and remove 'drop_me' from spaceA
        var updatedSource = """
            schema: "puck.world.definition.v1"

            state {
              spaces [
                {
                  name: "spaceA"
                  dimensions: 8
                  model: "puck-fixture"
                  revision: "1"
                }
              ]
            }

            sql {
                CREATE TABLE tA (
                    id TEXT PRIMARY KEY,
                    v  VECTOR(spaceA)
                );
                INSERT INTO tA (id, v) VALUES ('k1', embed('keep_me', space: spaceA));
            }
            """;

        await File.WriteAllTextAsync(puckPath, updatedSource, cancellationToken: TestContext.Current.CancellationToken);

        // Check should fail because of unused entries/spaces
        var checkCode = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath, "--check"]);
        Assert.Equal(expected: 1, actual: checkCode);

        // Embed without --check should prune unused entries and unused spaceB
        var pruneEmbedCode = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath]);
        Assert.Equal(expected: 0, actual: pruneEmbedCode);

        var prunedLock = EmbeddingLock.TryLoad(puckPath);
        Assert.NotNull(prunedLock);
        Assert.True(prunedLock.Spaces.ContainsKey("spaceA"));
        Assert.False(prunedLock.Spaces.ContainsKey("spaceB"));
        Assert.True(prunedLock.TryGet(spaceName: "spaceA", text: "keep_me", vectorBase64Url: out _));
        Assert.False(prunedLock.TryGet(spaceName: "spaceA", text: "drop_me", vectorBase64Url: out _));

        // Check now succeeds
        var checkAfterPrune = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath, "--check"]);
        Assert.Equal(expected: 0, actual: checkAfterPrune);
    }

    [Fact]
    public async Task ProbeSubcommandRanksVectorsAsync() {
        using var dir = new EmbedTestDirectory();
        var puckPath = Path.Combine(dir.Path, "probe_test.puck");

        var source = """
            schema: "puck.world.definition.v1"

            state {
              spaces [
                {
                  name: "lore"
                  dimensions: 8
                  model: "puck-fixture"
                  revision: "1"
                }
              ]
            }

            sql {
                CREATE TABLE lore_table (
                    id TEXT PRIMARY KEY,
                    v  VECTOR(lore)
                );
                INSERT INTO lore_table (id, v) VALUES
                    ('k1', embed('fire breathing dragon')),
                    ('k2', embed('friendly neighborhood spider')),
                    ('k3', embed('deep blue sea ocean'));
            }
            """;

        await File.WriteAllTextAsync(puckPath, source, cancellationToken: TestContext.Current.CancellationToken);

        var embedExitCode = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath]);
        Assert.Equal(expected: 0, actual: embedExitCode);

        // Probe for an existing text
        var probeExistingCode = await PuckRootCommand.InvokeAsync(args: ["embed", "probe", puckPath, "fire breathing dragon"]);
        Assert.Equal(expected: 0, actual: probeExistingCode);

        // Probe for a new query text (fixture generator generates embedding on the fly)
        var probeNewCode = await PuckRootCommand.InvokeAsync(args: ["embed", "probe", puckPath, "scary reptile creature", "--top", "2"]);
        Assert.Equal(expected: 0, actual: probeNewCode);
    }

    [Fact]
    public async Task DecompileWithLockResolvesEmbedAndWithoutLockResolvesVectorAsync() {
        using var dir = new EmbedTestDirectory();
        var puckPath = Path.Combine(dir.Path, "world.puck");
        var worldJsonPath = Path.Combine(dir.Path, "world.world.json");
        var lockPath = Path.Combine(dir.Path, "world.embeddings.json");
        var outWithLockPath = Path.Combine(dir.Path, "world_decompiled.puck");

        var puckSource = """
            schema: "puck.world.definition.v1"

            state {
              spaces [
                {
                  name: "lore"
                  dimensions: 8
                  model: "puck-fixture"
                  revision: "1"
                }
              ]
            }

            sql {
                CREATE TABLE lore_table (
                    id TEXT PRIMARY KEY,
                    v  VECTOR(lore)
                );
                INSERT INTO lore_table (id, v) VALUES ('k1', embed('magic sword'));
            }
            """;

        await File.WriteAllTextAsync(puckPath, puckSource, cancellationToken: TestContext.Current.CancellationToken);

        // 1. Generate lock file and compile to JSON
        var embedExit = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath]);
        Assert.Equal(expected: 0, actual: embedExit);
        var compileExit = await PuckRootCommand.InvokeAsync(args: ["compile", puckPath]);
        Assert.Equal(expected: 0, actual: compileExit);
        Assert.True(File.Exists(worldJsonPath));
        Assert.True(File.Exists(lockPath));

        // 2. Decompile with companion lock beside the file
        var decompileWithLock = await PuckRootCommand.InvokeAsync(args: ["decompile", worldJsonPath, "-o", outWithLockPath]);
        Assert.Equal(expected: 0, actual: decompileWithLock);
        Assert.True(File.Exists(outWithLockPath));
        var textWithLock = await File.ReadAllTextAsync(outWithLockPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("embed(\"magic sword\")", textWithLock, StringComparison.Ordinal);

        // 3. Decompile without lock in an isolated directory
        using var noLockDir = new EmbedTestDirectory();
        var isolatedJson = Path.Combine(noLockDir.Path, "isolated.world.json");
        var isolatedOut = Path.Combine(noLockDir.Path, "isolated.puck");
        File.Copy(worldJsonPath, isolatedJson);

        var decompileWithoutLock = await PuckRootCommand.InvokeAsync(args: ["decompile", isolatedJson, "-o", isolatedOut]);
        Assert.Equal(expected: 0, actual: decompileWithoutLock);
        var textWithoutLock = await File.ReadAllTextAsync(isolatedOut, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("vector(", textWithoutLock, StringComparison.Ordinal);
        Assert.DoesNotContain("embed(", textWithoutLock, StringComparison.Ordinal);

        // 4. Decompile with explicit --embeddings option pointing to lockPath
        var explicitOut = Path.Combine(noLockDir.Path, "explicit.puck");
        var decompileExplicit = await PuckRootCommand.InvokeAsync(args: ["decompile", isolatedJson, "-o", explicitOut, "--embeddings", lockPath]);
        Assert.Equal(expected: 0, actual: decompileExplicit);
        var textExplicit = await File.ReadAllTextAsync(explicitOut, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("embed(\"magic sword\")", textExplicit, StringComparison.Ordinal);

        // 5. Decompile with --sql projects to SQL embed('...')
        var sqlOut = Path.Combine(dir.Path, "sql_decompiled.puck");
        var decompileSql = await PuckRootCommand.InvokeAsync(args: ["decompile", worldJsonPath, "-o", sqlOut, "--sql"]);
        Assert.Equal(expected: 0, actual: decompileSql);
        var textSql = await File.ReadAllTextAsync(sqlOut, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("embed('magic sword')", textSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecompileEmbedsPairReconstructsTableSugarAsync() {
        using var dir = new EmbedTestDirectory();
        var worldJsonPath = Path.Combine(dir.Path, "pair.world.json");
        var lockPath = Path.Combine(dir.Path, "pair.embeddings.json");
        var outPuckPath = Path.Combine(dir.Path, "pair_decompiled.puck");

        // Use puck embed on a small sql source to get real vectors for "dragon" and "knight"
        var tempPuck = Path.Combine(dir.Path, "temp.puck");
        var tempSource = """
            schema: "puck.world.definition.v1"
            state {
              spaces [
                { name: "lore", dimensions: 8, model: "puck-fixture", revision: "1" }
              ]
            }
            sql {
                CREATE TABLE t (id TEXT PRIMARY KEY, v VECTOR(lore));
                INSERT INTO t (id, v) VALUES ('1', embed('dragon')), ('2', embed('knight'));
            }
            """;
        await File.WriteAllTextAsync(tempPuck, tempSource, cancellationToken: TestContext.Current.CancellationToken);
        var embedTempExit = await PuckRootCommand.InvokeAsync(args: ["embed", tempPuck]);
        Assert.Equal(expected: 0, actual: embedTempExit);

        var tempLock = EmbeddingLock.TryLoad(tempPuck);
        Assert.NotNull(tempLock);
        Assert.True(tempLock.TryGet(spaceName: "lore", text: "dragon", vectorBase64Url: out var vec1));
        Assert.True(tempLock.TryGet(spaceName: "lore", text: "knight", vectorBase64Url: out var vec2));

        // Copy lock to pair.embeddings.json
        File.Copy(EmbeddingLock.DeriveLockPath(sourcePath: tempPuck), lockPath);

        var worldJson = $$"""
            {
              "schema": "puck.world.definition.v1",
              "state": {
                "spaces": [
                  { "name": "lore", "model": "puck-fixture", "revision": "1", "dimensions": 8 }
                ],
                "world": [
                  {
                    "name": "loreText",
                    "kind": "Text",
                    "capacity": 10,
                    "cells": [
                      { "key": "e1", "value": "dragon" },
                      { "key": "e2", "value": "knight" }
                    ]
                  },
                  {
                    "name": "loreVec",
                    "kind": "Vector",
                    "space": "lore",
                    "capacity": 10,
                    "cells": [
                      { "key": "e1", "value": "{{vec1}}" },
                      { "key": "e2", "value": "{{vec2}}" }
                    ]
                  }
                ]
              }
            }
            """;

        await File.WriteAllTextAsync(worldJsonPath, worldJson, cancellationToken: TestContext.Current.CancellationToken);

        var decompileExit = await PuckRootCommand.InvokeAsync(args: ["decompile", worldJsonPath, "-o", outPuckPath]);
        Assert.Equal(expected: 0, actual: decompileExit);

        var decompiledText = await File.ReadAllTextAsync(outPuckPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("embeds(loreVec)", decompiledText, StringComparison.Ordinal);
        Assert.DoesNotContain("table loreVec", decompiledText, StringComparison.Ordinal);
    }
}
