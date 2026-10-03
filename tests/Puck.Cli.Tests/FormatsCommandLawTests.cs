using Puck.Cli.Formats;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: formats refuses untracked sources before reading or writing its ledger, names
/// every omitted source, and permits recording and checking once sources are tracked, ignored, or generated.</summary>
public sealed class FormatsCommandLawTests {
    private const string Source = "namespace Puck.Demo;\npublic static class DemoCodec { public const string SchemaVersion = \"puck.demo.v1\"; }";
    private const string SourcePath = "src/Puck.Demo/DemoCodec.cs";
    private const string UntrackedPrefix = "formats: untracked source: ";

    private static GitScratchCheckout Checkout() {
        var checkout = new GitScratchCheckout();

        checkout.Write(name: SourcePath, text: Source);
        checkout.Write(name: "src/Puck.Demo/Puck.Demo.csproj", text: "<Project />");
        _ = checkout.Git("add", "--", SourcePath, "src/Puck.Demo/Puck.Demo.csproj");

        return checkout;
    }
    private static (int ExitCode, string Output, string Error) Run(GitScratchCheckout checkout, bool check) =>
        ConsoleCapture.RunSplit(run: () => FormatsCommand.Execute(repositoryRoot: checkout.Root, check: check));
    private static void AssertRefused(GitScratchCheckout checkout, bool check, params string[] paths) {
        var (exitCode, output, error) = Run(check: check, checkout: checkout);

        Assert.Equal(actual: exitCode, expected: CliExit.Refused);
        Assert.Empty(collection: output);
        Assert.Equal(expected: paths, actual: error.Split(separator: '\n')
            .Where(predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: UntrackedPrefix))
            .Select(selector: static line => line[UntrackedPrefix.Length..].TrimEnd()));
        Assert.Contains(actualString: error, expectedSubstring: "git add or remove");
        Assert.Contains(actualString: error, expectedSubstring: "tracked sources only");
        Assert.Contains(actualString: error, expectedSubstring: "cannot describe what will be committed");
    }
    private static void AssertSuccess(GitScratchCheckout checkout, bool check) {
        var (exitCode, _, error) = Run(check: check, checkout: checkout);

        Assert.True(condition: (exitCode == CliExit.Success), userMessage: error);
        Assert.Empty(collection: error);
    }
    private static void RefusalPreservesLedger(bool check, bool ledgerExists) {
        using var checkout = Checkout();
        var ledger = Path.Combine(path1: checkout.Root, path2: FormatVersionsLedger.FileName);

        if (ledgerExists) {
            AssertSuccess(check: false, checkout: checkout);
        }

        var before = (ledgerExists ? File.ReadAllBytes(path: ledger) : null);

        checkout.Write(name: "src/Zeta.cs", text: "public class Zeta { }");
        checkout.Write(name: "src/Puck.Demo/Nested/Alpha source.cs", text: "public class Alpha { }");
        AssertRefused(checkout, check, "src/Puck.Demo/Nested/Alpha source.cs", "src/Zeta.cs");
        if (before is null) {
            Assert.False(condition: File.Exists(path: ledger));
        } else {
            Assert.Equal(expected: before, actual: File.ReadAllBytes(path: ledger));
        }
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void UntrackedSourcesRefuseRecordingWithoutWritingTheLedger(bool ledgerExists) =>
        RefusalPreservesLedger(check: false, ledgerExists: ledgerExists);
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void UntrackedSourcesRefuseCheckingWithoutWritingTheLedger(bool ledgerExists) =>
        RefusalPreservesLedger(check: true, ledgerExists: ledgerExists);
    [Fact]
    public void IgnoringTheUntrackedSourceAllowsRecordingAndCheckingAlongsideGeneratedFiles() {
        using var checkout = Checkout();
        const string Ignored = "src/Puck.Demo/Ignored.cs";
        const string Generated = "src/Puck.Demo/Generated.g.cs";

        checkout.Write(name: Ignored, text: "public class Ignored { public const int FormatVersion = 1; }");
        checkout.Write(name: Generated, text: "public class Generated { public const int FormatVersion = 2; }");
        checkout.Write(name: "src/Puck.Demo/Upper.G.cs", text: "public class Upper { public const int FormatVersion = 3; }");
        checkout.Write(name: "tests/Outside.cs", text: "public class Outside { public const int FormatVersion = 4; }");
        checkout.Write(name: "src/Notes.txt", text: "outside the source pathspec");
        AssertRefused(checkout, false, Ignored);

        checkout.Write(name: ".gitignore", text: $"/{Ignored}\n");
        AssertSuccess(check: false, checkout: checkout);
        AssertSuccess(check: true, checkout: checkout);
        var ledger = checkout.Read(name: FormatVersionsLedger.FileName);

        Assert.Contains(actualString: ledger, expectedSubstring: "DemoCodec.SchemaVersion");
        Assert.DoesNotContain(actualString: ledger, expectedSubstring: "Ignored");
        Assert.DoesNotContain(actualString: ledger, expectedSubstring: "Generated");
        Assert.DoesNotContain(actualString: ledger, expectedSubstring: "Upper");
        Assert.DoesNotContain(actualString: ledger, expectedSubstring: "Outside");

        _ = checkout.Git("add", "--", Generated);
        AssertSuccess(check: true, checkout: checkout);
    }
    [Fact]
    public void StagingTheUntrackedSourceAllowsRecordingItsFormat() {
        using var checkout = Checkout();
        const string Added = "src/Puck.Demo/Added.cs";

        checkout.Write(name: Added, text: "namespace Puck.Demo;\npublic class Added { public const int FormatVersion = 7; }");
        AssertRefused(checkout, false, Added);

        _ = checkout.Git("add", "--", Added);
        AssertSuccess(check: false, checkout: checkout);
        Assert.Contains(expectedSubstring: "Added.FormatVersion", actualString: checkout.Read(name: FormatVersionsLedger.FileName));
        AssertSuccess(check: true, checkout: checkout);
    }
}
