using Puck.Cli.Formats;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: formats reads tracked and unignored new sources; staging does not change a shape.</summary>
public sealed class FormatsCommandLawTests {
    private const string Source = "namespace Puck.Demo;\npublic static class DemoCodec { public const string SchemaVersion = \"puck.demo.v1\"; }";
    private const string SourcePath = "src/Puck.Demo/DemoCodec.cs";

    private static GitScratchCheckout Checkout() {
        var checkout = new GitScratchCheckout();

        checkout.Write(name: SourcePath, text: Source);
        checkout.Write(name: "src/Puck.Demo/Puck.Demo.csproj", text: "<Project />");
        _ = checkout.Git("add", "--", SourcePath, "src/Puck.Demo/Puck.Demo.csproj");

        return checkout;
    }
    private static (int ExitCode, string Output, string Error) Run(GitScratchCheckout checkout, bool check) =>
        ConsoleCapture.RunSplit(run: () => FormatsCommand.Execute(repositoryRoot: checkout.Root, check: check));
    private static void AssertSuccess(GitScratchCheckout checkout, bool check) {
        var (exitCode, _, error) = Run(check: check, checkout: checkout);

        Assert.True(condition: (exitCode == CliExit.Success), userMessage: error);
        Assert.Empty(collection: error);
    }

    [Fact]
    public void UntrackedSourcesParticipateInRecordingAndCheckingWithoutStaging() {
        using var checkout = Checkout();

        AssertSuccess(check: false, checkout: checkout);
        checkout.Write(name: "src/Puck.Demo/Added.cs", text: "namespace Puck.Demo; public class Added { public const int FormatVersion = 7; }");
        var before = checkout.Read(name: FormatVersionsLedger.FileName);

        Assert.Equal(CliExit.Failed, Run(check: true, checkout: checkout).ExitCode);
        Assert.Equal(before, checkout.Read(name: FormatVersionsLedger.FileName));
        AssertSuccess(check: false, checkout: checkout);
        Assert.Contains("Added.FormatVersion", checkout.Read(name: FormatVersionsLedger.FileName));
        AssertSuccess(check: true, checkout: checkout);
    }
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
        AssertSuccess(check: false, checkout: checkout);

        _ = checkout.Git("add", "--", Added);
        AssertSuccess(check: false, checkout: checkout);
        Assert.Contains(expectedSubstring: "Added.FormatVersion", actualString: checkout.Read(name: FormatVersionsLedger.FileName));
        AssertSuccess(check: true, checkout: checkout);
    }
}
