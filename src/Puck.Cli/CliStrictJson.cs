using System.Text.Json;

namespace Puck.Cli;

// Shared strict-JSON reading primitives for the CLI's manifest/contract loaders (parity, canary): duplicate-member
// refusal, unknown-member refusal, and the typed required-member readers both loaders otherwise carried
// statement-for-statement. Each caller supplies its own refusal-exception factory (and, where the wording differs,
// its own vocabulary text) so it keeps raising its own private exception type and message while sharing the
// parse/validate mechanics.
internal static class CliStrictJson {
    public static JsonDocument ParseStrict(string path, int maxDepth, string duplicateDetail, Func<string, Exception> refusal) {
        var document = JsonDocument.Parse(
            utf8Json: File.ReadAllBytes(path: path),
            options: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = maxDepth }
        );

        if (TryFindDuplicateMember(element: document.RootElement, path: "$", duplicate: out var duplicate)) {
            document.Dispose();

            throw refusal($"duplicate JSON member '{duplicate}' is ambiguous; {duplicateDetail}");
        }

        return document;
    }
    public static JsonElement RequireObject(JsonElement element, string context, Func<string, Exception> refusal) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw refusal($"{context} must be an object.");
        }

        return element;
    }
    public static void RequireOnlyMembers(JsonElement element, string context, string unknownMemberDetail, Func<string, Exception> refusal, params string[] allowed) {
        var names = new HashSet<string>(collection: allowed, comparer: StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject()) {
            if (!names.Contains(item: property.Name)) {
                throw refusal($"{context} contains unknown member '{property.Name}'; {unknownMemberDetail}");
            }
        }
    }
    public static string ReadRequiredString(JsonElement element, string member, string context, Func<string, Exception> refusal) {
        if (!element.TryGetProperty(propertyName: member, value: out var value) || (value.ValueKind != JsonValueKind.String) || string.IsNullOrWhiteSpace(value: value.GetString())) {
            throw refusal($"{context} {member} is required and must be non-blank.");
        }

        return value.GetString()!;
    }
    public static JsonElement ReadRequiredArray(JsonElement element, string member, string context, Func<string, Exception> refusal) {
        if (!element.TryGetProperty(propertyName: member, value: out var value) || (value.ValueKind != JsonValueKind.Array)) {
            throw refusal($"{context} {member} is required and must be an array.");
        }

        return value;
    }
    public static JsonElement ReadRequiredObject(JsonElement element, string member, string context, Func<string, Exception> refusal) {
        if (!element.TryGetProperty(propertyName: member, value: out var value)) {
            throw refusal($"{context} {member} is required and must be an object.");
        }

        return RequireObject(context: $"{context} {member}", element: value, refusal: refusal);
    }
    public static int ReadRequiredInt32(JsonElement element, string member, string context, Func<string, Exception> refusal) {
        if (!element.TryGetProperty(propertyName: member, value: out var value) || (value.ValueKind != JsonValueKind.Number) || !value.TryGetInt32(value: out var result)) {
            throw refusal($"{context} {member} must be a finite in-range integer.");
        }

        return result;
    }
    public static double ReadRequiredFiniteNumber(JsonElement element, string member, string context, string descriptor, Func<string, Exception> refusal) {
        if (!element.TryGetProperty(propertyName: member, value: out var value) || (value.ValueKind != JsonValueKind.Number) || !value.TryGetDouble(value: out var result) || !double.IsFinite(d: result)) {
            throw refusal($"{context} {member} must be a {descriptor}.");
        }

        return result;
    }

    private static bool TryFindDuplicateMember(JsonElement element, string path, out string duplicate) {
        if (element.ValueKind == JsonValueKind.Object) {
            var names = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var property in element.EnumerateObject()) {
                if (!names.Add(item: property.Name)) {
                    duplicate = $"{path}.{property.Name}";

                    return true;
                }
                if (TryFindDuplicateMember(element: property.Value, path: $"{path}.{property.Name}", duplicate: out duplicate)) {
                    return true;
                }
            }
        } else if (element.ValueKind == JsonValueKind.Array) {
            var index = 0;

            foreach (var item in element.EnumerateArray()) {
                if (TryFindDuplicateMember(duplicate: out duplicate, element: item, path: $"{path}[{index}]")) {
                    return true;
                }

                index++;
            }
        }

        duplicate = string.Empty;

        return false;
    }
}
