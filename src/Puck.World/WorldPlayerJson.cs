using System.Text.Json;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The single JSON seam for player-document boundary work OUTSIDE the persistence store — the <c>SetPlayerSection</c>
/// payload parse (server), the <c>GetPlayerDocument</c> read-back (server), and the rebind verbs' payload build
/// (client). It uses the SAME <see cref="JsonSerializerDefaults.Web"/> shape as <c>JsonObjectBlobStore</c>, so a payload
/// round-trips through the store byte-compatibly (one grammar, not two). The store itself owns persistence; this seam
/// owns the protocol-carried section payloads.
/// </summary>
internal static class WorldPlayerJson {
    /// <summary>The shared Web-defaults options (camelCase, case-insensitive), matching the persistence store.</summary>
    public static readonly JsonSerializerOptions Options = new(defaults: JsonSerializerDefaults.Web);

    /// <summary>Serializes the whole player document to JSON text (the <c>GetPlayerDocument</c> read-back).</summary>
    /// <param name="document">The document to serialize.</param>
    /// <returns>The JSON text.</returns>
    public static string Serialize(WorldPlayerDocument document) {
        return JsonSerializer.Serialize(value: document, options: Options);
    }

    /// <summary>Serializes a binding document to compact JSON text — the <c>SetPlayerSection(bindings)</c> payload the
    /// rebind verbs carry.</summary>
    /// <param name="document">The binding document to serialize.</param>
    /// <returns>The JSON text.</returns>
    public static string SerializeBindings(BindingProfileDocument document) {
        return JsonSerializer.Serialize(value: document, options: Options);
    }

    /// <summary>Serializes a motion section to compact JSON text — the <c>SetPlayerSection(motion)</c> payload
    /// <c>profile.set</c> composes from the seat's current motion with one field changed.</summary>
    /// <param name="motion">The motion section to serialize.</param>
    /// <returns>The JSON text.</returns>
    public static string SerializeMotion(WorldPlayerMotion motion) {
        return JsonSerializer.Serialize(value: motion, options: Options);
    }

    /// <summary>Parses a binding-document payload (or the literal <c>null</c>), returning whether it parsed.</summary>
    /// <param name="payload">The JSON payload.</param>
    /// <param name="document">The parsed document, or <see langword="null"/> for a null/absent payload.</param>
    /// <param name="error">The parse error, or empty on success.</param>
    /// <returns><see langword="true"/> when the payload parsed (a null payload is a valid "inherit the default").</returns>
    public static bool TryParseBindings(string payload, out BindingProfileDocument? document, out string error) {
        document = null;
        error = string.Empty;

        try {
            document = JsonSerializer.Deserialize<BindingProfileDocument?>(json: payload, options: Options);

            return true;
        } catch (JsonException exception) {
            error = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }
    }

    /// <summary>Parses a <c>WorldPlayerIdentity</c> payload (<c>{"name":…,"color":"#RRGGBB"}</c>) for a
    /// <c>SetPlayerSection(identity)</c> apply. A parse failure or a null/absent payload is rejected — an identity edit
    /// must carry a concrete identity (the thick validator then gates the values).</summary>
    /// <param name="payload">The JSON payload.</param>
    /// <param name="identity">The parsed identity on success.</param>
    /// <param name="error">The parse error, or empty on success.</param>
    /// <returns><see langword="true"/> when the payload parsed to a non-null identity.</returns>
    public static bool TryParseIdentity(string payload, out WorldPlayerIdentity identity, out string error) {
        return TryParseRecord(payload: payload, value: out identity!, error: out error);
    }

    /// <summary>Parses a <c>WorldPlayerMotion</c> payload (<c>{"moveSpeed":…,"turnSpeed":…,"invertLookX":…}</c>) for a
    /// <c>SetPlayerSection(motion)</c> apply. A parse failure or a null payload is rejected (the thick validator then
    /// gates finite-positive speeds).</summary>
    /// <param name="payload">The JSON payload.</param>
    /// <param name="motion">The parsed motion on success.</param>
    /// <param name="error">The parse error, or empty on success.</param>
    /// <returns><see langword="true"/> when the payload parsed to a non-null motion.</returns>
    public static bool TryParseMotion(string payload, out WorldPlayerMotion motion, out string error) {
        return TryParseRecord(payload: payload, value: out motion!, error: out error);
    }

    /// <summary>Parses an open-preferences payload — a JSON OBJECT folded into the profile's extension bag for a
    /// <c>SetPlayerSection(preferences)</c> apply. The literal <c>null</c> clears the bag; a non-object (array/scalar)
    /// is rejected, since the preferences bag is a keyed store.</summary>
    /// <param name="payload">The JSON payload.</param>
    /// <param name="preferences">The parsed bag, or <see langword="null"/> for a null payload (a cleared bag).</param>
    /// <param name="error">The parse error, or empty on success.</param>
    /// <returns><see langword="true"/> when the payload parsed to a JSON object or null.</returns>
    public static bool TryParsePreferences(string payload, out IDictionary<string, JsonElement>? preferences, out string error) {
        preferences = null;
        error = string.Empty;

        try {
            preferences = JsonSerializer.Deserialize<Dictionary<string, JsonElement>?>(json: payload, options: Options);

            return true;
        } catch (JsonException exception) {
            error = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }
    }

    // Deserialize a required section record (identity/motion) with the shared Web options; a null result (the literal
    // "null" payload) or a JSON error is a failure, since these sections must carry a concrete value.
    private static bool TryParseRecord<T>(string payload, out T value, out string error) where T : class {
        value = null!;
        error = string.Empty;

        try {
            if (JsonSerializer.Deserialize<T>(json: payload, options: Options) is not { } parsed) {
                error = "the payload was null";

                return false;
            }

            value = parsed;

            return true;
        } catch (JsonException exception) {
            error = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }
    }
}
