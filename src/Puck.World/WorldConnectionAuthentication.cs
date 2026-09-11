using System.Text.Json;
using Puck.Networking;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The desktop distribution's dynamic connection authentication catalog.</summary>
internal static class WorldConnectionAuthentication {
    private static readonly Dictionary<string, WorldAuthenticationProvider> s_providers = new(StringComparer.Ordinal);
    private static readonly Lock s_gate = new();

    internal static void Register(WorldAuthenticationProvider provider) {
        ArgumentNullException.ThrowIfNull(argument: provider);
        lock (s_gate) {
            s_providers[provider.Type] = provider;
        }
    }

    internal static (IAuthenticator Authenticator, string Subject) Load(string path) {
        using var document = JsonDocument.Parse(ConfinedFile.ReadAllBytes(path, 16384));
        var root = document.RootElement;

        if ((root.ValueKind != JsonValueKind.Object) || (root.EnumerateObject().Count() != 2) ||
            !root.TryGetProperty(propertyName: "type", value: out var type) || !root.TryGetProperty(propertyName: "settings", value: out var settings) ||
            (type.ValueKind != JsonValueKind.String) || (settings.ValueKind != JsonValueKind.Object)) {
            throw new ArgumentException(message: "Authentication configuration requires an installed type and its settings object.");
        }

        var typeKey = type.GetString()!;
        WorldAuthenticationProvider? provider;

        lock (s_gate) {
            s_providers.TryGetValue(key: typeKey, value: out provider);
        }

        if (provider is null) {
            throw new ArgumentException(message: $"Uninstalled authentication extension '{typeKey}'.");
        }

        return provider.Client(settings);
    }
}
