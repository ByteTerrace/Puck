using System.Text.Json.Nodes;
using Puck.Hosting;
using Puck.Testing;
using Xunit;

namespace Puck.World.Games.Tests;

/// <summary>
/// THE LAW: a world document survives <c>puck decompile</c> and <c>puck compile</c>. The corpus is every
/// <c>.world.json</c> the repository tracks with no tracked <c>.puck</c> source beside it (the documents a migration to
/// source starts from). The members half of the law (<c>WorldDecompileRoundTripLawTests.EmptyMembers.cs</c>) holds
/// every empty list and object, and every section held null, that the generated world schema declares.
/// </summary>
public sealed partial class WorldDecompileRoundTripLawTests {
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
}
