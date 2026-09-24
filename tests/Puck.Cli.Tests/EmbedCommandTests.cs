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
        var puckPath = Path.Combine(path1: dir.Path, path2: "world.puck");
        var lockPath = Path.Combine(path1: dir.Path, path2: "world.embeddings.json");

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
        var (compileBeforeEmbedCode, _, compileBeforeEmbedError) = await ConsoleCapture.RunSplitAsync(run: () => PuckRootCommand.InvokeAsync(args: ["compile", puckPath]));
        Assert.Equal(actual: compileBeforeEmbedCode, expected: 1);
        Assert.Contains(actualString: compileBeforeEmbedError, comparisonType: StringComparison.Ordinal, expectedSubstring: "PUCK079");

        // 2. Check before embed should report missing entries and exit 1
        var checkBeforeEmbedCode = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath, "--check"]);

        Assert.Equal(actual: checkBeforeEmbedCode, expected: 1);

        // 3. puck embed generates lock file deterministically
        var embedExitCode = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath]);

        Assert.Equal(actual: embedExitCode, expected: 0);
        Assert.True(condition: File.Exists(path: lockPath));

        var lockFile = EmbeddingLock.TryLoad(rootSourcePath: puckPath);

        Assert.NotNull(@object: lockFile);
        Assert.True(condition: lockFile.Spaces.ContainsKey(key: "lore"));
        var space = lockFile.Spaces["lore"];

        Assert.Equal(expected: 8, actual: space.Identity.Dimensions);
        Assert.Equal(expected: "puck-fixture", actual: space.Identity.Model);
        Assert.True(condition: lockFile.TryGet(spaceName: "lore", text: "hello world", vectorBase64Url: out var v1));
        Assert.True(condition: lockFile.TryGet(spaceName: "lore", text: "farewell", vectorBase64Url: out var v2));
        Assert.NotEmpty(collection: v1);
        Assert.NotEmpty(collection: v2);

        // 4. puck embed --check should now exit 0 without errors
        var checkAfterEmbedCode = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath, "--check"]);

        Assert.Equal(actual: checkAfterEmbedCode, expected: 0);

        // 5. puck compile should now succeed with exit code 0
        var compileAfterEmbedCode = await PuckRootCommand.InvokeAsync(args: ["compile", puckPath]);

        Assert.Equal(actual: compileAfterEmbedCode, expected: 0);

        // 6. Running embed again should be idempotent
        var lockJsonBefore = await File.ReadAllTextAsync(lockPath, cancellationToken: TestContext.Current.CancellationToken);
        var secondEmbedCode = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath]);

        Assert.Equal(actual: secondEmbedCode, expected: 0);
        var lockJsonAfter = await File.ReadAllTextAsync(lockPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(actual: lockJsonAfter, expected: lockJsonBefore);

        // 7. Generated fixture lock matches committed expected file byte-for-byte
        var expectedLockPath = Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "expected_fixture.embeddings.json");

        Assert.True(condition: File.Exists(path: expectedLockPath));
        var generatedBytes = await File.ReadAllBytesAsync(lockPath, cancellationToken: TestContext.Current.CancellationToken);
        var expectedBytes = await File.ReadAllBytesAsync(expectedLockPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(actual: generatedBytes, expected: expectedBytes);
    }
    [Fact]
    public async Task EmbedPrunesUnusedEntriesAndSpacesAsync() {
        using var dir = new EmbedTestDirectory();
        var puckPath = Path.Combine(path1: dir.Path, path2: "prune_test.puck");
        var lockPath = Path.Combine(path1: dir.Path, path2: "prune_test.embeddings.json");

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

        Assert.Equal(actual: initialEmbedCode, expected: 0);

        var lockFile = EmbeddingLock.TryLoad(rootSourcePath: puckPath);

        Assert.NotNull(@object: lockFile);
        Assert.True(condition: lockFile.Spaces.ContainsKey(key: "spaceA"));
        Assert.True(condition: lockFile.Spaces.ContainsKey(key: "spaceB"));
        Assert.True(condition: lockFile.TryGet(spaceName: "spaceA", text: "keep_me", vectorBase64Url: out _));
        Assert.True(condition: lockFile.TryGet(spaceName: "spaceA", text: "drop_me", vectorBase64Url: out _));
        Assert.True(condition: lockFile.TryGet(spaceName: "spaceB", text: "drop_space", vectorBase64Url: out _));

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

        Assert.Equal(actual: checkCode, expected: 1);

        // Embed without --check should prune unused entries and unused spaceB
        var pruneEmbedCode = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath]);

        Assert.Equal(actual: pruneEmbedCode, expected: 0);

        var prunedLock = EmbeddingLock.TryLoad(rootSourcePath: puckPath);

        Assert.NotNull(@object: prunedLock);
        Assert.True(condition: prunedLock.Spaces.ContainsKey(key: "spaceA"));
        Assert.False(condition: prunedLock.Spaces.ContainsKey(key: "spaceB"));
        Assert.True(condition: prunedLock.TryGet(spaceName: "spaceA", text: "keep_me", vectorBase64Url: out _));
        Assert.False(condition: prunedLock.TryGet(spaceName: "spaceA", text: "drop_me", vectorBase64Url: out _));

        // Check now succeeds
        var checkAfterPrune = await PuckRootCommand.InvokeAsync(args: ["embed", puckPath, "--check"]);

        Assert.Equal(actual: checkAfterPrune, expected: 0);
    }
    [Fact]
    public async Task ProbeSubcommandRanksVectorsAsync() {
        using var dir = new EmbedTestDirectory();
        var puckPath = Path.Combine(path1: dir.Path, path2: "probe_test.puck");

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

        Assert.Equal(actual: embedExitCode, expected: 0);

        // Probe for an existing text
        var probeExistingCode = await PuckRootCommand.InvokeAsync(args: ["embed", "probe", puckPath, "fire breathing dragon"]);

        Assert.Equal(actual: probeExistingCode, expected: 0);

        // Probe for a new query text (fixture generator generates embedding on the fly)
        var probeNewCode = await PuckRootCommand.InvokeAsync(args: ["embed", "probe", puckPath, "scary reptile creature", "--top", "2"]);

        Assert.Equal(actual: probeNewCode, expected: 0);
    }
    [Fact]
    public async Task DecompileWithLockResolvesEmbedAndWithoutLockResolvesVectorAsync() {
        using var dir = new EmbedTestDirectory();
        var puckPath = Path.Combine(path1: dir.Path, path2: "world.puck");
        var worldJsonPath = Path.Combine(path1: dir.Path, path2: "world.world.json");
        var lockPath = Path.Combine(path1: dir.Path, path2: "world.embeddings.json");
        var outWithLockPath = Path.Combine(path1: dir.Path, path2: "world_decompiled.puck");

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

        Assert.Equal(actual: embedExit, expected: 0);
        var compileExit = await PuckRootCommand.InvokeAsync(args: ["compile", puckPath]);

        Assert.Equal(actual: compileExit, expected: 0);
        Assert.True(condition: File.Exists(path: worldJsonPath));
        Assert.True(condition: File.Exists(path: lockPath));

        // 2. Decompile with companion lock beside the file
        var decompileWithLock = await PuckRootCommand.InvokeAsync(args: ["decompile", worldJsonPath, "-o", outWithLockPath]);

        Assert.Equal(actual: decompileWithLock, expected: 0);
        Assert.True(condition: File.Exists(path: outWithLockPath));
        var textWithLock = await File.ReadAllTextAsync(outWithLockPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(actualString: textWithLock, comparisonType: StringComparison.Ordinal, expectedSubstring: "embed(\"magic sword\")");

        // 3. Decompile without lock in an isolated directory
        using var noLockDir = new EmbedTestDirectory();
        var isolatedJson = Path.Combine(path1: noLockDir.Path, path2: "isolated.world.json");
        var isolatedOut = Path.Combine(path1: noLockDir.Path, path2: "isolated.puck");

        File.Copy(destFileName: isolatedJson, sourceFileName: worldJsonPath);

        var decompileWithoutLock = await PuckRootCommand.InvokeAsync(args: ["decompile", isolatedJson, "-o", isolatedOut]);

        Assert.Equal(actual: decompileWithoutLock, expected: 0);
        var textWithoutLock = await File.ReadAllTextAsync(isolatedOut, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(actualString: textWithoutLock, comparisonType: StringComparison.Ordinal, expectedSubstring: "vector(");
        Assert.DoesNotContain(actualString: textWithoutLock, comparisonType: StringComparison.Ordinal, expectedSubstring: "embed(");

        // 4. Decompile with explicit --embeddings option pointing to lockPath
        var explicitOut = Path.Combine(path1: noLockDir.Path, path2: "explicit.puck");
        var decompileExplicit = await PuckRootCommand.InvokeAsync(args: ["decompile", isolatedJson, "-o", explicitOut, "--embeddings", lockPath]);

        Assert.Equal(actual: decompileExplicit, expected: 0);
        var textExplicit = await File.ReadAllTextAsync(explicitOut, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(actualString: textExplicit, comparisonType: StringComparison.Ordinal, expectedSubstring: "embed(\"magic sword\")");

        // 5. Decompile with --sql projects to SQL embed('...')
        var sqlOut = Path.Combine(path1: dir.Path, path2: "sql_decompiled.puck");
        var decompileSql = await PuckRootCommand.InvokeAsync(args: ["decompile", worldJsonPath, "-o", sqlOut, "--sql"]);

        Assert.Equal(actual: decompileSql, expected: 0);
        var textSql = await File.ReadAllTextAsync(sqlOut, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(actualString: textSql, comparisonType: StringComparison.Ordinal, expectedSubstring: "embed('magic sword')");
    }
    [Fact]
    public async Task DecompileEmbedsPairReconstructsTableSugarAsync() {
        using var dir = new EmbedTestDirectory();
        var worldJsonPath = Path.Combine(path1: dir.Path, path2: "pair.world.json");
        var lockPath = Path.Combine(path1: dir.Path, path2: "pair.embeddings.json");
        var outPuckPath = Path.Combine(path1: dir.Path, path2: "pair_decompiled.puck");

        // Use puck embed on a small sql source to get real vectors for "dragon" and "knight"
        var tempPuck = Path.Combine(path1: dir.Path, path2: "temp.puck");
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

        Assert.Equal(actual: embedTempExit, expected: 0);

        var tempLock = EmbeddingLock.TryLoad(rootSourcePath: tempPuck);

        Assert.NotNull(@object: tempLock);
        Assert.True(condition: tempLock.TryGet(spaceName: "lore", text: "dragon", vectorBase64Url: out var vec1));
        Assert.True(condition: tempLock.TryGet(spaceName: "lore", text: "knight", vectorBase64Url: out var vec2));

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

        Assert.Equal(actual: decompileExit, expected: 0);

        var decompiledText = await File.ReadAllTextAsync(outPuckPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(actualString: decompiledText, comparisonType: StringComparison.Ordinal, expectedSubstring: "embeds(loreVec)");
        Assert.DoesNotContain(actualString: decompiledText, comparisonType: StringComparison.Ordinal, expectedSubstring: "table loreVec");
    }
}
