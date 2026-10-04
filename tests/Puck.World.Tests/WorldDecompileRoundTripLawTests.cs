using System.Text;
using System.Text.Json.Nodes;
using Puck.Hosting;
using Puck.Testing;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Composition;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a world document survives <c>puck decompile</c> and <c>puck compile</c>. Every <c>.world.json</c> the
/// repository tracks with no tracked <c>.puck</c> source beside it (the documents a migration to source starts from; the
/// sources themselves are held by <c>AuthorExpressionTests</c>) is decompiled to source, the source compiled, and the
/// result composed exactly as the original is, through the same basis and imports at the same path. The two composed
/// definitions serialize to the same bytes, and a layer that does not parse as a definition on its own (a module, a
/// shard overlay) composes to the same tree. A document the decompiler refuses by name is listed in
/// <see cref="NamedRefusals"/> with its reason, and a listed document that stops refusing fails the law as surely as a
/// new loss does. The members half of the law (<c>WorldDecompileRoundTripLawTests.EmptyMembers.cs</c>) holds every
/// empty list and object, and every section held null, that the generated world schema declares.
/// </summary>
public sealed partial class WorldDecompileRoundTripLawTests {
    // The documents the decompiler refuses by name, with the start of the refusal's message. A module instance's machine
    // has no source spelling (open item S6).
    private static readonly Dictionary<string, string> NamedRefusals = new(comparer: StringComparer.Ordinal) {
        ["tests/Puck.World.Canaries/four-corners-sharded/island.world.json"] = "'arcade$agb-screen' at /machines/0/name is a name Puck generates",
        ["tests/Puck.World.Canaries/four-corners-sharded/quilt-ne.world.json"] = "'arcade$agb-screen' at /machines/0/name is a name Puck generates",
        ["tests/Puck.World.Canaries/four-corners-sharded/quilt-nw.world.json"] = "'arcade$agb-screen' at /machines/0/name is a name Puck generates",
        ["tests/Puck.World.Canaries/four-corners-sharded/quilt-se.world.json"] = "'arcade$agb-screen' at /machines/0/name is a name Puck generates",
        ["tests/Puck.World.Canaries/four-corners-sharded/quilt-sw.world.json"] = "'arcade$agb-screen' at /machines/0/name is a name Puck generates",
    };

    private static string Excerpt(JsonNode? node) {
        var text = (node?.ToJsonString() ?? "absent");

        return ((text.Length > 60) ? text[..60] : text);
    }
    // The first place two trees differ, as a pointer and the two values, or null when they are equal.
    private static string? FirstDifference(JsonNode? expected, JsonNode? actual, string path) {
        if ((expected is null) && (actual is null)) {
            return null;
        }
        if ((expected is null) || (actual is null)) {
            return $"{path}: {Excerpt(node: expected)} became {Excerpt(node: actual)}";
        }
        if (expected.GetValueKind() != actual.GetValueKind()) {
            return $"{path}: {Excerpt(node: expected)} became {Excerpt(node: actual)}";
        }
        if ((expected is JsonObject expectedObject) && (actual is JsonObject actualObject)) {
            foreach (var (key, value) in expectedObject) {
                if (!actualObject.ContainsKey(propertyName: key)) {
                    return $"{path}/{key}: {Excerpt(node: value)} became absent";
                }
                if (FirstDifference(
                    actual: actualObject[key],
                    expected: value,
                    path: $"{path}/{key}"
                ) is { } nested) {
                    return nested;
                }
            }
            foreach (var (key, value) in actualObject) {
                if (!expectedObject.ContainsKey(propertyName: key)) {
                    return $"{path}/{key}: absent became {Excerpt(node: value)}";
                }
            }

            return null;
        }
        if ((expected is JsonArray expectedArray) && (actual is JsonArray actualArray)) {
            if (expectedArray.Count != actualArray.Count) {
                return $"{path}: {expectedArray.Count} items became {actualArray.Count}";
            }
            for (var index = 0; (index < expectedArray.Count); ++index) {
                if (FirstDifference(
                    actual: actualArray[index],
                    expected: expectedArray[index],
                    path: $"{path}[{index}]"
                ) is { } nested) {
                    return nested;
                }
            }

            return null;
        }

        // Two numbers are one value however they are spelled: a typed member reads `3` and `3.0` alike, and where the
        // spelling matters (an untyped member) the composed definition comparison holds it.
        if ((expected.GetValueKind() == System.Text.Json.JsonValueKind.Number) &&
            decimal.TryParse(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var expectedNumber,
                s: expected.ToJsonString()
            ) &&
            decimal.TryParse(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var actualNumber,
                s: actual.ToJsonString()
            )) {
            return ((expectedNumber == actualNumber) ? null : $"{path}: {Excerpt(node: expected)} became {Excerpt(node: actual)}");
        }

        return (string.Equals(
            a: expected.ToJsonString(),
            b: actual.ToJsonString(),
            comparisonType: StringComparison.Ordinal
        )
            ? null
            : $"{path}: {Excerpt(node: expected)} became {Excerpt(node: actual)}");
    }
    // Git defines the corpus, as it does for the source laws: scratch worlds and old published packages are not inputs.
    private static List<string> Documents(string root) {
        var files = new List<string>();
        var listing = ChildProcess.RunAsync(
            arguments: ["-C", root, "ls-files", "-z", "--", "*.puck", "*.world.json", ":!:experimental/"],
            cancellationToken: TestContext.Current.CancellationToken,
            fileName: "git",
            input: string.Empty,
            timeout: TestLiveness.Bound
        ).GetAwaiter().GetResult();

        Assert.True(condition: (listing.ExitCode == 0), userMessage: $"git ls-files failed: {listing.Stderr}");
        foreach (var relative in listing.Stdout.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0')) {
            files.Add(item: Path.Combine(path1: root, path2: relative).Replace(newChar: '/', oldChar: '\\'));
        }

        var sources = new HashSet<string>(
            collection: files.Where(predicate: static file => file.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".puck"
            )).Select(selector: static file => file[..^".puck".Length]),
            comparer: StringComparer.Ordinal
        );

        files.RemoveAll(match: file => (file.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: ".puck"
        ) || sources.Contains(item: file[..^".world.json".Length])));
        files.Sort(comparer: StringComparer.Ordinal);

        return files;
    }
    // A shape or placement row sugar defaults a member it leaves out, so a row that omits it and a row that states the
    // default are one row: both composed trees are held with those members dropped.
    private static void DropDefaulted(JsonObject document) {
        if (document["placements"]?["rows"] is JsonArray placements) {
            foreach (var row in placements.OfType<JsonObject>()) {
                foreach (var key in WorldDocumentRowDefaults.PlacementKeys) {
                    if (row.ContainsKey(propertyName: key) && WorldDocumentRowDefaults.IsDefaultValue(
                        index: 0,
                        key: key,
                        shape: false,
                        value: row[key]
                    )) {
                        _ = row.Remove(propertyName: key);
                    }
                }
            }
        }
        if (document["prototypes"] is JsonArray prototypes) {
            foreach (var prototype in prototypes.OfType<JsonObject>()) {
                if (prototype["document"]?["shapes"] is not JsonArray shapes) {
                    continue;
                }
                for (var index = 0; (index < shapes.Count); ++index) {
                    if (shapes[index] is not JsonObject shape) {
                        continue;
                    }
                    foreach (var key in WorldDocumentRowDefaults.ShapeKeys) {
                        if (shape.ContainsKey(propertyName: key) && WorldDocumentRowDefaults.IsDefaultValue(
                            index: index,
                            key: key,
                            shape: true,
                            value: shape[key]
                        )) {
                            _ = shape.Remove(propertyName: key);
                        }
                    }
                }
            }
        }
    }
    private static (bool Composed, JsonObject? Document, string Reason) Compose(string path, JsonObject root) {
        var composed = PuckDocumentComposer.TryComposeWorldDocument(
            catalog: TestHookInstaller.CreateMachineCatalog(),
            composed: out var document,
            chainBytes: out _,
            reason: out var reason,
            rootBytes: Encoding.UTF8.GetBytes(s: root.ToJsonString()),
            rootResolvedPath: path
        );

        // A root that names neither a basis nor an import composes to itself.
        document = ((document ?? root).DeepClone() as JsonObject)!;
        DropDefaulted(document: document);

        return (composed, document, reason);
    }
    // The verdict of one document: null when it round-trips, else what was lost.
    private static string? RoundTrip(string path, string relative, HashSet<string> refused, ref int checkedCount) {
        // A layer that declares no schema (a module, a shard overlay) is a world document too; one that declares another
        // schema is a different vocabulary's.
        if ((WorldDocumentCorpus.ReadObject(path: path) is not { } original) || (
            (original["schema"] is not null) && !string.Equals(
            a: original["schema"]!.ToString(),
            b: "puck.world.definition.v1",
            comparisonType: StringComparison.Ordinal
        ))) {
            return null;
        }

        ++checkedCount;

        string source;

        try {
            source = WorldDecompiler.Decompile(root: original);
        } catch (WorldDecompileRefusedException refusal) {
            return ((NamedRefusals.TryGetValue(
                key: relative,
                value: out var expected
            ) && refusal.Message.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: expected
            ) && refused.Add(item: relative))
                ? null
                : $"{relative}: the decompiler refuses it: {refusal.Message.Split(separator: '\n')[0]}");
        }

        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source,
            sourcePath: path
        );

        if (!compilation.Success || (compilation.Json is null)) {
            return $"{relative}: the printed source does not compile: {string.Join(separator: " | ", values: compilation.Diagnostics.Take(count: 2).Select(selector: static diagnostic => $"{diagnostic.Code}: {diagnostic.Message.Split(separator: '\n')[0]}"))}";
        }

        var before = Compose(
            path: path,
            root: original
        );
        var after = Compose(
            path: path,
            root: compilation.Json
        );

        if (before.Composed != after.Composed) {
            return $"{relative}: composes {before.Composed} before and {after.Composed} after: {(before.Composed ? after.Reason : before.Reason).Split(separator: '\n')[0]}";
        }
        if (!before.Composed) {
            // A layer that does not compose on its own (a fragment) is held to its document.
            return ((FirstDifference(
                actual: compilation.Json,
                expected: original,
                path: string.Empty
            ) is { } layerLoss)
                ? $"{relative}: the layer changed: {layerLoss}"
                : null);
        }

        var oldText = Serialized(
            document: before.Document!,
            reason: out var oldReason,
            relative: relative
        );
        var newText = Serialized(
            document: after.Document!,
            reason: out var newReason,
            relative: relative
        );

        if ((oldText is null) || (newText is null)) {
            return ((FirstDifference(
                actual: after.Document,
                expected: before.Document,
                path: string.Empty
            ) is { } composedLoss)
                ? $"{relative}: the composed document changed: {composedLoss}"
                : null);
        }

        return (string.Equals(
            a: oldText,
            b: newText,
            comparisonType: StringComparison.Ordinal
        )
            ? null
            : $"{relative}: the composed definition changed: {FirstDifference(
                actual: JsonNode.Parse(json: newText),
                expected: JsonNode.Parse(json: oldText),
                path: string.Empty
            )}");
    }
    private static string? Serialized(JsonObject document, string relative, out string reason) {
        if (!WorldDefinitionFileSource.TryParseDocument(
            definition: out var definition,
            json: document.ToJsonString(),
            reason: out reason,
            sourceName: relative
        )) {
            return null;
        }

        return Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: definition!));
    }

    [Fact]
    public void EveryWorldDocumentSurvivesDecompileAndCompile() {
        var root = RepositoryPaths.RequireRoot();
        var checkedCount = 0;
        var losses = new List<string>();
        var refused = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var path in Documents(root: root)) {
            var relative = Path.GetRelativePath(
                path: path,
                relativeTo: root
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            );

            if (RoundTrip(
                checkedCount: ref checkedCount,
                path: path,
                refused: refused,
                relative: relative
            ) is { } loss) {
                losses.Add(item: loss);
            }
        }

        foreach (var (relative, _) in NamedRefusals) {
            if (!refused.Contains(item: relative)) {
                losses.Add(item: $"{relative}: a listed refusal no longer refuses, so drop it from {nameof(NamedRefusals)}");
            }
        }

        Assert.True(
            condition: (checkedCount > 20),
            userMessage: $"only {checkedCount} JSON world documents were found"
        );
        Assert.True(
            condition: (losses.Count == 0),
            userMessage: $"{losses.Count} of {checkedCount} world document(s) do not survive decompile and compile:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: losses)}"
        );
    }
}
