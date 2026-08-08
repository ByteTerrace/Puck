using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>The reserved-character kernel shared by every member of this repository's validated-identifier family
/// (<see cref="WorldSafeName"/>, <see cref="WorldCellName"/>): control characters and the fixed reserved set (quote,
/// angle brackets, pipe, colon, asterisk, question mark, and both slashes) — the strictest host's reserved
/// characters, a superset of every platform's, so a value that passes is storable everywhere regardless of which
/// host minted it (the same requirement <see cref="WorldOwnedWorldFileName"/>'s id↔file-name mapping always
/// carried). Neither family member escapes or collapses an offending character the way that mapping used to — each
/// REFUSES it, by name, at construction, which is what makes simply HOLDING either type a proof of safety rather
/// than a courtesy some caller remembered to check.</summary>
internal static class WorldIdentifierRules {
    /// <summary>The reserved-character set spelled out for a refusal sentence.</summary>
    public const string ReservedDescription = "control characters and of the reserved set (quote, angle brackets, pipe, colon, asterisk, question mark, and both slashes)";

    private static readonly SearchValues<char> ReservedCharacters = SearchValues.Create(values: "\"<>|:*?\\/");

    /// <summary>Validates the kernel rule: non-empty, free of control characters and the reserved set.</summary>
    /// <param name="candidate">The candidate string.</param>
    /// <param name="reason">Why it was refused, naming the offending character, or empty on success.</param>
    /// <returns><see langword="true"/> when the kernel rule is satisfied.</returns>
    public static bool TryValidateKernel(string? candidate, out string reason) {
        if (string.IsNullOrEmpty(value: candidate)) {
            reason = "is required";

            return false;
        }

        foreach (var character in candidate) {
            if (char.IsControl(c: character)) {
                reason = $"carries a control character (0x{(int)character:X2}) — use a value free of {ReservedDescription}";

                return false;
            }

            if (ReservedCharacters.Contains(value: character)) {
                reason = $"carries the reserved character '{character}' — use a value free of {ReservedDescription}";

                return false;
            }
        }

        reason = string.Empty;

        return true;
    }
}

/// <summary>
/// A validated world/owned-world id or process-local world-instance name — the type CANNOT hold a value that does
/// not survive <see cref="WorldOwnedWorldFileName"/>'s id↔file-name mapping, or that would navigate a directory
/// instead of naming one segment of it (a bare <c>"."</c> or <c>".."</c>). Construction refuses by name, naming the
/// offending character (or the navigation rule), so every door that used to hand-check
/// <c>WorldOwnedWorldFileName.IsSafe</c> plus its own copy of the <c>"."</c>/<c>".."</c> rule now just HOLDS this
/// type instead — the check happens exactly once, at the earliest door a candidate string crosses (a console verb
/// argument, or this document's own JSON parse), and every downstream consumer inherits the proof for free.
/// </summary>
public readonly record struct WorldSafeName {
    private WorldSafeName(string value) => Value = value;

    /// <summary>Gets the validated string.</summary>
    public string Value { get; }

    /// <summary>Parses a candidate, throwing when it is unsafe.</summary>
    /// <param name="candidate">The candidate string.</param>
    /// <returns>The validated name.</returns>
    /// <exception cref="FormatException">The candidate is unsafe.</exception>
    public static WorldSafeName Parse(string candidate) =>
        (TryParse(candidate: candidate, name: out var name, reason: out var reason) ? name : throw new FormatException(message: reason));

    /// <summary>Parses a candidate, refusing by name (naming the offending character, or the navigation rule) rather
    /// than throwing.</summary>
    /// <param name="candidate">The candidate string.</param>
    /// <param name="name">The validated name, on success.</param>
    /// <param name="reason">Why the candidate was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the candidate is safe.</returns>
    public static bool TryParse(string? candidate, out WorldSafeName name, out string reason) {
        name = default;

        if (!WorldIdentifierRules.TryValidateKernel(candidate: candidate, reason: out reason)) {
            return false;
        }

        if (string.Equals(a: candidate, b: ".", comparisonType: StringComparison.Ordinal) || string.Equals(a: candidate, b: "..", comparisonType: StringComparison.Ordinal)) {
            reason = "must not be '.' or '..'";

            return false;
        }

        name = new WorldSafeName(value: candidate!);

        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <summary>Reads as its validated string wherever a plain string is expected.</summary>
    public static implicit operator string(WorldSafeName name) => name.Value;
}

/// <summary>
/// A validated <c>state</c>-section row name or cell key — the base <see cref="WorldSafeName"/> rule PLUS no dot
/// ANYWHERE, which is what makes the <c>state.&lt;row&gt;.&lt;key&gt;</c> HUD binding grammar unambiguous BY
/// CONSTRUCTION: splitting a bound token on <c>'.'</c> can never mistake part of a row or cell name for a grammar
/// separator, because neither can hold one. The reserved slot key <see cref="WorldStateRow.SlotKey"/>
/// (<c>"$value"</c>) is unaffected — <c>'$'</c> is neither a reserved character nor a dot, so it is already a legal
/// <see cref="WorldCellName"/> like any other author-chosen key, exactly the one reserved exception the substrate
/// mints rather than authors.
/// </summary>
public readonly record struct WorldCellName {
    private WorldCellName(string value) => Value = value;

    /// <summary>Gets the validated string.</summary>
    public string Value { get; }

    /// <summary>Parses a candidate, throwing when it is unsafe.</summary>
    /// <param name="candidate">The candidate string.</param>
    /// <returns>The validated name.</returns>
    /// <exception cref="FormatException">The candidate is unsafe.</exception>
    public static WorldCellName Parse(string candidate) =>
        (TryParse(candidate: candidate, name: out var name, reason: out var reason) ? name : throw new FormatException(message: reason));

    /// <summary>Parses a candidate, refusing by name (naming the offending character, or the dot rule) rather than
    /// throwing.</summary>
    /// <param name="candidate">The candidate string.</param>
    /// <param name="name">The validated name, on success.</param>
    /// <param name="reason">Why the candidate was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the candidate is safe.</returns>
    public static bool TryParse(string? candidate, out WorldCellName name, out string reason) {
        name = default;

        if (!WorldIdentifierRules.TryValidateKernel(candidate: candidate, reason: out reason)) {
            return false;
        }

        if (candidate!.Contains(value: '.')) {
            reason = "carries a '.' — a state row/cell name must be free of dots so state.<row>.<key> parses unambiguously";

            return false;
        }

        name = new WorldCellName(value: candidate);

        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <summary>Reads as its validated string wherever a plain string is expected.</summary>
    public static implicit operator string(WorldCellName name) => name.Value;
}

/// <summary>Reads/writes <see cref="WorldSafeName"/> as its plain string — refusing on read, by name, exactly like
/// <see cref="WorldSafeName.TryParse"/>, so a document holding one can never carry an unsafe id.</summary>
internal sealed class WorldSafeNameJsonConverter : JsonConverter<WorldSafeName> {
    public override WorldSafeName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var token = ((reader.TokenType == JsonTokenType.String) ? reader.GetString() : null);

        return (WorldSafeName.TryParse(candidate: token, name: out var name, reason: out var reason)
            ? name
            : throw new JsonException(message: $"'{token}' {reason}."));
    }

    public override void Write(Utf8JsonWriter writer, WorldSafeName value, JsonSerializerOptions options) => writer.WriteStringValue(value: value.Value);
}

/// <summary>Reads/writes <see cref="WorldCellName"/> as its plain string — refusing on read, by name, exactly like
/// <see cref="WorldCellName.TryParse"/>, so a document holding one can never carry a dotted or unsafe row/cell
/// name.</summary>
internal sealed class WorldCellNameJsonConverter : JsonConverter<WorldCellName> {
    public override WorldCellName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var token = ((reader.TokenType == JsonTokenType.String) ? reader.GetString() : null);

        return (WorldCellName.TryParse(candidate: token, name: out var name, reason: out var reason)
            ? name
            : throw new JsonException(message: $"'{token}' {reason}."));
    }

    public override void Write(Utf8JsonWriter writer, WorldCellName value, JsonSerializerOptions options) => writer.WriteStringValue(value: value.Value);
}
