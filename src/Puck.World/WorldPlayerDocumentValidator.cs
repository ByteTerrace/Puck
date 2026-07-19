using System.Globalization;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The one thick gate for a loaded <see cref="WorldPlayerDocument"/> — the player-scope peer of
/// <see cref="WorldDefinitionValidator"/>. It rejects a malformed catalog structurally (schema, non-negative revision,
/// unique non-empty ids and names, parseable colors, finite positive speeds) and gates every non-null binding section
/// through the existing <see cref="BindingProfile.Compile"/> validator — the binding validator is never reimplemented.
/// A load that fails falls back LOUDLY to the built-in default (the run-document doctrine: a loader never half-accepts).
/// </summary>
internal static class WorldPlayerDocumentValidator {
    /// <summary>Validates a document without throwing.</summary>
    /// <param name="document">The candidate document.</param>
    /// <param name="reason">The collapsed one-line failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the document is valid.</returns>
    public static bool TryValidate(WorldPlayerDocument document, out string reason) {
        try {
            Validate(document: document);
            reason = string.Empty;

            return true;
        } catch (InvalidOperationException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }
    }

    /// <summary>Validates a document, throwing <see cref="InvalidOperationException"/> with the collapsed error list on
    /// any structural or binding-compile failure.</summary>
    /// <param name="document">The candidate document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The document is invalid.</exception>
    public static void Validate(WorldPlayerDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var errors = new List<string>();

        if (!string.Equals(a: document.Schema, b: WorldPlayerDocument.SchemaVersion, comparisonType: StringComparison.Ordinal)) {
            errors.Add(item: $"schema '{document.Schema ?? "(absent)"}' is not '{WorldPlayerDocument.SchemaVersion}'.");
        }

        if (document.Revision < 1L) {
            errors.Add(item: $"revision {document.Revision} must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(value: document.UpdatedAtUtc) ||
            !DateTimeOffset.TryParse(input: document.UpdatedAtUtc, formatProvider: CultureInfo.InvariantCulture, styles: DateTimeStyles.RoundtripKind, result: out _)) {
            errors.Add(item: $"updatedAtUtc '{document.UpdatedAtUtc ?? "(absent)"}' is not an ISO-8601 timestamp.");
        }

        if (document.Profiles is not { Count: > 0 } profiles) {
            errors.Add(item: "profiles requires at least one entry.");
        } else {
            var ids = new HashSet<string>(comparer: StringComparer.Ordinal);
            var names = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

            for (var index = 0; (index < profiles.Count); index++) {
                ValidateProfile(profile: profiles[index], path: $"profiles[{index}]", ids: ids, names: names, errors: errors);
            }
        }

        if (errors.Count > 0) {
            throw new InvalidOperationException(message: $"Invalid WorldPlayerDocument:{Environment.NewLine} - {string.Join(separator: $"{Environment.NewLine} - ", values: errors)}");
        }
    }

    private static void ValidateProfile(WorldPlayerProfile profile, string path, HashSet<string> ids, HashSet<string> names, List<string> errors) {
        if (profile is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        if (string.IsNullOrWhiteSpace(value: profile.Id)) {
            errors.Add(item: $"{path}.id is required.");
        } else if (!ids.Add(item: profile.Id)) {
            errors.Add(item: $"{path}.id '{profile.Id}' is duplicated.");
        } else if (profile.Id.IndexOfAny(anyOf: Path.GetInvalidFileNameChars()) >= 0) {
            // The id is the per-profile blob's key segment (world/profiles/<id>.json) — it must be filename-safe
            // so it addresses one blob and never escapes the container.
            errors.Add(item: $"{path}.id '{profile.Id}' contains characters invalid for a profile blob key.");
        }

        if (profile.Identity is null) {
            errors.Add(item: $"{path}.identity is required.");
        } else {
            if (string.IsNullOrWhiteSpace(value: profile.Identity.Name)) {
                errors.Add(item: $"{path}.identity.name is required.");
            } else if (!names.Add(item: profile.Identity.Name)) {
                errors.Add(item: $"{path}.identity.name '{profile.Identity.Name}' is duplicated (case-insensitive).");
            }

            if (!IsHexColor(value: profile.Identity.Color)) {
                errors.Add(item: $"{path}.identity.color '{profile.Identity.Color ?? "(absent)"}' is not a #RRGGBB hex color.");
            }
        }

        if (profile.Motion is null) {
            errors.Add(item: $"{path}.motion is required.");
        } else {
            if (!float.IsFinite(f: profile.Motion.MoveSpeed) || (profile.Motion.MoveSpeed <= 0f)) {
                errors.Add(item: $"{path}.motion.moveSpeed {profile.Motion.MoveSpeed} must be finite and positive.");
            }

            if (!float.IsFinite(f: profile.Motion.TurnSpeed) || (profile.Motion.TurnSpeed <= 0f)) {
                errors.Add(item: $"{path}.motion.turnSpeed {profile.Motion.TurnSpeed} must be finite and positive.");
            }
        }

        // A present binding section gates through the existing binding compiler — composed with the engine default so a
        // partial profile page that only makes sense post-merge still validates against the real runtime artifact.
        if (profile.Bindings is { } bindings) {
            try {
                _ = BindingProfile.Compile(document: WorldBindingComposer.Compose(WorldDefaultBindings.BuildDocument(), bindings));
            } catch (ArgumentException exception) {
                errors.Add(item: $"{path}.bindings does not compile: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
            }
        }
    }

    // A #RRGGBB (or bare RRGGBB) hex color — the persisted convention. Must accept exactly the hex forms
    // WorldProfile.ParseColor accepts, so a value that validates always parses (keep the two in sync).
    private static bool IsHexColor(string? value) {
        var span = (value ?? string.Empty).AsSpan().Trim();

        if ((span.Length > 0) && (span[0] == '#')) {
            span = span[1..];
        }

        return (span.Length == 6) &&
            byte.TryParse(s: span[..2], style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out _) &&
            byte.TryParse(s: span[2..4], style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out _) &&
            byte.TryParse(s: span[4..6], style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out _);
    }
}
