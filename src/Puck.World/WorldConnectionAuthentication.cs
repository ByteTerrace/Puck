using System.Text.Json;
using Puck.Abstractions;
using Puck.Networking;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Reads a deployment's host configuration file, an installed provider's <c>type</c> and its <c>settings</c>,
/// and selects that provider from the composed extensions. Connection authentication and storage both arrive this way,
/// so the host names no provider and reads no provider setting itself.</summary>
internal static class WorldConnectionAuthentication {
    internal static (IAuthenticator Authenticator, string Subject) Load(PuckExtensionSet extensions, string path, TimeProvider clock) => Select(
        path: path,
        purpose: "Authentication",
        select: (type, settings) => extensions.Select<WorldAuthenticationProvider>(
            key: type,
            purpose: "Authentication"
        ).Client(
            settings,
            clock
        )
    );
    internal static ObjectStorageTarget LoadStorage(PuckExtensionSet extensions, string path) => Select(
        path: path,
        purpose: "Storage",
        select: (type, settings) => extensions.Select<WorldStorageProvider>(
            key: type,
            purpose: "Storage"
        ).Create(settings)
    );

    private static TResult Select<TResult>(string path, string purpose, Func<string, JsonElement, TResult> select) {
        using var document = JsonDocument.Parse(ConfinedFile.ReadAllBytes(
            maximumBytes: 16384,
            path: path
        ));
        var root = document.RootElement;

        if (
            (root.ValueKind != JsonValueKind.Object) ||
            (root.EnumerateObject().Count() != 2) ||
            !root.TryGetProperty(
            propertyName: "type",
            value: out var type
        ) ||
            !root.TryGetProperty(
            propertyName: "settings",
            value: out var settings
        ) ||
            (type.ValueKind != JsonValueKind.String) ||
            (settings.ValueKind != JsonValueKind.Object)
        ) {
            throw new ArgumentException(message: $"{purpose} configuration requires an installed type and its settings object.");
        }
        return select(
            type.GetString()!,
            settings
        );
    }
}
