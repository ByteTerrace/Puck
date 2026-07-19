using System.Text.Json;

namespace Puck.World;

/// <summary>The loaded world document plus its on-disk origin — the DI record <c>world.save</c> reads to learn its
/// default target. <see cref="SourcePath"/> is <see langword="null"/> when the definition is the baked
/// <see cref="WorldDefinition.Default"/> (no file, or a rejected file), so a bare <c>world.save</c> knows it has no
/// target and must be given a path.</summary>
/// <param name="Definition">The active world definition.</param>
/// <param name="SourcePath">The file the definition loaded from, or <see langword="null"/> when baked/fallback.</param>
internal sealed record WorldDefinitionSource(WorldDefinition Definition, string? SourcePath);

/// <summary>
/// Resolves the world definition at boot: a <c>--world &lt;path&gt;</c> argument (or the checked-in
/// <c>Assets/worlds/default.world.json</c> beside the executable), loaded through <see cref="WorldJsonContext"/>,
/// schema-checked, and passed through <see cref="WorldDefinitionValidator"/>. ANY failure — missing file, parse error,
/// schema mismatch, or a validation error — falls back LOUDLY to the baked <see cref="WorldDefinition.Default"/>. Boot
/// always prints exactly one <c>[world] definition:</c> line naming the resolved path (or the baked-default reason).
/// </summary>
internal static class WorldDefinitionLoader {
    /// <summary>The default world file, resolved against <see cref="AppContext.BaseDirectory"/> when no
    /// <c>--world</c> path is supplied.</summary>
    public static readonly string DefaultRelativePath = Path.Combine(path1: "Assets", path2: "worlds", path3: "default.world.json");

    /// <summary>Loads the active world definition, honoring an optional explicit path and falling back loudly to the
    /// baked default on any failure. Prints the one-line boot origin to <see cref="Console.Error"/>.</summary>
    /// <param name="explicitPath">The <c>--world</c> path, or <see langword="null"/>/empty for the default file.</param>
    /// <returns>The active definition and its origin.</returns>
    public static WorldDefinitionSource Load(string? explicitPath) {
        var path = (string.IsNullOrWhiteSpace(value: explicitPath)
            ? Path.Combine(path1: AppContext.BaseDirectory, path2: DefaultRelativePath)
            : Path.GetFullPath(path: explicitPath));

        if (TryLoadFile(path: path, definition: out var loaded, reason: out var reason)) {
            Console.Error.WriteLine(value: $"[world] definition: {path}");

            return new WorldDefinitionSource(Definition: loaded, SourcePath: path);
        }

        Console.Error.WriteLine(value: $"[world] definition: baked default ({reason})");

        return new WorldDefinitionSource(Definition: WorldDefinition.Default, SourcePath: null);
    }

    /// <summary>Loads and validates a world document from a file — the public seam the runtime <c>world.load</c> verb
    /// reuses so it never reimplements the deserialize → coalesce → schema-check → validate path. Read → deserialize →
    /// coalesce absent optional sections → schema-check → validate. Any failure yields a one-line reason (line endings
    /// collapsed) and <see langword="false"/>; a broad catch is deliberate here — a load boundary with a safe fallback,
    /// mirroring WorldProfileRegistration's malformed-document posture.</summary>
    /// <param name="path">The file to load.</param>
    /// <param name="definition">The loaded definition on success; <see cref="WorldDefinition.Default"/> on failure.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the file loaded and validated.</returns>
    public static bool TryLoadFile(string path, out WorldDefinition definition, out string reason) {
        definition = WorldDefinition.Default;

        if (!File.Exists(path: path)) {
            reason = $"no file at {path}";

            return false;
        }

        try {
            var json = File.ReadAllText(path: path);
            var parsed = Normalize(definition: (JsonSerializer.Deserialize(json: json, jsonTypeInfo: WorldJsonContext.Default.WorldDefinition)
                ?? throw new InvalidOperationException(message: "document deserialized to null")));

            if (!string.Equals(a: parsed.Schema, b: WorldDefinition.SchemaVersion, comparisonType: StringComparison.Ordinal)) {
                reason = $"{path}: schema '{parsed.Schema ?? "(absent)"}' is not {WorldDefinition.SchemaVersion}";

                return false;
            }

            WorldDefinitionValidator.Validate(definition: parsed);
            definition = parsed;
            reason = "";

            return true;
        } catch (Exception exception) {
            reason = $"{path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }

    // Coalesce absent-in-JSON optional sections (source-gen skips a positional collection's initializer when the JSON
    // property is absent, leaving null — the run-document doctrine's trap) so the validator and downstream consumers
    // never dereference null. Required sections stay null → the validator reports them → loud fallback. A fully-authored
    // document (every section present, as the canonical writer always emits) is unchanged by this pass, so load→save
    // byte-identity holds.
    private static WorldDefinition Normalize(WorldDefinition definition) {
        var assignment = (definition.Assignment is { } authored
            ? new WorldKitAssignment(Policy: authored.Policy, Table: (authored.Table ?? []))
            : WorldKitAssignment.Hash);

        return (definition with {
            Addons = (definition.Addons ?? []),
            Assignment = assignment,
            BindingOverlays = (definition.BindingOverlays ?? []),
            Creations = (definition.Creations ?? []),
            Placements = (definition.Placements ?? []),
            Storage = (definition.Storage ?? WorldStorageDefaults.None),
            Authoring = (definition.Authoring ?? WorldAuthoringDefaults.Default),
        });
    }
}
