using System.Text.Json;
using Microsoft.Extensions.AI;
using Puck.Abstractions;

namespace Puck.World.Agents.Harness;

/// <summary>A model client an extension contributes, keyed by provider name. A world agent participant selects one by
/// the name its settings give, or the one installed when they give none, and hands it the provider's own settings.
/// Every provider authenticates with identity; none accepts a key.</summary>
/// <param name="Create">Validates the provider settings and creates a model client without calling the service. The
/// caller owns and disposes the client. Refuses invalid settings with an <see cref="ArgumentException"/> naming the
/// setting.</param>
public sealed record ChatClientProvider(Func<JsonElement, IChatClient> Create) {
    /// <summary>Selects the provider a participant's settings name, or the one installed when they name none.</summary>
    /// <param name="extensions">The host's composed extensions.</param>
    /// <param name="name">The configured provider name, or <see langword="null"/> when none is configured.</param>
    /// <returns>The selected provider.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="extensions"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not installed, or no name is configured and none
    /// or several providers are installed; the refusal names the installed providers.</exception>
    public static ChatClientProvider Select(PuckExtensionSet extensions, string? name) {
        ArgumentNullException.ThrowIfNull(argument: extensions);
        return extensions.Select<ChatClientProvider>(
            key: name,
            purpose: "Agent chat client provider"
        );
    }
}
/// <summary>Registers <see cref="ChatClientProvider"/> contributions.</summary>
public static class ChatClientProviderRegistration {
    /// <summary>Adds a chat client provider under its provider name.</summary>
    /// <param name="registry">The registering extension's registry.</param>
    /// <param name="name">The provider name a participant's settings select.</param>
    /// <param name="provider">The provider.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> or <paramref name="provider"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="PuckExtensionException"><paramref name="name"/> is blank or another registration holds it.</exception>
    public static void AddChatClient(this IPuckExtensionRegistry registry, string name, ChatClientProvider provider) {
        ArgumentNullException.ThrowIfNull(argument: registry);
        ArgumentNullException.ThrowIfNull(argument: provider);
        registry.Add(
            contribution: provider,
            key: name
        );
    }
}
