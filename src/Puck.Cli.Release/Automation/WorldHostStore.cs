using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>The owner's store as a hosting machine reaches it: the provider a silo document names (<c>type</c>), opened
/// through the installed world extensions exactly as the silo opens its own store, so the machine's verbs name no
/// provider and the provider's identity and storage access have one implementation, the provider's extension.</summary>
/// <param name="Blobs">The routed blob store.</param>
/// <param name="Target">The owner's store target.</param>
/// <param name="Owner">The world owner.</param>
public sealed record WorldHostStore(IObjectBlobStore Blobs, ObjectStorageTarget Target, Guid Owner) {
    /// <summary>Opens a store a silo document names.</summary>
    /// <param name="store">The store selection: <c>type</c> and <c>settings</c>.</param>
    /// <param name="owner">The world owner.</param>
    /// <returns>The opened store.</returns>
    /// <exception cref="InvalidDataException">The selection names no type.</exception>
    /// <exception cref="InvalidOperationException">No installed extension provides the type.</exception>
    public static WorldHostStore Open(JsonObject store, Guid owner) {
        ArgumentNullException.ThrowIfNull(argument: store);
        var type = ((string?)store["type"] ?? throw new InvalidDataException(message: "the host's store names no type"));
        var provider = CliExtensions.Installed.Select<WorldStorageProvider>(key: type, purpose: "host storage");
        var services = new ServiceCollection();

        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
        using var settings = JsonDocument.Parse(json: (store["settings"]?.ToJsonString() ?? "{}"));
        var blobs = services.BuildServiceProvider().GetRequiredService<IObjectBlobStore>();

        return new(Blobs: blobs, Owner: owner, Target: provider.Create(settings.RootElement.Clone()));
    }
    /// <summary>Gets the owner's release group store.</summary>
    public WorldReleaseGroupStore Groups => new(owner: Owner, store: Blobs, target: Target);
    /// <summary>Gets the owner's release content store.</summary>
    public WorldReleaseArchive Content => new(owner: Owner, store: Blobs, target: Target);
}
