using Puck.Testing;
using Xunit;

namespace Puck.Cli.Shaders.Tests;

/// <summary>
/// Laws for <c>puck shaders compare</c>: it covers exactly the SPIR-V modules, so a DXIL file, differing, missing or
/// alone, is no part of it; two trees holding the same SPIR-V match; a differing byte fails by name with the first byte
/// that differs; a module one tree lacks fails by name; copies under <c>bin</c> and <c>obj</c> are not compared; a tree
/// with no SPIR-V is refused rather than matched; <c>puck shaders collect</c> copies a checkout's SPIR-V, and no DXIL
/// and nothing under <c>bin</c> or <c>artifacts</c>, into a tree that matches it; and on the real tree the projects
/// whose build compiles shaders with DXC are those owning a tracked stage source outside <c>experimental/</c>.
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

    // Only a Windows build compiles DXIL (build/Shaders.targets), so the cross-host comparison holds SPIR-V alone: a
    // DXIL file that differs, that one tree lacks, or that is the only file of a stage, decides nothing and is not
    // counted.
    [Fact]
    public void TheComparisonCoversExactlyTheSpirvModules() {
        var (exitCode, output, error) = Compare(
            actual: Tree(("src/A/Assets/Shaders/blit.frag.spv", [1, 2, 3]), ("src/A/Assets/Shaders/blit.frag.dxil", [4, 6]), ("src/A/Assets/Shaders/linux.comp.dxil", [7])),
            expected: Tree(("src/A/Assets/Shaders/blit.frag.spv", [1, 2, 3]), ("src/A/Assets/Shaders/blit.frag.dxil", [4, 5]), ("src/A/Assets/Shaders/place.comp.dxil", [9]))
        );

        Assert.Equal(actual: exitCode, expected: 0);
        Assert.Contains(actualString: output, expectedSubstring: "all 1 SPIR-V modules match byte for byte");
        Assert.Empty(collection: error.Trim());
    }
    [Fact]
    public void TreesHoldingTheSameSpirvMatch() {
        var (exitCode, output, error) = Compare(
            actual: Tree(("src/A/Assets/Shaders/blit.frag.spv", [1, 2, 3]), ("src/A/Assets/Shaders/blit.vert.spv", [4, 5])),
            expected: Tree(("src/A/Assets/Shaders/blit.frag.spv", [1, 2, 3]), ("src/A/Assets/Shaders/blit.vert.spv", [4, 5]))
        );

        Assert.Equal(actual: exitCode, expected: 0);
        Assert.Contains(actualString: output, expectedSubstring: "all 2 SPIR-V modules match byte for byte");
        Assert.Empty(collection: error.Trim());
    }
    [Fact]
    public void ADifferingByteFailsByNameAtTheFirstByteThatDiffers() {
        var (exitCode, _, error) = Compare(
            actual: Tree(("src/A/Assets/Shaders/place.comp.spv", [9, 9, 7, 9]), ("src/A/Assets/Shaders/blit.frag.spv", [1])),
            expected: Tree(("src/A/Assets/Shaders/place.comp.spv", [9, 9, 9, 9]), ("src/A/Assets/Shaders/blit.frag.spv", [1]))
        );

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Contains(actualString: error, expectedSubstring: "src/A/Assets/Shaders/place.comp.spv differs from byte 2 (4 bytes expected, 4 actual)");
        Assert.DoesNotContain(actualString: error, expectedSubstring: "blit.frag.spv");

        var (shorter, _, truncated) = Compare(
            actual: Tree(("src/A/x.spv", [1, 2])),
            expected: Tree(("src/A/x.spv", [1, 2, 3]))
        );

        Assert.Equal(actual: shorter, expected: 1);
        Assert.Contains(actualString: truncated, expectedSubstring: "src/A/x.spv differs from byte 2 (3 bytes expected, 2 actual)");
    }
    [Fact]
    public void AModuleOneTreeLacksFailsByName() {
        var (exitCode, _, error) = Compare(
            actual: Tree(("src/A/b.spv", [1]), ("src/A/c.spv", [2])),
            expected: Tree(("src/A/a.spv", [1]), ("src/A/b.spv", [1]))
        );

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Contains(actualString: error, expectedSubstring: "src/A/a.spv is in the expected tree only");
        Assert.Contains(actualString: error, expectedSubstring: "src/A/c.spv is in the actual tree only");
    }
    [Fact]
    public void BuildOutputCopiesAreNotCompared() {
        var (exitCode, output, _) = Compare(
            actual: Tree(("src/A/k.spv", [1]), ("src/A/bin/Release/net10.0/k.spv", [7]), ("src/A/obj/k.spv", [8])),
            expected: Tree(("src/A/k.spv", [1]))
        );

        Assert.Equal(actual: exitCode, expected: 0);
        Assert.Contains(actualString: output, expectedSubstring: "all 1 SPIR-V modules match");
    }
    [Fact]
    public void ATreeWithNoSpirvIsRefused() {
        var (exitCode, _, error) = Compare(
            actual: Tree(("src/A/k.spv", [1])),
            expected: Tree(("src/A/readme.txt", [1]), ("src/A/k.dxil", [1]))
        );

        Assert.Equal(actual: exitCode, expected: 2);
        Assert.Contains(actualString: error, expectedSubstring: "the expected tree");
        Assert.Contains(actualString: error, expectedSubstring: "holds no .spv file");
    }
    [Fact]
    public void ACollectedTreeMatchesTheCheckoutItCameFrom() {
        using var root = new TemporaryDirectory(prefix: "puck-shaders-collect-");

        var checkout = Path.Combine(path1: root.RootPath, path2: "checkout");
        var collected = Path.Combine(path1: root.RootPath, path2: "collected");

        foreach (var (path, bytes) in ((ReadOnlySpan<(string, byte[])>)[("src/A/Assets/Shaders/k.comp.spv", [1, 2]), ("tests/B/p.frag.spv", [3]), ("tests/B/p.frag.dxil", [4]), ("src/A/bin/Release/k.comp.spv", [9]), ("artifacts/world/k.comp.spv", [9]), ("src/A/k.hlsl", [0])])) {
            var full = Path.Combine(path1: checkout, path2: path);

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: full)!);
            File.WriteAllBytes(bytes: bytes, path: full);
        }

        var (exitCode, _, _) = ConsoleCapture.RunSplit(run: () => CompareCommand.Collect(destination: collected, root: checkout));

        Assert.Equal(actual: exitCode, expected: 0);
        Assert.Equal(
            actual: Directory.EnumerateFiles(path: collected, searchOption: SearchOption.AllDirectories, searchPattern: "*")
                .Select(selector: file => Path.GetRelativePath(path: file, relativeTo: collected).Replace(newChar: '/', oldChar: '\\'))
                .Order(comparer: StringComparer.Ordinal),
            expected: ["src/A/Assets/Shaders/k.comp.spv", "tests/B/p.frag.spv"]
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
