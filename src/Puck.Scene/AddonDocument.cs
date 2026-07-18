using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// One WASM addon the sim-tick host instantiates and drives with a fixed-point snapshot each tick, folding its
/// returned virtual-pad commands into a roster slot's input. Module identity is a machine-local path (mirroring
/// <see cref="GamingBrickSource.RomPath"/>); an optional content-address pin lets an author demand an exact byte
/// match. Filesystem-free at parse time — existence and hash verification are the run path's job, not the
/// validator's (mirroring the <c>romPath</c> precedent).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddonDocument {
    /// <summary>The addon's identifying name — unique within the document; used by console verbs and logging.</summary>
    public string Name { get; init; } = "";
    /// <summary>The module file path (machine-local; parse validation does not check existence — the run path does).</summary>
    public string? ModulePath { get; init; }
    /// <summary>The optional content-address integrity pin (canonical <c>sha256-64/{hex}</c> form, see
    /// <c>AssetContentHash.ToString</c>); null skips the check.</summary>
    public string? ModuleHash { get; init; }
    /// <summary>The roster slot the addon drives exclusively; null means the first free non-human slot.</summary>
    public int? Slot { get; init; }
    /// <summary>The per-tick fuel budget before a deterministic halt; null uses the host default.</summary>
    public long? FuelPerTick { get; init; }
    /// <summary>Whether the addon starts enabled; null (the default, and what an omitted value means) is enabled.</summary>
    public bool? Enabled { get; init; }

    internal void Validate(string path, ValidationErrors errors) {
        if (string.IsNullOrWhiteSpace(value: Name)) {
            errors.Add(path: $"{path}.name", message: "name must be non-empty");
        }

        if (ModulePath is null) {
            errors.Add(path: path, message: "an addon requires modulePath");
        } else if (string.IsNullOrWhiteSpace(value: ModulePath)) {
            errors.Add(path: $"{path}.modulePath", message: "modulePath, when present, must name a module");
        }

        if ((Slot is not null) && (Slot < 0)) {
            errors.Add(path: $"{path}.slot", message: "slot, when present, must be >= 0");
        }

        if ((FuelPerTick is not null) && (FuelPerTick <= 0)) {
            errors.Add(path: $"{path}.fuelPerTick", message: "fuelPerTick, when present, must be positive");
        }

        if ((ModuleHash is not null) && !IsValidModuleHash(hash: ModuleHash)) {
            errors.Add(path: $"{path}.moduleHash", message: "moduleHash, when present, must match sha256-64/{16 hex}");
        }
    }

    // Hand-rolled rather than System.Text.Regex — mirrors BrickExitCondition.TryParseAddress (GamingBrickSource.cs),
    // the codebase's precedent for validating a small fixed-shape string: a literal prefix check plus a length check
    // plus a per-character loop, not a compiled pattern. The canonical form is a "sha256-64/" prefix followed by
    // exactly 16 lowercase hex digits (what AssetContentHash.ToString's "x16" format emits).
    private static bool IsValidModuleHash(string hash) {
        const string prefix = "sha256-64/";

        if (!hash.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal) || (hash.Length != (prefix.Length + 16))) {
            return false;
        }

        for (var index = prefix.Length; (index < hash.Length); index++) {
            if (!char.IsAsciiHexDigitLower(c: hash[index])) {
                return false;
            }
        }

        return true;
    }
}
