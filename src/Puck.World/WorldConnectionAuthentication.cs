using System.Text.Json;
using Puck.Abstractions;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Reads a deployment's host configuration file, an installed provider's <c>type</c> and its <c>settings</c>,
/// and selects that provider from the composed extensions. A client's sign-in and a host's storage both arrive this
/// way, so the host names no provider and reads no provider setting itself.</summary>
internal static class WorldConnectionAuthentication {
    /// <summary>Reads a client's sign-in configuration: the provider's <c>type</c> and <c>settings</c>, and
    /// <c>remoteKeyHash</c>, the lowercase SHA-256 fingerprint of the authority's transport key that the sign-in
    /// credential and every later proof are presented to.</summary>
    internal static (IWorldSignInCredential Credential, string RemoteKeyHash) Load(PuckExtensionSet extensions, string path, TimeProvider clock) {
        string? remoteKeyHash = null;
        var credential = Select(
            extra: (name, value) => {
                if (
                    (name != "remoteKeyHash") ||
                    (value.ValueKind != JsonValueKind.String) ||
                    (value.GetString() is not { } hash) ||
                    !Puck.Abstractions.ContentDigest.IsLowerHexDigits(length: Puck.Abstractions.ContentDigest.HexLength, text: hash)
                ) {
                    return false;
                }
                remoteKeyHash = hash;
                return true;
            },
            path: path,
            purpose: "Authentication",
            select: (type, settings) => extensions.Select<WorldSignInProvider>(
                key: type,
                purpose: "Authentication"
            ).Client(
                settings,
                clock
            )
        );

        return (credential, (remoteKeyHash ?? throw new ArgumentException(message: "Authentication configuration requires remoteKeyHash, the authority's lowercase SHA-256 transport key fingerprint.")));
    }
    internal static ObjectStorageTarget LoadStorage(PuckExtensionSet extensions, string path) => Select(
        extra: static (_, _) => false,
        path: path,
        purpose: "Storage",
        select: (type, settings) => extensions.Select<WorldStorageProvider>(
            key: type,
            purpose: "Storage"
        ).Create(settings)
    );

    private static TResult Select<TResult>(string path, string purpose, Func<string, JsonElement, TResult> select, Func<string, JsonElement, bool> extra) {
        using var document = JsonDocument.Parse(ConfinedFile.ReadAllBytes(
            maximumBytes: 16384,
            path: path
        ));
        var root = document.RootElement;
        JsonElement? type = null;
        JsonElement? settings = null;

        if (root.ValueKind == JsonValueKind.Object) {
            foreach (var property in root.EnumerateObject()) {
                switch (property.Name) {
                    case "type" when (type is null) && (property.Value.ValueKind == JsonValueKind.String): type = property.Value; break;
                    case "settings" when (settings is null) && (property.Value.ValueKind == JsonValueKind.Object): settings = property.Value; break;
                    default:
                        if (!extra(property.Name, property.Value)) {
                            throw new ArgumentException(message: $"{purpose} configuration member '{property.Name}' is unknown or malformed.");
                        }
                        break;
                }
            }
        }
        if ((type is not { } named) || (settings is not { } values)) {
            throw new ArgumentException(message: $"{purpose} configuration requires an installed type and its settings object.");
        }
        return select(
            named.GetString()!,
            values
        );
    }
}
