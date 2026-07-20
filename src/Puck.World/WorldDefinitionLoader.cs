namespace Puck.World;

/// <summary>The loaded world document plus its on-disk origin — the DI record <c>world.save</c> reads to learn its
/// default target. <see cref="SourcePath"/> is <see langword="null"/> when the definition is the baked
/// <see cref="WorldDefinition.Default"/> (only ever the no-<c>--world</c>, no-shipped-asset case), so a bare
/// <c>world.save</c> knows it has no target and must be given a path.</summary>
/// <param name="Definition">The active world definition.</param>
/// <param name="SourcePath">The file the definition loaded from, or <see langword="null"/> when baked.</param>
internal sealed record WorldDefinitionSource(WorldDefinition Definition, string? SourcePath);

/// <summary>
/// Resolves the world definition at boot: a <c>--world &lt;path&gt;</c> argument (or the checked-in
/// <c>Assets/worlds/default.world.json</c> beside the executable), loaded through <see cref="WorldJsonContext"/>,
/// schema-checked, and passed through <see cref="WorldDefinitionValidator"/>.
/// </summary>
/// <remarks>An EXPLICIT <c>--world</c> path is an assertion: absent, unreadable, or invalid, it fails the boot with a
/// named reason and a non-zero exit — a typo must never quietly run some other world. Only the implicit default path
/// falls back to the baked <see cref="WorldDefinition.Default"/> (the no-asset-shipped case), and it says so. Boot
/// prints exactly one <c>[world] definition:</c> line naming the resolved path or the baked-default reason.</remarks>
internal static class WorldDefinitionLoader {
    /// <summary>The default world file, resolved against <see cref="AppContext.BaseDirectory"/> when no
    /// <c>--world</c> path is supplied.</summary>
    public static readonly string DefaultRelativePath = Path.Combine(path1: "Assets", path2: "worlds", path3: "default.world.json");

    /// <summary>Resolves the active world definition. An explicit path that will not load is a boot failure; the
    /// implicit default path falls back loudly to the baked definition.</summary>
    /// <param name="explicitPath">The <c>--world</c> path, or <see langword="null"/>/empty for the default file.</param>
    /// <param name="source">The resolved definition and its origin, when this returns <see langword="true"/>.</param>
    /// <param name="failure">The one-line boot-failure message, or empty on success.</param>
    /// <returns><see langword="true"/> when the boot may proceed.</returns>
    public static bool TryResolve(string? explicitPath, out WorldDefinitionSource source, out string failure) {
        var explicitly = !string.IsNullOrWhiteSpace(value: explicitPath);
        var path = (explicitly ? Path.GetFullPath(path: explicitPath!) : Path.Combine(path1: AppContext.BaseDirectory, path2: DefaultRelativePath));

        if (TryLoadFile(path: path, definition: out var loaded, reason: out var reason)) {
            Console.Error.WriteLine(value: $"[world] definition: {path}");

            source = new WorldDefinitionSource(Definition: loaded, SourcePath: path);
            failure = string.Empty;

            return true;
        }

        if (explicitly) {
            source = new WorldDefinitionSource(Definition: WorldDefinition.Default, SourcePath: null);
            failure = $"[world] --world {reason}";

            return false;
        }

        Console.Error.WriteLine(value: $"[world] definition: baked default ({reason})");

        source = new WorldDefinitionSource(Definition: WorldDefinition.Default, SourcePath: null);
        failure = string.Empty;

        return true;
    }

    /// <summary>Loads and validates a world document from a file — the public seam the runtime <c>world.load</c> verb
    /// reuses so it never reimplements the deserialize → schema-check → validate path. Any failure yields a one-line
    /// reason (line endings collapsed) and <see langword="false"/>, and the three failure classes are named apart:
    /// an ABSENT file, an UNREADABLE file, and an INVALID document. An INCOMPLETE document — one missing a section the
    /// canonical writer emits — is invalid like any other; the validator names every missing section. A broad catch is
    /// deliberate: a load boundary must never throw out of <see cref="TryLoadFile"/>.</summary>
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

        string json;

        try {
            json = File.ReadAllText(path: path);
        } catch (Exception exception) {
            reason = $"cannot read {path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        try {
            if (!WorldJsonPayload.TryParse(json: json, info: WorldJsonContext.Default.WorldDefinition, value: out var parsed, error: out var parseError)) {
                reason = $"{path} is not a valid {WorldDefinition.SchemaVersion} document: {parseError}";

                return false;
            }

            if (!string.Equals(a: parsed.Schema, b: WorldDefinition.SchemaVersion, comparisonType: StringComparison.Ordinal)) {
                reason = $"{path} is not a valid {WorldDefinition.SchemaVersion} document: schema '{parsed.Schema ?? "(absent)"}' is not {WorldDefinition.SchemaVersion}";

                return false;
            }

            WorldDefinitionValidator.Validate(definition: parsed);
            definition = parsed;
            reason = "";

            return true;
        } catch (Exception exception) {
            reason = $"{path} is not a valid {WorldDefinition.SchemaVersion} document: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }
}
