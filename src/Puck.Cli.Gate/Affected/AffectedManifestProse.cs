using System.Text.Json;

namespace Puck.Cli.Affected;

/// <summary>Recognizes manifest edits that only change root prose; their strict load still runs.</summary>
public static class AffectedManifestProse {
    private const string Root = "tests/Puck.World.Canaries/";
    private const string Suffix = "/canary.json";

    /// <summary>Returns the canary directory for a manifest path, or null for another file.</summary>
    public static string? Id(string path) => ((path.StartsWith(comparisonType: StringComparison.Ordinal, value: Root) &&
        path.EndsWith(comparisonType: StringComparison.Ordinal, value: Suffix) &&
        (path.Length > (Root.Length + Suffix.Length)) &&
        (path[Root.Length..^Suffix.Length] is { Length: > 0 } id) && !id.Contains(value: '/')) ? id : null);
    /// <summary>Compares both manifests with only title and binding omitted. Every execution and verdict field
    /// remains, including nested names and text. Additions, deletions and malformed JSON keep ordinary selection.</summary>
    public static bool IsUnchanged(string path, IAffectedTree before, IAffectedTree after) {
        if ((Id(path: path) is null) || (before.ReadText(path: path) is not { } oldText) || (after.ReadText(path: path) is not { } newText)) {
            return false;
        }

        try {
            using var oldDocument = JsonDocument.Parse(json: oldText);
            using var newDocument = JsonDocument.Parse(json: newText);

            if ((oldDocument.RootElement.ValueKind != JsonValueKind.Object) || (newDocument.RootElement.ValueKind != JsonValueKind.Object)) {
                return false;
            }

            return JsonElement.DeepEquals(element1: WithoutProse(root: oldDocument.RootElement), element2: WithoutProse(root: newDocument.RootElement));
        } catch (JsonException) {
            return false;
        }
    }

    private static JsonElement WithoutProse(JsonElement root) {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(utf8Json: stream)) {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject()) {
                if (property.Name is not ("title" or "binding")) {
                    property.WriteTo(writer: writer);
                }
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(utf8Json: stream.ToArray());

        return document.RootElement.Clone();
    }
}
