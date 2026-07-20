using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Puck.World;

/// <summary>
/// The one place author-supplied JSON text becomes a document row. Every console verb that takes an inline-JSON
/// argument — and every file-backed load that reads one — parses through here, so a malformed payload is NAMED and
/// REFUSED on the wire instead of escaping as a fault that kills the host.
/// </summary>
/// <remarks>
/// Polymorphic (<c>$type</c>) deserialization does NOT fail with <see cref="JsonException"/> alone. Observed against
/// this project's discriminated unions: an absent discriminator on an abstract union base throws
/// <see cref="NotSupportedException"/>; an unknown, duplicate, non-string, or misplaced discriminator, a type-mismatched
/// field, and truncated text all throw <see cref="JsonException"/>. A contract/resolver fault surfaces as
/// <see cref="InvalidOperationException"/>, and a null or unusable argument as <see cref="ArgumentException"/>.
/// <see cref="IsParseFailure(Exception)"/> is that whole set — catching narrower is the defect this type exists to
/// prevent.
/// </remarks>
internal static class WorldJsonPayload {
    /// <summary>The message used when a payload is absent.</summary>
    public const string EmptyPayload = "expected a compact inline-JSON argument";

    /// <summary>The message used when a payload parses to the literal <c>null</c> where a row is required.</summary>
    public const string NullPayload = "the JSON parsed to null";

    /// <summary>Reports whether an exception is a JSON payload rejection rather than a host fault — the complete set
    /// <see cref="JsonSerializer"/> raises for author-supplied text, polymorphic payloads included.</summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns><see langword="true"/> when the exception describes a bad payload.</returns>
    public static bool IsParseFailure(Exception exception) {
        return (exception is JsonException or NotSupportedException or InvalidOperationException or ArgumentException);
    }

    /// <summary>Renders a rejection reason naming both the failure and the offending payload.</summary>
    /// <param name="exception">The parse failure.</param>
    /// <param name="json">The payload that failed.</param>
    /// <returns>A single-line reason.</returns>
    public static string Describe(Exception exception, string json) {
        ArgumentNullException.ThrowIfNull(argument: exception);

        return $"{exception.Message.ReplaceLineEndings(replacementText: " ")} (payload: {Elide(json: json)})";
    }

    /// <summary>Parses a required row from an inline-JSON argument through a source-generated contract.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="json">The payload text.</param>
    /// <param name="info">The source-generated contract.</param>
    /// <param name="value">The parsed row on success.</param>
    /// <param name="error">The one-line rejection reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the payload parsed to a non-null row.</returns>
    public static bool TryParse<T>(string json, JsonTypeInfo<T> info, out T value, out string error) {
        value = default!;

        if (string.IsNullOrWhiteSpace(value: json)) {
            error = EmptyPayload;

            return false;
        }

        try {
            if (JsonSerializer.Deserialize(json: json, jsonTypeInfo: info) is not { } parsed) {
                error = NullPayload;

                return false;
            }

            value = parsed;
            error = string.Empty;

            return true;
        } catch (Exception exception) when (IsParseFailure(exception: exception)) {
            error = Describe(exception: exception, json: json);

            return false;
        }
    }

    /// <summary>Parses a required row from an inline-JSON argument through reflection-backed options.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="json">The payload text.</param>
    /// <param name="options">The serializer options.</param>
    /// <param name="value">The parsed row on success.</param>
    /// <param name="error">The one-line rejection reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the payload parsed to a non-null row.</returns>
    public static bool TryParse<T>(string json, JsonSerializerOptions options, out T value, out string error) where T : class {
        value = null!;

        if (string.IsNullOrWhiteSpace(value: json)) {
            error = EmptyPayload;

            return false;
        }

        try {
            if (JsonSerializer.Deserialize<T>(json: json, options: options) is not { } parsed) {
                error = NullPayload;

                return false;
            }

            value = parsed;
            error = string.Empty;

            return true;
        } catch (Exception exception) when (IsParseFailure(exception: exception)) {
            error = Describe(exception: exception, json: json);

            return false;
        }
    }

    /// <summary>Parses a payload whose literal <c>null</c> is a meaningful value (an inherit/clear), so only a
    /// malformed payload is a rejection.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="json">The payload text.</param>
    /// <param name="options">The serializer options.</param>
    /// <param name="value">The parsed row, or <see langword="null"/> for a null payload.</param>
    /// <param name="error">The one-line rejection reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the payload parsed.</returns>
    public static bool TryParseOptional<T>(string json, JsonSerializerOptions options, out T? value, out string error) where T : class {
        value = null;

        try {
            value = JsonSerializer.Deserialize<T>(json: json, options: options);
            error = string.Empty;

            return true;
        } catch (Exception exception) when (IsParseFailure(exception: exception)) {
            error = Describe(exception: exception, json: json);

            return false;
        }
    }

    // Payloads ride a console line; a long one is trimmed so the rejection stays one readable line.
    private static string Elide(string json) {
        const int Limit = 120;

        var text = (json ?? string.Empty).ReplaceLineEndings(replacementText: " ").Trim();

        return ((text.Length <= Limit) ? text : string.Concat(str0: text.AsSpan(start: 0, length: Limit), str1: "…"));
    }
}
