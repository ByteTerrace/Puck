using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: every scratch repository a law creates through <see cref="GitScratchCheckout"/> turns
/// off git's automatic maintenance, so no commit in a law starts a background prune or repack.</summary>
public sealed class GitScratchCheckoutLawTests {
    private static void AssertNeverMaintained(string repository) {
        Assert.Equal(expected: "false", actual: CliGit.Run(repository, "config", "--get", "maintenance.auto").Stdout.Trim());
        Assert.Equal(expected: "0", actual: CliGit.Run(repository, "config", "--get", "gc.auto").Stdout.Trim());
    }

    [Fact]
    public void ACheckoutIsNeverMaintained() {
        using var checkout = new GitScratchCheckout();

        AssertNeverMaintained(repository: checkout.Root);
    }
    [Fact]
    public void AnInitializedDirectoryIsNeverMaintained() {
        using var directory = new TemporaryDirectory(prefix: "puck-git-init-law-");

        GitScratchCheckout.Initialize(repository: directory.RootPath);
        AssertNeverMaintained(repository: directory.RootPath);
        foreach (var file in Directory.EnumerateFiles(path: directory.RootPath, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            File.SetAttributes(fileAttributes: FileAttributes.Normal, path: file);
        }
    }
}
