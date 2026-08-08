using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// The law-link gate for <c>VerifiedCode.json</c>. <c>Puck.Analyzers</c> enforces that a brand's recorded fingerprint
/// stays honest about the CODE; it deliberately does not read the law suite (see the analyzer's own remarks), so
/// this test is the other half — every law id a manifest entry cites must still resolve in
/// <see cref="LawDeclarations.All"/>. A law can be renamed or retired long after a brand cites it; without this gate
/// the brand would keep citing a justification that no longer exists.
/// </summary>
public sealed class VerifiedCodeManifestTests {
    private static readonly JsonSerializerOptions Options = new() {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    [Trait(name: "tier", value: "Default")]
    public void EveryCitedLawIdResolves() {
        var manifestPath = Path.GetFullPath(path: Path.Combine(TestPaths.ProjectDirectory, "..", "..", "VerifiedCode.json"));
        var manifest = JsonSerializer.Deserialize<Manifest>(json: File.ReadAllText(path: manifestPath), options: Options)!;
        var declaredLawIds = LawDeclarations.All.Keys.ToHashSet(comparer: StringComparer.Ordinal);

        var unresolved = manifest.Entries
            .SelectMany(selector: pair => pair.Value.Laws.Where(predicate: lawId => !declaredLawIds.Contains(item: lawId)).Select(selector: lawId => $"{pair.Key} -> {lawId}"))
            .OrderBy(keySelector: text => text, comparer: StringComparer.Ordinal)
            .ToList();

        Assert.True(condition: (unresolved.Count == 0), userMessage: $"{unresolved.Count} VerifiedCode.json law citation(s) resolve to no LawDeclarations.All entry: {string.Join(separator: ", ", values: unresolved.Take(count: 20))}");
    }

    private sealed record Manifest([property: JsonPropertyName("format")] int Format, [property: JsonPropertyName("entries")] Dictionary<string, ManifestEntry> Entries);

    private sealed record ManifestEntry(
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("algorithm")] string Algorithm,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("laws")] string[] Laws);
}
