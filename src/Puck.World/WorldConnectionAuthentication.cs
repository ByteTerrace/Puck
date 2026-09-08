using System.Text.Json;
using Puck.Networking;
using Puck.Storage;
using Puck.World.Azure;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The desktop distribution's installed connection authentication catalog.</summary>
internal static class WorldConnectionAuthentication {
    private static readonly WorldExtensionRegistry<WorldAuthenticationProvider> Providers = new(
        [AzureSiloExtensions.Authentication], provider => provider.Type);

    internal static (IAuthenticator Authenticator, string Subject) Load(string path) {
        using var document = JsonDocument.Parse(ConfinedFile.ReadAllBytes(path, 16384));
        var root = document.RootElement;

        if ((root.ValueKind != JsonValueKind.Object) || (root.EnumerateObject().Count() != 2) ||
            !root.TryGetProperty(propertyName: "type", value: out var type) || !root.TryGetProperty(propertyName: "settings", value: out var settings) ||
            (type.ValueKind != JsonValueKind.String) || (settings.ValueKind != JsonValueKind.Object) ||
            !Providers.TryGet(type.GetString()!, out var provider)) {
            throw new ArgumentException(message: "Authentication configuration requires an installed type and its settings object.");
        }
        return provider.Client(settings);
    }
}
