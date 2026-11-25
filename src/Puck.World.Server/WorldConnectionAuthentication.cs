using System.Text.Json;
using Puck.Abstractions;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>A client's sign-in configuration, as a deployment publishes it (<c>world-authentication.json</c>): the
/// installed provider's <c>type</c> and its <c>settings</c>, and <c>remoteKeyHash</c>, the lowercase SHA-256
/// fingerprint of the authority's transport key that the sign-in credential and every later proof are presented
/// to.</summary>
/// <param name="Type">The sign-in provider's contribution key.</param>
/// <param name="Settings">The provider-owned settings, detached from the file's document.</param>
/// <param name="RemoteKeyHash">The authority's transport key fingerprint.</param>
public sealed record WorldSignInConfiguration(string Type, JsonElement Settings, string RemoteKeyHash) {
    /// <summary>Selects the configured provider from a host's composed extensions.</summary>
    /// <param name="extensions">The host's extensions.</param>
    /// <returns>The provider the configuration names.</returns>
    /// <exception cref="ArgumentException">No installed extension provides <see cref="Type"/>; the refusal names the
    /// installed sign-ins.</exception>
    public WorldSignInProvider Provider(PuckExtensionSet extensions) => extensions.Select<WorldSignInProvider>(
        key: Type,
        purpose: "Authentication"
    );
}

/// <summary>Reads a deployment's host configuration file, an installed provider's <c>type</c> and its <c>settings</c>,
/// and selects that provider from the composed extensions. A client's sign-in and a host's storage both arrive this
/// way, so no host names a provider or reads a provider setting itself, and every reader of a sign-in file (the World
/// that signs in, the tool that ships the file in a player build) reads it here.</summary>
public static class WorldConnectionAuthentication {
    /// <summary>The most bytes a configuration file may hold.</summary>
    public const int MaximumBytes = 16384;

    /// <summary>Reads a client's sign-in configuration file without selecting its provider.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The configuration.</returns>
    /// <exception cref="ArgumentException">The file is not a sign-in configuration: a member is unknown or malformed,
    /// or <c>type</c>, <c>settings</c> or <c>remoteKeyHash</c> is missing.</exception>
    /// <exception cref="IOException">The file cannot be read or exceeds <see cref="MaximumBytes"/>.</exception>
    /// <exception cref="JsonException">The file is not JSON.</exception>
    public static WorldSignInConfiguration ReadSignIn(string path) {
        string? remoteKeyHash = null;
        var (type, settings) = Read(
            extra: (name, value) => {
                if (
                    (name != "remoteKeyHash") ||
                    (value.ValueKind != JsonValueKind.String) ||
                    (value.GetString() is not { } hash) ||
                    !ContentDigest.IsLowerHexDigits(length: ContentDigest.HexLength, text: hash)
                ) {
                    return false;
                }
                remoteKeyHash = hash;
                return true;
            },
            path: path,
            purpose: "Authentication"
        );

        return new(
            RemoteKeyHash: (remoteKeyHash ?? throw new ArgumentException(message: "Authentication configuration requires remoteKeyHash, the authority's lowercase SHA-256 transport key fingerprint.")),
            Settings: settings,
            Type: type
        );
    }
    /// <summary>Reads a client's sign-in configuration and creates its credential source from the installed provider
    /// it names.</summary>
    /// <param name="extensions">The host's extensions.</param>
    /// <param name="path">The file.</param>
    /// <param name="clock">The host clock the provider's deadlines run on.</param>
    /// <returns>The credential source and the authority's transport key fingerprint.</returns>
    /// <exception cref="ArgumentException">The file is malformed, its provider is not installed, or the provider
    /// refuses its settings.</exception>
    public static (IWorldSignInCredential Credential, string RemoteKeyHash) Load(PuckExtensionSet extensions, string path, TimeProvider clock) {
        var configuration = ReadSignIn(path: path);

        return (configuration.Provider(extensions: extensions).Client(configuration.Settings, clock), configuration.RemoteKeyHash);
    }
    /// <summary>Reads a host's storage configuration and creates its target from the installed provider it names.</summary>
    /// <param name="extensions">The host's extensions.</param>
    /// <param name="path">The file.</param>
    /// <returns>The storage target.</returns>
    public static ObjectStorageTarget LoadStorage(PuckExtensionSet extensions, string path) {
        var (type, settings) = Read(
            extra: static (_, _) => false,
            path: path,
            purpose: "Storage"
        );

        return extensions.Select<WorldStorageProvider>(
            key: type,
            purpose: "Storage"
        ).Create(settings);
    }

    // The one reader of a provider configuration's shape: exactly one type string and one settings object, every other
    // member accepted only by the caller's extra.
    private static (string Type, JsonElement Settings) Read(string path, string purpose, Func<string, JsonElement, bool> extra) {
        using var document = JsonDocument.Parse(ConfinedFile.ReadAllBytes(
            maximumBytes: MaximumBytes,
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
        // The settings outlive the document they were read from.
        return (named.GetString()!, values.Clone());
    }
}
