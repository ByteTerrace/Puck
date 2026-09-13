using System.Text.Json;
using Puck.Networking;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The desktop distribution's dynamic connection authentication catalog.</summary>
internal static class WorldConnectionAuthentication {
    private static readonly Dictionary<string, WorldAuthenticationProvider> Providers = new(comparer: StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    internal static (IAuthenticator Authenticator, string Subject) Load(string path) {
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

        var typeKey = type.GetString()!;
        WorldAuthenticationProvider? provider;

        lock (Gate) {
            Providers.TryGetValue(
                key: typeKey,
                value: out provider
            );
        }

        if (provider is null) {
            throw new ArgumentException(message: $"Uninstalled authentication extension '{typeKey}'.");
        }

        return provider.Client(settings);
    }
    internal static void Register(WorldAuthenticationProvider provider) {
        ArgumentNullException.ThrowIfNull(argument: provider);
        lock (Gate) {
            Providers[provider.Type] = provider;
        }
    }
}
