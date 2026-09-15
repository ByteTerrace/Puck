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
        var compileBeforeEmbedCode = await PuckRootCommand.InvokeAsync(args: ["compile", puckPath]);
        Assert.Equal(expected: 1, actual: compileBeforeEmbedCode);

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
}
