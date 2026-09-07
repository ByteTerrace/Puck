using System.Reflection;
using System.Text.Json.Nodes;

namespace Puck.World;

public static partial class WorldSchema {
    // The commit a checked-in schema was generated at cannot equal the commit that FIRST introduces the file (the
    // commit embedding the regenerated file's own hash does not exist yet when the file is written), so `--check`
    // masks this field before comparing rather than pinning a value no commit could ever satisfy.
    private static void ApplySelfIdentification(JsonObject root, string schemaVersion) {
        var identity = new JsonObject {
            ["schemaVersion"] = schemaVersion,
            ["generator"] = $"{nameof(Puck)}.{nameof(World)}.{nameof(WorldSchema)}",
            ["commit"] = ResolveCommit(),
        };

        // Reinserted right after "$id" (the identity block reads together) rather than appended, so a person
        // opening the file sees what generated it before the document shape begins.
        var existing = root.ToList();

        root.Clear();

        foreach (var (key, value) in existing) {
            root.Add(propertyName: key, value: value);

            if (string.Equals(a: key, b: "$id", comparisonType: StringComparison.Ordinal)) {
                root.Add(propertyName: "x-puck", value: identity);
            }
        }

        if (root["properties"]?["schema"] is JsonObject schemaNode) {
            schemaNode["const"] = schemaVersion;
        }
    }
    // The SDK's own git integration appends "+<revision>" to AssemblyInformationalVersion when the build tree sits
    // inside a git repository; no explicit SourceLink package reference is needed for this suffix to appear.
    private static string ResolveCommit() {
        var informational = typeof(WorldSchema).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var plusIndex = (informational?.IndexOf(value: '+') ?? -1);

        return ((plusIndex >= 0)
            ? informational![(plusIndex + 1)..]
            : "unknown"
        );
    }
}
