using Puck.Cli.Docs;
using Puck.Testing;

using Xunit;

namespace Puck.Cli.Source.Tests;

/// <summary>Matcher laws for ownership, normalization, ambiguity and revision selection that the sample reports do not exercise.</summary>
public sealed class DocsCodeCitationLawTests {
    private sealed class Repository : IDisposable {
        private readonly TemporaryDirectory m_directory = new();
        public string Root => m_directory.RootPath;
        public Repository() {
            Git("init", "--quiet");
            Git("config", "maintenance.auto", "false");
            Git("config", "gc.auto", "0");
            Write("src/code.cs", "alpha beta\n" + string.Join('\n', Enumerable.Repeat("padding", 21)) + "\ntrue quote\n");
            Commit();
        }
        public void Write(string path, string text) => m_directory.WriteText(name: path, text: text);
        public void Commit() {
            Git("add", ".");
            Git("-c", "user.name=Citation laws", "-c", "user.email=laws@example.invalid", "commit", "--quiet", "-m", "fixture");
        }
        private void Git(params string[] arguments) {
            var result = CliGit.Run(Root, arguments);
            Assert.True(result.ExitCode == 0, result.Stderr);
        }
        public IReadOnlyList<DocsCodeCitations.Result> Check(string markdown) => new DocsCodeCitations(Root).Check(".claude/scratch/report.md", markdown.Split('\n'));
        public void Dispose() => m_directory.Dispose();
    }

    [Fact]
    public void WhitespaceRunsNormalizeButTokensCannotMerge() {
        using var repository = new Repository();
        var match = Assert.Single(repository.Check("src/code.cs:1\n```cs\n  alpha\t beta  \n```"));
        Assert.Equal("verified", match.Status);
        var missing = Assert.Single(repository.Check("src/code.cs:1\n```cs\nalphabeta\n```"));
        Assert.Equal("not found", missing.Status);
    }

    [Theory]
    [InlineData("0", "not found")]
    [InlineData("23", "verified")]
    [InlineData("24", "not found")]
    [InlineData("2-1", "not found")]
    [InlineData("1-9999999999999999999999999999999", "not found")]
    public void BoundsAreOneBasedAndInclusive(string range, string status) {
        using var repository = new Repository();
        Assert.Equal(status, Assert.Single(repository.Check($"src/code.cs:{range}")).Status);
    }

    [Fact]
    public void AQuoteElsewhereDriftsAndElisionIsNotAWildcard() {
        using var repository = new Repository();
        var drift = Assert.Single(repository.Check("src/code.cs:1\n```cs\ntrue quote\n```"));
        Assert.Equal("drifted", drift.Status);
        Assert.Contains("true line 23", drift.Detail);
        Assert.Equal("not found", Assert.Single(repository.Check("src/code.cs:1\n```cs\nalpha ... beta\n```")).Status);
        Assert.Equal("verified", Assert.Single(repository.Check("src/code.cs:18\n```cs\ntrue quote\n```")).Status);
    }

    [Fact]
    public void BulletsOwnIndentedFencesAcrossExplanationsButSiblingsDoNot() {
        using var repository = new Repository();
        var results = repository.Check("""
            - src/code.cs:1
              Explanation of this quote.

              ~~~cs
              invented quote
              ~~~
            - Another bullet.
              ```cs
              alpha beta
              ```
            ```text
            missing.cs:900
            ```
            """);
        Assert.Equal("not found", Assert.Single(results).Status);
        Assert.Equal("not found", Assert.Single(repository.Check("src/code.cs:1\n```cs\nalpha beta")).Status);
    }

    [Fact]
    public void LinksUseTheirFullTargetAndShorthandRetainsThePath() {
        using var repository = new Repository();
        var linked = repository.Check("- [`code.cs:1`](file:///old/worktree/src/code.cs#L1):\n  ```cs\n  alpha beta\n  ```");
        Assert.Equal("verified", Assert.Single(linked).Status);
        var shorthand = repository.Check("`code.cs:1`, `:23`, `:24`");
        Assert.Equal(["verified", "verified", "not found"], shorthand.Select(result => result.Status));
    }

    [Fact]
    public void HeadIsTheDefaultAndExplicitRevisionsReadTheNamedBlob() {
        using var repository = new Repository();
        repository.Write("src/code.cs", "new content\n");
        // An uncommitted edit cannot change the evidence at HEAD.
        Assert.Equal("verified", Assert.Single(repository.Check("src/code.cs:23")).Status);
        repository.Commit();
        Assert.Equal("not found", Assert.Single(repository.Check("src/code.cs:23")).Status);
        Assert.Equal("verified", Assert.Single(repository.Check("Citation revision: HEAD~1\nsrc/code.cs:23")).Status);
        Assert.Equal("verified", Assert.Single(repository.Check("src/code.cs@HEAD~1:23")).Status);
        Assert.Equal("not found", Assert.Single(repository.Check("src/code.cs@no-such-revision:1")).Status);
    }

    [Fact]
    public void ARepeatedQuoteUsesTheNearbyOccurrenceAndAmbiguousPathsFail() {
        using var repository = new Repository();
        repository.Write("src/other/code.cs", "alpha beta\n");
        repository.Write("src/repeated.cs", "true quote\n" + string.Join('\n', Enumerable.Repeat("padding", 21)) + "\ntrue quote\n");
        repository.Commit();
        Assert.Equal("not found", Assert.Single(repository.Check("code.cs:1")).Status);
        Assert.Equal("verified", Assert.Single(repository.Check("src/repeated.cs:23\n```cs\ntrue quote\n```")).Status);
    }
}
