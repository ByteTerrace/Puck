using System.Text.Json.Nodes;
using System.Text;
using Puck.Testing;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Source saves change the authored row while retaining unrelated source and refusing expansion-owned rows.</summary>
public sealed class SourceSaveLawTests {
    private const string Source = (WorldSources.Header + """
        // Keep this heading and the spacing beneath it.
        let distance = 2

        template marker(id) {
            placement id { prototype: "post" }
        }

        placements {
          // The other row keeps its expression and this comment.
          placement "still" { prototype: "post" position [distance, 0, 0] yawDegrees: 0 scale: 1 }

          placement "moved" { prototype: "post" position [0, 0, 0] yawDegrees: 0 scale: 1 }
        }
        // Keep this tail.

        """);

    [Fact]
    public void ChangedPlacementRoundTripsAndEveryOtherByteRemains() {
        var compiled = WorldCompiler.Compile(source: Source, cancellationToken: TestContext.Current.CancellationToken);
        var target = compiled.RequireJson().DeepClone().AsObject();

        target["placements"]!["rows"]![1]!["position"] = new JsonArray(4, 5, 6);

        Assert.True(condition: WorldSourceEdits.TryRewrite(source: Source, target: target, rewritten: out var saved, reason: out var reason), userMessage: reason);
        Assert.True(condition: JsonNode.DeepEquals(node1: target, node2: WorldCompiler.Compile(source: saved, cancellationToken: TestContext.Current.CancellationToken).RequireJson()));
        var origin = compiled.SourceMap.Snapshot()["/placements/rows/1"];

        Assert.StartsWith(expectedStartString: Source[..origin.Span.Offset], actualString: saved);
        Assert.EndsWith(expectedEndString: Source[(origin.Span.Offset + origin.Span.Length)..], actualString: saved);
    }
    [InlineData("for i in range(0, 1) { placement $\"loop-{i}\" { prototype: \"post\" } }", "compile-time for", "loop-0")]
    [InlineData("marker(id: \"templated\")", "template 'marker'", "templated")]
    [Theory]
    public void GeneratedPlacementRefusesByConstructAndName(string generated, string construct, string name) {
        var source = (((WorldSources.Header + "template marker(id) { placement id { prototype: \"post\" } }\nplacements { ") + generated) + " }\n");
        var target = WorldCompiler.Compile(source: source, cancellationToken: TestContext.Current.CancellationToken).RequireJson().DeepClone().AsObject();

        target["placements"]!["rows"]![0]!["position"] = new JsonArray(1, 0, 0);

        Assert.False(condition: WorldSourceEdits.TryRewrite(source: source, target: target, rewritten: out var saved, reason: out var reason));
        Assert.Equal(actual: saved, expected: source);
        Assert.Contains(actualString: reason, expectedSubstring: construct);
        Assert.Contains(actualString: reason, expectedSubstring: name);
        Assert.Contains(actualString: reason, expectedSubstring: "JSON delta");
    }
    [Fact]
    public void DuplicateRequiresAnAuthoredNameBeforeSave() {
        var target = WorldCompiler.Compile(source: Source, cancellationToken: TestContext.Current.CancellationToken).RequireJson().DeepClone().AsObject();
        var rows = target["placements"]!["rows"]!.AsArray();
        var duplicate = rows[1]!.DeepClone();

        duplicate["id"] = "moved$duplicate";
        rows.Add(item: duplicate);

        Assert.False(condition: WorldSourceEdits.TryRewrite(source: Source, target: target, rewritten: out var unchanged, reason: out var reason));
        Assert.Equal(actual: unchanged, expected: Source);
        Assert.Contains(actualString: reason, expectedSubstring: "moved$duplicate");
        Assert.Contains(actualString: reason, expectedSubstring: "rename");

        duplicate["id"] = "copy";
        Assert.True(condition: WorldSourceEdits.TryRewrite(source: Source, target: target, rewritten: out var saved, reason: out reason), userMessage: reason);
        Assert.Contains(actualString: saved, expectedSubstring: "placement \"still\" { prototype: \"post\" position [distance, 0, 0] yawDegrees: 0 scale: 1 }");
        Assert.True(condition: JsonNode.DeepEquals(node1: target, node2: WorldCompiler.Compile(source: saved, cancellationToken: TestContext.Current.CancellationToken).RequireJson()));
    }
    [Fact]
    public void RemovingOneRowRetainsTheSurvivorsOriginalExpression() {
        var target = WorldCompiler.Compile(source: Source, cancellationToken: TestContext.Current.CancellationToken).RequireJson().DeepClone().AsObject();

        target["placements"]!["rows"]!.AsArray().RemoveAt(index: 1);

        Assert.True(condition: WorldSourceEdits.TryRewrite(source: Source, target: target, rewritten: out var saved, reason: out var reason), userMessage: reason);
        Assert.Contains(actualString: saved, expectedSubstring: "position [distance, 0, 0]");
        Assert.Contains(actualString: saved, expectedSubstring: "// Keep this tail.");
        Assert.True(condition: JsonNode.DeepEquals(node1: target, node2: WorldCompiler.Compile(source: saved, cancellationToken: TestContext.Current.CancellationToken).RequireJson()));
    }
    [InlineData("marker(id: \"templated\")")]
    [InlineData("for i in range(0, 2) { placement $\"loop-{i}\" { prototype: \"post\" } }")]
    [Theory]
    public void RemovingAnAuthoredRowDoesNotRewriteUntouchedGeneratedRows(string generated) {
        var source = (((WorldSources.Header + "template marker(id) { placement id { prototype: \"post\" } }\nplacements {\nplacement \"authored\" { prototype: \"post\" }\n") + generated) + "\n}\n");
        var compiled = WorldCompiler.Compile(source: source, cancellationToken: TestContext.Current.CancellationToken);
        var target = compiled.RequireJson().DeepClone().AsObject();

        target["placements"]!["rows"]!.AsArray().RemoveAt(index: 0);
        Assert.True(condition: WorldSourceEdits.TryRewrite(source: source, target: target, rewritten: out var saved, reason: out var reason), userMessage: reason);
        var span = compiled.SourceMap.Snapshot()["/placements/rows/0"].Span;

        Assert.Equal(expected: source.Remove(startIndex: span.Offset, count: span.Length), actual: saved);
        Assert.True(condition: JsonNode.DeepEquals(node1: target, node2: WorldCompiler.Compile(source: saved, cancellationToken: TestContext.Current.CancellationToken).RequireJson()));
    }
    [Fact]
    public void NewSectionRetainsTheExistingSourceExactly() {
        var source = (WorldSources.Header + "// An unrelated constant.\nlet retained = 3\n");
        var target = WorldCompiler.Compile(source: source, cancellationToken: TestContext.Current.CancellationToken).RequireJson().DeepClone().AsObject();

        target["state"] = WorldSources.LowerClean(body: "state { world { slot score = 1 } }")["state"]!.DeepClone();

        Assert.True(condition: WorldSourceEdits.TryRewrite(source: source, target: target, rewritten: out var saved, reason: out var reason), userMessage: reason);
        Assert.StartsWith(actualString: saved, expectedStartString: source);
        Assert.True(condition: JsonNode.DeepEquals(node1: target, node2: WorldCompiler.Compile(source: saved, cancellationToken: TestContext.Current.CancellationToken).RequireJson()));
    }
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void SavedSnapshotRecomposesWithoutCanonicalizingOtherRows(bool basis, bool bom) {
        using var files = new TemporaryDirectory();

        _ = files.WriteText(name: "base.puck", text: (WorldSources.Header + "state { world { slot inherited = 1 } }\n"));
        var source = (basis ? Source.Replace(comparisonType: StringComparison.Ordinal, newValue: (WorldSources.Header + "basis: \"base\"\n"), oldValue: WorldSources.Header) : Source);
        var sourceBytes = Encoding.UTF8.GetBytes(s: source);
        var path = files.WriteBytes(name: "world.puck", bytes: (bom ? [.. Encoding.UTF8.Preamble, .. sourceBytes] : sourceBytes));
        var compiled = WorldCompiler.Compile(source: source, sourcePath: path, cancellationToken: TestContext.Current.CancellationToken);
        var target = compiled.RequireJson().DeepClone().AsObject();

        target["placements"]!["rows"]![1]!["position"] = new JsonArray(7, 8, 9);
        Assert.True(condition: WorldSourceLoader.TryReadAuthored(path: path, document: Encoding.UTF8.GetBytes(s: target.ToJsonString()), authored: out var live, reason: out var reason), userMessage: reason);

        Assert.True(condition: WorldSourceSave.TrySave(definition: live!, path: path, bytesWritten: out var written, reason: out reason), userMessage: reason);

        Assert.Equal(expected: new FileInfo(fileName: path).Length, actual: written);
        Assert.Equal(expected: bom, actual: File.ReadAllBytes(path: path).AsSpan().StartsWith(value: Encoding.UTF8.Preamble));
        var saved = File.ReadAllText(path: path);
        var origin = compiled.SourceMap.Snapshot()["/placements/rows/1"].Span;

        Assert.StartsWith(expectedStartString: source[..origin.Offset], actualString: saved);
        Assert.EndsWith(expectedEndString: source[(origin.Offset + origin.Length)..], actualString: saved);
        var recompiled = WorldCompiler.Compile(source: saved, sourcePath: path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: WorldSourceLoader.TryReadAuthored(path: path, document: Encoding.UTF8.GetBytes(s: recompiled.RequireJson().ToJsonString()), authored: out var reloaded, reason: out reason), userMessage: reason);
        Assert.Equal(expected: WorldDefinitionSerialization.Serialize(definition: live!), actual: WorldDefinitionSerialization.Serialize(definition: reloaded!));
    }
    [InlineData(0, "0.2", "0.3")]
    [InlineData(1, "0.8", "0.9")]
    [Theory]
    public void Saving_an_appended_array_leaf_keeps_both_authored_property_spans(int row, string before, string after) {
        var source = (WorldSources.Header + """
            views {
              // First authored array stays separate.
              post [ { name: a, package: "sdf.film-grain", parameters { strength: 0.2 } } ]
              // The appended row owns a different nested value span.
              post [ { name: b, package: "sdf.film-grain", parameters { strength: 0.8 } } ]
            }
            """);
        var target = WorldCompiler.Compile(source: source, cancellationToken: TestContext.Current.CancellationToken).RequireJson().DeepClone().AsObject();

        target["views"]!["post"]![row]!["parameters"]!["strength"] = double.Parse(after, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(condition: WorldSourceEdits.TryRewrite(source: source, target: target, rewritten: out var saved, reason: out var reason), userMessage: reason);
        Assert.Equal(source.Replace(comparisonType: StringComparison.Ordinal, newValue: after, oldValue: before), saved);
    }
}
