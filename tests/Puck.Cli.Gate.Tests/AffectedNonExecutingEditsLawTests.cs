using Puck.Cli.Affected;
using Xunit;

namespace Puck.Cli.Gate.Tests;

public sealed class AffectedNonExecutingEditsLawTests {
    private const string Code = "class Value { int Read() => 1; }";
    private const string Json = """{"id":"example","title":"old","binding":"old","bootShape":"headless","requirements":[],"timeoutSeconds":10,"positive":{"expect":[{"name":"old","text":"old"}]},"discriminating":{}}""";
    private const string Manifest = "tests/Puck.World.Canaries/example/canary.json";
    private const string Source = "src/World/Value.cs";

    private static AffectedPlan Select(GitScratchCheckout checkout, string since, params string[] changed) {
        using var before = new AffectedRevisionTree(documentTrees: AffectedRevisionExport.DocumentTrees, root: checkout.Root, revision: since);
        var after = new AffectedWorkingTree(root: checkout.Root);

        return AffectedSelection.Select(
            changed: changed,
            projects: [new(Directory: "src/World", IsSuite: false, Name: "World", References: []), new(Directory: "tests/World.Tests", IsSuite: true, Name: "World.Tests", References: ["World"])],
            canaries: [new(Directory: "tests/Puck.World.Canaries/example", Files: [], Id: "example", RequiresGpu: true)],
            coverage: new Dictionary<string, IReadOnlySet<string>> { [Source] = new HashSet<string>(collection: ["example"]), [Manifest] = new HashSet<string>(collection: ["example"]) },
            consumersOf: _ => ["World"], worldClosure: new HashSet<string>(collection: ["World"]), declaresTests: _ => false,
            catalogInputs: (_, _) => true, standInsFor: _ => [], canariesReaching: _ => new HashSet<string>(collection: ["example"]), worldInput: _ => true,
            triviaOnly: path => AffectedCSharpTrivia.IsUnchanged(after: after, before: before, path: path),
            proseOnly: path => AffectedManifestProse.IsUnchanged(after: after, before: before, path: path));
    }
    [InlineData("// corrected comment\nclass Value { int Read() => 1; }")]
    [InlineData("/// <summary>Corrected documentation.</summary>\nclass Value { int Read() => 1; }")]
    [InlineData("class  Value\n{\n    int Read() => 1;\n}\n")]
    [InlineData("#region grouping\nclass Value { int Read() => 1; }\n#endregion")]
    [Theory]
    public void TriviaOnlyEditsSelectNoExecution(string after) {
        using var checkout = new GitScratchCheckout();

        checkout.Write(name: Source, text: Code);
        var since = checkout.Commit(message: "source");

        checkout.Write(name: Source, text: after);

        var plan = Select(checkout: checkout, since: since, Source);

        Assert.Empty(collection: plan.Suites);
        Assert.Empty(collection: plan.Canaries);
        Assert.Empty(collection: plan.CanaryChecks);
        Assert.Empty(collection: plan.Baselines);
        Assert.Empty(collection: plan.Worlds);
        Assert.Empty(collection: plan.Unmapped);
        Assert.Empty(collection: plan.Deleted);
        Assert.False(condition: plan.Catalog);
        Assert.False(condition: plan.Parity);
        Assert.False(condition: plan.Everything);
    }
    [Fact]
    public void UnchangedExecutableDirectivesStillAllowCommentEdits() {
        using var checkout = new GitScratchCheckout();

        checkout.Write(name: Source, text: ("#nullable enable\n" + Code));
        var since = checkout.Commit(message: "source");

        checkout.Write(name: Source, text: ("// clearer comment\n#nullable enable\n" + Code));
        var plan = Select(checkout: checkout, since: since, Source);

        Assert.Empty(collection: plan.Suites);
        Assert.Empty(collection: plan.Canaries);
    }
    [InlineData("// corrected comment\nclass Value { int Read() => 2; }")]
    [InlineData("/// <summary>Corrected documentation.</summary>\nclass Value { int Write() => 1; }")]
    [InlineData("class Value { int Read() => ; }")]
    [Theory]
    public void TriviaCannotHideATokenChangeOrParseError(string after) {
        using var checkout = new GitScratchCheckout();

        checkout.Write(name: Source, text: Code);
        var since = checkout.Commit(message: "source");

        checkout.Write(name: Source, text: after);

        var plan = Select(checkout: checkout, since: since, Source);

        Assert.Equal(expected: ["World.Tests"], actual: plan.Suites);
        Assert.Equal(expected: ["example"], actual: plan.Canaries);
        Assert.True(condition: plan.Parity);
    }
    [InlineData("#if SYMBOL\nclass Value { int Read() => 1; }\n#endif", "#if SYMBOL\nclass Value { int Read() => 2; }\n#endif")]
    [InlineData("#if SYMBOL\nclass Value {}\n#endif", "// comment\n#if SYMBOL\nclass Value {}\n#endif")]
    [InlineData("#nullable enable\nclass Value {}", "#nullable disable\nclass Value {}")]
    [InlineData("#pragma warning disable CS0169\nclass Value {}", "#pragma warning restore CS0169\nclass Value {}")]
    [InlineData("#line 10\nclass Value {}", "#line 20\nclass Value {}")]
    [InlineData(null, Code)]
    [InlineData(Code, null)]
    [Theory]
    public void ConditionalDirectivesChangedDirectivesAndMissingFilesStayUnjudged(string? before, string? after) {
        using var checkout = new GitScratchCheckout();

        checkout.Write(name: "README.md", text: "base");
        if (before is not null) { checkout.Write(name: Source, text: before); }
        var since = checkout.Commit(message: "source");

        if (after is not null) { checkout.Write(name: Source, text: after); } else { File.Delete(path: Path.Combine(path1: checkout.Root, path2: Source)); }

        var plan = Select(checkout: checkout, since: since, Source);

        Assert.Equal(expected: ["World.Tests"], actual: plan.Suites);
        Assert.Equal(expected: ["example"], actual: plan.Canaries);
    }
    [InlineData("title")]
    [InlineData("binding")]
    [Theory]
    public void RootProseSelectsOnlyItsStrictManifestCheck(string field) {
        using var checkout = new GitScratchCheckout();

        checkout.Write(name: Manifest, text: Json);
        var since = checkout.Commit(message: "manifest");

        checkout.Write(name: Manifest, text: Json.Replace(comparisonType: StringComparison.Ordinal, newValue: $"\"{field}\":\"new\"", oldValue: $"\"{field}\":\"old\""));

        var plan = Select(checkout: checkout, since: since, Manifest);

        Assert.Equal(expected: ["example"], actual: plan.CanaryChecks);
        Assert.Empty(collection: plan.Canaries);
        Assert.Empty(collection: plan.Suites);
        Assert.Empty(collection: plan.Baselines);
        Assert.False(condition: plan.Parity);
        Assert.False(condition: plan.Catalog);
    }
    [InlineData("\"timeoutSeconds\":10", "\"timeoutSeconds\":20")]
    [InlineData("\"bootShape\":\"headless\"", "\"bootShape\":\"windowed\"")]
    [InlineData("\"requirements\":[]", "\"requirements\":[\"gpu\"]")]
    [InlineData("\"id\":\"example\"", "\"id\":\"other\"")]
    [InlineData("\"name\":\"old\"", "\"name\":\"new\"")]
    [InlineData("\"text\":\"old\"", "\"text\":\"new\"")]
    [InlineData("\"discriminating\":{}", "\"discriminating\":{\"world\":\"other.world.json\"}")]
    [InlineData("\"positive\":", "\"backends\":[\"vulkan\"],\"positive\":")]
    [InlineData("\"positive\":", "\"fixtures\":[\"other.txt\"],\"positive\":")]
    [Theory]
    public void EveryExecutionAndVerdictFieldStillSelectsTheRun(string before, string after) {
        using var checkout = new GitScratchCheckout();

        checkout.Write(name: Manifest, text: Json);
        var since = checkout.Commit(message: "manifest");

        checkout.Write(name: Manifest, text: Json.Replace(comparisonType: StringComparison.Ordinal, newValue: after, oldValue: before));

        var plan = Select(checkout: checkout, since: since, Manifest);

        Assert.Equal(expected: ["example"], actual: plan.Canaries);
        Assert.Empty(collection: plan.CanaryChecks);
    }
    [InlineData(null, Json)]
    [InlineData(Json, null)]
    [InlineData(Json, "{")]
    [InlineData(Json, "[]")]
    [Theory]
    public void AddedDeletedAndMalformedManifestsStayUnjudged(string? before, string? after) {
        using var checkout = new GitScratchCheckout();

        checkout.Write(name: "README.md", text: "base");
        if (before is not null) { checkout.Write(name: Manifest, text: before); }
        var since = checkout.Commit(message: "manifest");

        if (after is not null) { checkout.Write(name: Manifest, text: after); } else { File.Delete(path: Path.Combine(path1: checkout.Root, path2: Manifest)); }
        var plan = Select(checkout: checkout, since: since, Manifest);

        Assert.Equal(expected: ["example"], actual: plan.Canaries);
        Assert.Empty(collection: plan.CanaryChecks);
    }
}
