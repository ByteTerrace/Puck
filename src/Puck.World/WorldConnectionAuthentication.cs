using System.Text.Json;
using Puck.Abstractions;
using Puck.Networking;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Reads a deployment's connection authentication configuration and selects its installed provider.</summary>
internal static class WorldConnectionAuthentication {
    internal static (IAuthenticator Authenticator, string Subject) Load(PuckExtensionSet extensions, string path, TimeProvider clock) {
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
            throw new ArgumentException(message: "Authentication configuration requires an installed type and its settings object.");
        }
        return extensions.Select<WorldAuthenticationProvider>(
            key: type.GetString()!,
            purpose: "Authentication"
        ).Client(
            settings,
            clock
        );
    }
}
