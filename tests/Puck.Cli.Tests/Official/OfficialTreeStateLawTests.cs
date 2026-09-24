using Puck.Cli.Official;
using Puck.Launcher.Release;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests.Official;

/// <summary>CONTRACT UNDER TEST: <c>puck official build</c> names the tree it built, not the build of the CLI that
/// built it. <see cref="OfficialBuildCommand.TryReadTree"/> reads the HEAD commit of the git checkout holding the worlds
/// directory, counts the tree dirty when git status reports anything under that directory (a tracked file modified,
/// staged or deleted, or an untracked or ignored file) and nothing elsewhere in the checkout, and stamps
/// <see cref="OfficialBuildInfo.NoCommit"/>, dirty, for a directory no checkout holds or a checkout with no commit.
/// Each law runs over a checkout of its own in a temporary directory, so none reads this repository's shared
/// working-tree state.</summary>
public sealed class OfficialTreeStateLawTests {
    private const string Worlds = "worlds";

    private sealed class Checkout : IDisposable {
        private readonly TemporaryDirectory m_directory = new();

        public Checkout(bool commit = true) {
            Write(
                name: "worlds/klondike.puck",
                text: "schema: \"puck.world.definition.v1\"\n"
            );
            Write(
                name: "docs/guide.md",
                text: "# Guide\n"
            );
            Write(
                name: ".gitignore",
                text: "*.scratch\n"
            );
            Git("init", "--quiet", "--initial-branch=main");

            if (commit) {
                Git("add", "--all");
                Commit(message: "initial");
            }
        }

        public string WorldsDirectory => m_directory.PathOf(name: Worlds);

        public void Commit(string message) =>
            Git("-c", "user.name=law", "-c", "user.email=law@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet", "--all", "--message", message);
        public void Dispose() {
            // Git writes its object files read-only, which a recursive delete refuses on Windows.
            foreach (var file in Directory.EnumerateFiles(
                path: m_directory.RootPath,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*"
            )) {
                File.SetAttributes(
                    fileAttributes: FileAttributes.Normal,
                    path: file
                );
            }

            m_directory.Dispose();
        }
        public void Git(params string[] arguments) {
            var result = CliGit.Run(
                arguments: [.. arguments],
                repository: m_directory.RootPath
            );

            Assert.True(
                condition: (result.ExitCode == 0),
                userMessage: $"git {string.Join(separator: ' ', values: arguments)} exited {result.ExitCode}: {result.Stderr}"
            );
        }
        public string Head() {
            var result = CliGit.Run(
                arguments: ["rev-parse", "HEAD"],
                repository: m_directory.RootPath
            );

            Assert.Equal(
                actual: result.ExitCode,
                expected: 0
            );

            return result.Stdout.Trim();
        }
        public void Write(string name, string text) =>
            m_directory.WriteText(
                name: name,
                text: text
            );
    }

    private static (string Commit, bool Dirty) Read(string worldsDirectory) {
        Assert.True(
            condition: OfficialBuildCommand.TryReadTree(
                commit: out var commit,
                dirty: out var dirty,
                refusal: out var refusal,
                worldsDirectory: worldsDirectory
            ),
            userMessage: refusal
        );

        return (commit, dirty);
    }

    /// <summary>A worlds directory exactly as its checkout's HEAD holds it is named by that commit and is clean.</summary>
    [Fact]
    public void ACleanTreeIsNamedByItsHeadCommit() {
        using var checkout = new Checkout();

        Assert.Equal(
            actual: Read(worldsDirectory: checkout.WorldsDirectory),
            expected: (checkout.Head(), false)
        );
    }
    /// <summary>A modified, deleted, staged, untracked or ignored file under the worlds directory makes the tree differ
    /// from its commit, which it is still named by.</summary>
    [Theory]
    [InlineData("modified")]
    [InlineData("deleted")]
    [InlineData("staged")]
    [InlineData("untracked")]
    [InlineData("ignored")]
    public void AnyChangeUnderTheWorldsDirectoryMakesTheTreeDirty(string change) {
        using var checkout = new Checkout();
        var head = checkout.Head();
        var tracked = Path.Combine(
            path1: checkout.WorldsDirectory,
            path2: "klondike.puck"
        );

        switch (change) {
            case "modified":
                File.AppendAllText(
                    contents: "// edited\n",
                    path: tracked
                );
                break;
            case "deleted":
                File.Delete(path: tracked);
                break;
            case "staged":
                File.AppendAllText(
                    contents: "// edited\n",
                    path: tracked
                );
                checkout.Git("add", "--all");
                break;
            case "untracked":
                checkout.Write(
                    name: "worlds/spider.world.json",
                    text: "{}\n"
                );
                break;
            default:
                checkout.Write(
                    name: "worlds/notes.scratch",
                    text: "scratch\n"
                );
                break;
        }

        Assert.Equal(
            actual: Read(worldsDirectory: checkout.WorldsDirectory),
            expected: (head, true)
        );
    }
    /// <summary>A change elsewhere in the checkout is not the worlds tree's: the build still names a clean commit.</summary>
    [Fact]
    public void AChangeOutsideTheWorldsDirectoryLeavesTheTreeClean() {
        using var checkout = new Checkout();

        checkout.Write(
            name: "docs/guide.md",
            text: "# Guide, edited\n"
        );
        checkout.Write(
            name: "docs/new.md",
            text: "# New\n"
        );

        Assert.Equal(
            actual: Read(worldsDirectory: checkout.WorldsDirectory),
            expected: (checkout.Head(), false)
        );
    }
    /// <summary>A checkout whose HEAD names no commit yet holds nothing the build read.</summary>
    [Fact]
    public void ACheckoutWithNoCommitIsNamedNone() {
        using var checkout = new Checkout(commit: false);

        Assert.Equal(
            actual: Read(worldsDirectory: checkout.WorldsDirectory),
            expected: (OfficialBuildInfo.NoCommit, true)
        );
    }
    /// <summary>A worlds directory no checkout holds is stamped <c>none</c> rather than guessed at, and counts as
    /// differing, since no commit holds it.</summary>
    [Fact]
    public void ADirectoryOutsideEveryCheckoutIsNamedNone() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(
            name: "worlds/klondike.puck",
            text: "schema: \"puck.world.definition.v1\"\n"
        );
        Assert.Null(@object: RepositoryPaths.Ascend(
            probe: static candidate => (Path.Exists(path: Path.Combine(
                path1: candidate.FullName,
                path2: ".git"
            ))
                ? candidate.FullName
                : null
            ),
            start: directory.RootPath
        ));
        Assert.Equal(
            actual: Read(worldsDirectory: directory.PathOf(name: Worlds)),
            expected: (OfficialBuildInfo.NoCommit, true)
        );
    }
    /// <summary>Without <c>--allow-dirty</c>, the verb refuses a tree no commit holds before it reads the engine or
    /// writes anything, and names why.</summary>
    [Fact]
    public void TheBuildRefusesATreeNoCommitHoldsWithoutAllowDirty() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(
            name: "worlds/klondike.puck",
            text: "schema: \"puck.world.definition.v1\"\n"
        );

        var (exitCode, _, stdErr) = ConsoleCapture.RunSplit(run: () => OfficialBuildCommand.Create().Parse(args: [
            "--tree", directory.PathOf(name: "tree"),
            "--channel", "dev",
            "--engine", directory.PathOf(name: "engine"),
            "--worlds", directory.PathOf(name: Worlds),
        ]).Invoke());

        Assert.Equal(
            actual: exitCode,
            expected: 2
        );
        Assert.Contains(
            actualString: stdErr,
            expectedSubstring: "no commit holds the worlds directory"
        );
        Assert.False(condition: Directory.Exists(path: directory.PathOf(name: "tree")));
    }
}
