using Puck.Cli.Formats;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Format recording sees a new source dependency before staging, excluding ignored and generated files.</summary>
public sealed class FormatsSourceDiscoveryLawTests {
    [Fact]
    public void NewSourceDependenciesHaveTheSameRecordedShapeBeforeAndAfterStaging() {
        using var checkout = new GitScratchCheckout();
        const string Document = "src/Puck.Demo/Document.cs";
        const string Mode = "src/Puck.Demo/Mode.cs";

        checkout.Write(name: ".gitignore", text: "**/obj/\n");
        checkout.Write(name: Document, text: "public sealed record Document(Mode Mode) { public const string Schema = \"puck.demo.v1\"; }");
        _ = checkout.Git("add", "--", ".gitignore", Document);
        checkout.Write(name: Mode, text: "public enum Mode { Instant, Queue }");
        checkout.Write(name: "src/Puck.Demo/obj/Ignored.cs", text: "public sealed class Ignored { public const int Format = 7; }");
        checkout.Write(name: "src/Puck.Demo/Generated.g.cs", text: "public sealed class Generated { public const int Format = 8; }");
        var sources = FormatsCommand.ReadSources(repositoryRoot: checkout.Root);

        Assert.Equal(expected: [Document, Mode], actual: sources.Keys.Order(comparer: StringComparer.Ordinal));
        var unstaged = FormatVersionsLedger.Render(entries: FormatVersionsLedger.Discover(files: sources));

        _ = checkout.Git("add", "--", Mode);
        var staged = FormatVersionsLedger.Render(entries: FormatVersionsLedger.Discover(files: FormatsCommand.ReadSources(repositoryRoot: checkout.Root)));

        Assert.Equal(actual: staged, expected: unstaged);
        checkout.Write(name: Mode, text: "public enum Mode { Instant = 2, Queue }");
        var changed = FormatVersionsLedger.Render(entries: FormatVersionsLedger.Discover(files: FormatsCommand.ReadSources(repositoryRoot: checkout.Root)));

        Assert.NotEqual(actual: changed, expected: staged);
    }
}
