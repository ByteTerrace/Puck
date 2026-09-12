namespace Puck.GamingBricks;

/// <summary>The common startup options understood by both GamingBrick screen engines.</summary>
/// <param name="Mode">Whether to execute firmware from reset or seed the cartridge handoff.</param>
/// <param name="ImagePath">An external firmware path, or null for the machine's bundled Puck firmware.</param>
public readonly record struct MachineBootOptions(MachineBootMode Mode, string? ImagePath) {
    /// <summary>Separates startup tokens from machine-specific options. The optional <c>bios=</c> path is last and
    /// consumes the rest of the string, allowing paths with spaces. Matching surrounding double quotes are removed.</summary>
    /// <param name="options">Space-separated machine options, optionally ending in <c>bios=&lt;path&gt;</c>.</param>
    /// <param name="machineTokens">Tokens other than <c>cold</c>, <c>fast</c>, and the final <c>bios=</c> selection.</param>
    /// <param name="defaults">Selections to retain when omitted, or null for cold startup with bundled firmware.</param>
    /// <returns>Cold startup with bundled firmware by default.</returns>
    /// <exception cref="ArgumentException">The image path is empty or startup modes conflict.</exception>
    public static MachineBootOptions Parse(string? options, out string[] machineTokens, MachineBootOptions? defaults = null) {
        var remaining = options?.Trim() ?? string.Empty;
        var path = defaults?.ImagePath;
        var imageStart = remaining.IndexOf(value: "bios=", comparisonType: StringComparison.OrdinalIgnoreCase);
        if ((imageStart >= 0) && ((imageStart == 0) || char.IsWhiteSpace(c: remaining[imageStart - 1]))) {
            path = remaining[(imageStart + 5)..].Trim();
            remaining = remaining[..imageStart];
            if ((path.Length >= 2) && (path[0] == '"') && (path[^1] == '"')) {
                path = path[1..^1];
            }
            if (string.IsNullOrWhiteSpace(value: path)) {
                throw new ArgumentException(message: "bios= requires an image path or puck.", paramName: nameof(options));
            }
            if (path.Equals(value: "puck", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                path = null;
            }
        }

        MachineBootMode? mode = null;
        var tokens = new List<string>();
        foreach (var token in remaining.Split(separator: (char[]?)null, options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            MachineBootMode? parsed = token.ToLowerInvariant() switch {
                "cold" => MachineBootMode.Cold,
                "fast" => MachineBootMode.Fast,
                _ => null,
            };
            if (parsed is null) {
                tokens.Add(item: token);
            } else if ((mode is not null) && (mode != parsed)) {
                throw new ArgumentException(message: "Choose cold or fast startup, not both.", paramName: nameof(options));
            } else {
                mode = parsed;
            }
        }

        machineTokens = [.. tokens];
        return new(Mode: mode ?? defaults?.Mode ?? MachineBootMode.Cold, ImagePath: path);
    }

    /// <summary>Formats startup options, keeping any external image path last.</summary>
    /// <returns>A canonical suffix with explicit startup mode and optional image path.</returns>
    public string Format() =>
        $"{(Mode == MachineBootMode.Fast ? "fast" : "cold")}{(ImagePath is null ? string.Empty : $" bios={ImagePath}")}";
}
