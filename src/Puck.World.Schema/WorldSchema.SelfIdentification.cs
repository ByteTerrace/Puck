using System.Text.Json.Nodes;

namespace Puck.World;

public static partial class WorldSchema {
    // A checked-in file carries no commit: the commit it is generated at cannot equal the one that first checks it
    // in (that commit does not exist while the file is being written), so a stamped file could never match its own
    // regeneration. The bundle, which nothing checks in, carries the commit (see StampBundleCommit).
    private static void ApplySelfIdentification(JsonObject root, string schemaVersion) {
        var identity = new JsonObject {
            ["schemaVersion"] = schemaVersion,
            ["generator"] = $"{nameof(Puck)}.{nameof(World)}.{nameof(WorldSchema)}",
        };

        // Reinserted right after "$id" (the identity block reads together) rather than appended, so a person
        // opening the file sees what generated it before the document shape begins.
        var existing = root.ToList();

        root.Clear();

        foreach (var (key, value) in existing) {
            root.Add(
                propertyName: key,
                value: value
            );

            if (string.Equals(
                a: key,
                b: "$id",
                comparisonType: StringComparison.Ordinal
            )) {
                root.Add(
                    propertyName: "x-puck",
                    value: identity
                );
            }
        }

        if (root["properties"]?["schema"] is JsonObject schemaNode) {
            schemaNode["const"] = schemaVersion;
        }
    }
    /// <summary>Stamps the bundle's <c>x-puck.commit</c> with the commit this generator was built at.</summary>
    private static void StampBundleCommit(JsonObject root) {
        if (root["x-puck"] is JsonObject identity) {
            identity["commit"] = ResolveCommit();
        }
    }
    private static string ResolveCommit() => (SourceRevision ?? "unknown");
    // The project embeds the commit as its own resource (see Puck.World.Schema.csproj); no assembly carries it in its
    // informational version, so a new commit recompiles this project alone.
    private static string? ReadSourceRevision() {
        using var stream = typeof(WorldSchema).Assembly.GetManifestResourceStream(name: "Puck.World.Schema.SourceRevision");

        if (stream is null) {
            return null;
        }

        using var reader = new StreamReader(stream: stream);
        var revision = reader.ReadToEnd().Trim();

        return ((revision.Length == 0)
            ? null
            : revision
        );
    }

    /// <summary>Gets the commit this build of the schema was made at, or <see langword="null"/> for a build outside a
    /// git tree.</summary>
    public static string? SourceRevision { get; } = ReadSourceRevision();
}
