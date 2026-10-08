using System.Buffers.Binary;
using System.Text;
using Puck.Cli.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for <c>puck shaders compare</c>: two trees holding the same compiled shaders match; a differing byte fails by
/// name with the first byte that differs, and a DXIL difference also names the container chunks that differ; a file one
/// tree lacks fails by name; copies under <c>bin</c> and
/// <c>obj</c> are not compared; a tree with no bytecode is refused rather than matched; <c>puck shaders collect</c>
/// copies a checkout's bytecode, and nothing under <c>bin</c> or <c>artifacts</c>, into a tree that matches it; and on
/// the real tree the projects whose build compiles shaders with DXC are those owning a tracked stage source outside
/// <c>experimental/</c>.
/// </summary>
public sealed class ShadersCompareLawTests {
    // Two trees, each written from its own files, compared by the verb.
    private static (int ExitCode, string Output, string Error) Compare(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual) {
        using var root = new TemporaryDirectory(prefix: "puck-shaders-compare-");

        foreach (var (tree, files) in ((ReadOnlySpan<(string, Dictionary<string, byte[]>)>)[("expected", expected), ("actual", actual)])) {
            foreach (var (path, bytes) in files) {
                var full = Path.Combine(path1: root.RootPath, path2: tree, path3: path);

                _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: full)!);
                File.WriteAllBytes(bytes: bytes, path: full);
            }

            _ = Directory.CreateDirectory(path: Path.Combine(path1: root.RootPath, path2: tree));
        }

        return ConsoleCapture.RunSplit(run: () => CompareCommand.Run(
            actualRoot: Path.Combine(path1: root.RootPath, path2: "actual"),
            expectedRoot: Path.Combine(path1: root.RootPath, path2: "expected")
        ));
    }
    private static Dictionary<string, byte[]> Tree(params (string Path, byte[] Bytes)[] files) => files.ToDictionary(
        comparer: StringComparer.Ordinal,
        elementSelector: static file => file.Bytes,
        keySelector: static file => file.Path
    );

    [Fact]
    public void TreesHoldingTheSameBytecodeMatch() {
        var (exitCode, output, error) = Compare(
            actual: Tree(("src/A/Assets/Shaders/blit.frag.spv", [1, 2, 3]), ("src/A/Assets/Shaders/blit.frag.dxil", [4, 5])),
            expected: Tree(("src/A/Assets/Shaders/blit.frag.spv", [1, 2, 3]), ("src/A/Assets/Shaders/blit.frag.dxil", [4, 5]))
        );

        Assert.Equal(actual: exitCode, expected: 0);
        Assert.Contains(actualString: output, expectedSubstring: "all 2 compiled shaders match byte for byte");
        Assert.Empty(collection: error.Trim());
    }
    [Fact]
    public void ADifferingByteFailsByNameAtTheFirstByteThatDiffers() {
        var (exitCode, _, error) = Compare(
            actual: Tree(("src/A/Assets/Shaders/place.comp.dxil", [9, 9, 7, 9]), ("src/A/Assets/Shaders/place.comp.spv", [1])),
            expected: Tree(("src/A/Assets/Shaders/place.comp.dxil", [9, 9, 9, 9]), ("src/A/Assets/Shaders/place.comp.spv", [1]))
        );

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Contains(actualString: error, expectedSubstring: "src/A/Assets/Shaders/place.comp.dxil differs from byte 2 (4 bytes expected, 4 actual)");
        Assert.DoesNotContain(actualString: error, expectedSubstring: "place.comp.spv");

        var (shorter, _, truncated) = Compare(
            actual: Tree(("src/A/x.spv", [1, 2])),
            expected: Tree(("src/A/x.spv", [1, 2, 3]))
        );

        Assert.Equal(actual: shorter, expected: 1);
        Assert.Contains(actualString: truncated, expectedSubstring: "src/A/x.spv differs from byte 2 (3 bytes expected, 2 actual)");
    }

    // A DXBC container holding each chunk in order, under an all-zero digest but for its first byte.
    private static byte[] Container(byte digest, params (string Code, byte[] Payload)[] chunks) {
        var table = (32 + (chunks.Length * 4));
        var bytes = new byte[(table + chunks.Sum(selector: static chunk => (8 + chunk.Payload.Length)))];
        var offset = table;

        "DXBC"u8.CopyTo(destination: bytes);
        bytes[4] = digest;
        BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes.AsSpan(start: 24), value: ((uint)bytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes.AsSpan(start: 28), value: ((uint)chunks.Length));
        for (var index = 0; (index < chunks.Length); index++) {
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes.AsSpan(start: (32 + (index * 4))), value: ((uint)offset));
            Encoding.ASCII.GetBytes(s: chunks[index].Code).CopyTo(array: bytes, index: offset);
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes.AsSpan(start: (offset + 4)), value: ((uint)chunks[index].Payload.Length));
            chunks[index].Payload.CopyTo(array: bytes, index: (offset + 8));
            offset += (8 + chunks[index].Payload.Length);
        }

        return bytes;
    }

    // A DXIL difference names the container chunks that differ, the program, its reflection or its pipeline state,
    // since the container digest at byte 4 differs whenever anything does; a difference in the header alone says so.
    [Fact]
    public void ADxilDifferenceNamesTheContainerChunksThatDiffer() {
        var expected = Container(1, ("DXIL", [1, 2, 3, 4]), ("STAT", [5, 6, 7, 8]), ("HASH", [0, 0, 0, 0]));

        var (exitCode, _, error) = Compare(
            actual: Tree(("src/A/k.comp.dxil", Container(2, ("DXIL", [1, 2, 3, 5, 0, 0, 0, 0]), ("STAT", [5, 6, 7, 8]), ("HASH", [0, 0, 0, 1])))),
            expected: Tree(("src/A/k.comp.dxil", expected))
        );

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Contains(actualString: error, expectedSubstring: "src/A/k.comp.dxil differs from byte 4 (80 bytes expected, 84 actual); DXBC chunks that differ: DXIL (4 expected, 8 actual), HASH (4 expected, 4 actual).");

        var (headerOnly, _, header) = Compare(
            actual: Tree(("src/A/k.comp.dxil", Container(2, ("DXIL", [1, 2, 3, 4]), ("STAT", [5, 6, 7, 8]), ("HASH", [0, 0, 0, 0])))),
            expected: Tree(("src/A/k.comp.dxil", expected))
        );

        Assert.Equal(actual: headerOnly, expected: 1);
        Assert.Contains(actualString: header, expectedSubstring: "differs from byte 4 (80 bytes expected, 80 actual); every container chunk matches, so only the header differs.");
    }
    [Fact]
    public void AFileOneTreeLacksFailsByName() {
        var (exitCode, _, error) = Compare(
            actual: Tree(("src/A/b.spv", [1]), ("src/A/c.dxil", [2])),
            expected: Tree(("src/A/a.spv", [1]), ("src/A/b.spv", [1]))
        );

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Contains(actualString: error, expectedSubstring: "src/A/a.spv is in the expected tree only");
        Assert.Contains(actualString: error, expectedSubstring: "src/A/c.dxil is in the actual tree only");
    }
    [Fact]
    public void BuildOutputCopiesAreNotCompared() {
        var (exitCode, output, _) = Compare(
            actual: Tree(("src/A/k.spv", [1]), ("src/A/bin/Release/net10.0/k.spv", [7]), ("src/A/obj/k.dxil", [8])),
            expected: Tree(("src/A/k.spv", [1]))
        );

        Assert.Equal(actual: exitCode, expected: 0);
        Assert.Contains(actualString: output, expectedSubstring: "all 1 compiled shaders match");
    }
    [Fact]
    public void ATreeWithNoBytecodeIsRefused() {
        var (exitCode, _, error) = Compare(
            actual: Tree(("src/A/k.spv", [1])),
            expected: Tree(("src/A/readme.txt", [1]))
        );

        Assert.Equal(actual: exitCode, expected: 2);
        Assert.Contains(actualString: error, expectedSubstring: "the expected tree");
        Assert.Contains(actualString: error, expectedSubstring: "holds no .spv or .dxil file");
    }
    [Fact]
    public void ACollectedTreeMatchesTheCheckoutItCameFrom() {
        using var root = new TemporaryDirectory(prefix: "puck-shaders-collect-");

        var checkout = Path.Combine(path1: root.RootPath, path2: "checkout");
        var collected = Path.Combine(path1: root.RootPath, path2: "collected");

        foreach (var (path, bytes) in ((ReadOnlySpan<(string, byte[])>)[("src/A/Assets/Shaders/k.comp.spv", [1, 2]), ("tests/B/p.frag.dxil", [3]), ("src/A/bin/Release/k.comp.spv", [9]), ("artifacts/world/k.comp.spv", [9]), ("src/A/k.hlsl", [0])])) {
            var full = Path.Combine(path1: checkout, path2: path);

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: full)!);
            File.WriteAllBytes(bytes: bytes, path: full);
        }

        var (exitCode, _, _) = ConsoleCapture.RunSplit(run: () => CompareCommand.Collect(destination: collected, root: checkout));

        Assert.Equal(actual: exitCode, expected: 0);
        Assert.Equal(
            actual: CompareCommand.Bytecode(root: collected).Keys,
            expected: ["src/A/Assets/Shaders/k.comp.spv", "tests/B/p.frag.dxil"]
        );
        Assert.Equal(expected: 0, actual: ConsoleCapture.RunSplit(run: () => CompareCommand.Run(actualRoot: checkout, expectedRoot: collected)).ExitCode);
        Assert.Equal(expected: 2, actual: ConsoleCapture.RunSplit(run: () => CompareCommand.Collect(destination: collected, root: Path.Combine(path1: checkout, path2: "missing"))).ExitCode);
    }

    // The tracked files a pattern matches outside experimental/, repository-relative with forward slashes.
    private static IReadOnlyList<string> Tracked(string repositoryRoot, string pattern) => [.. CliGit.Run(repositoryRoot, "ls-files", "--", pattern).Stdout
        .Split(separator: '\n')
        .Select(selector: static line => line.TrimEnd(trimChar: '\r'))
        .Where(predicate: static line => ((line.Length > 0) && !line.StartsWith(comparisonType: StringComparison.Ordinal, value: "experimental/")))];

    // On the real tree the projects whose build compiles shaders with DXC are exactly the projects that own a tracked
    // stage source, found from the files rather than from any project's items, so a project that gains or loses a
    // shader moves both sides and this law never needs editing. Test projects are among them: CI's Windows build
    // compiles the whole solution and collects every kernel it wrote, so the Linux compare compiles theirs too.
    [Fact]
    public void OnTheTreeTheShaderProjectsAreTheProjectsOwningATrackedStageSource() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var projects = Tracked(pattern: "*.csproj", repositoryRoot: repositoryRoot);
        var owners = new SortedSet<string>(comparer: StringComparer.Ordinal);

        // A stage source belongs to the deepest tracked project whose directory holds it. Canary and counters fixture
        // sources are compiled by the World they are handed to (a world.shaders.reload tree), never by a build.
        foreach (var source in ((string[])["*.comp.hlsl", "*.vert.hlsl", "*.frag.hlsl"]).SelectMany(selector: pattern => Tracked(pattern: pattern, repositoryRoot: repositoryRoot)).Where(predicate: static source => !(source.StartsWith(comparisonType: StringComparison.Ordinal, value: "tests/Puck.World.Canaries/") || source.StartsWith(comparisonType: StringComparison.Ordinal, value: "tests/Puck.Counters/")))) {
            var owner = projects
                .Where(predicate: project => source.StartsWith(comparisonType: StringComparison.Ordinal, value: (project[..(project.LastIndexOf(value: '/') + 1)])))
                .MaxBy(keySelector: static project => project.Length);

            Assert.True(
                condition: (owner is not null),
                userMessage: $"{source} lies in no tracked project."
            );
            _ = owners.Add(item: owner!);
        }

        Assert.NotEmpty(collection: owners);
        Assert.Equal(
            actual: CompareCommand.ShaderProjects(
                projects: projects,
                repositoryRoot: repositoryRoot
            ),
            expected: owners
        );
    }
}
