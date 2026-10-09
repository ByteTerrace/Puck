using Xunit;

namespace Puck.Cli.Core.Tests;

/// <summary>Laws for the revision check <c>puck --version</c> and <c>puck mcp --profile operator</c> run: a CLI built at a
/// commit other than its checkout's HEAD says so and names both, a CLI at HEAD says nothing, and a directory that is not
/// a Puck checkout is never read as one.</summary>
public sealed class CliRevisionLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void AVersionCarriesItsRevisionAsBuildMetadata() {
        Assert.Equal(
            actual: CliRevision.Describe(revision: "abc123", version: "0.1.0-alpha"),
            expected: "0.1.0-alpha+abc123"
        );
        Assert.Equal(
            actual: CliRevision.Describe(revision: null, version: "0.1.0-alpha"),
            expected: "0.1.0-alpha"
        );
    }
    [Fact]
    public async Task ACliBuiltAtAnotherCommitNamesBothAndTheReinstall() {
        const string Elsewhere = "0123456789abcdef0123456789abcdef01234567";

        using var checkout = new GitScratchCheckout();

        checkout.Write(name: "Puck.slnx", text: "<Solution />\n");
        checkout.Write(name: "src/readme.txt", text: "x\n");

        var head = checkout.Commit(message: "head");
        var nested = Path.Combine(path1: checkout.Root, path2: "src");
        var stale = await CliRevision.StaleWarningAsync(
            built: Elsewhere,
            cancellationToken: Token,
            workingDirectory: nested
        );

        Assert.NotNull(@object: stale);
        Assert.Contains(actualString: stale, expectedSubstring: Elsewhere);
        Assert.Contains(actualString: stale, expectedSubstring: head);
        Assert.Contains(actualString: stale, expectedSubstring: CliRevision.ReinstallReference);
        Assert.Null(@object: await CliRevision.StaleWarningAsync(
            built: head,
            cancellationToken: Token,
            workingDirectory: nested
        ));
        Assert.Null(@object: await CliRevision.StaleWarningAsync(
            built: null,
            cancellationToken: Token,
            workingDirectory: nested
        ));

        // The same commit history without a solution file is not a Puck checkout, so it is never read.
        File.Delete(path: Path.Combine(path1: checkout.Root, path2: "Puck.slnx"));
        _ = checkout.Commit(message: "not puck");

        Assert.Null(@object: await CliRevision.StaleWarningAsync(
            built: Elsewhere,
            cancellationToken: Token,
            workingDirectory: nested
        ));
    }
}
