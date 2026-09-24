using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions;
using Puck.Abstractions.Machines;

namespace Puck.World.Machines;

/// <summary>The immutable machine factories and content providers selected by one host's composition root.
/// Runtime resolution never consults another host or a process-wide registration table.</summary>
public sealed class WorldMachineCatalog : IMachineValidationCatalog {
    /// <summary>Gets the stable descriptor fingerprint used to partition composed document images.</summary>
    public string CompositionFingerprint { get; }
    /// <summary>Gets the content providers, keyed by the engine they prepare content for.</summary>
    public FrozenDictionary<string, IMachineContentProvider> ContentProviders { get; }
    /// <summary>Gets the registered engines, keyed by their ordinal identifiers.</summary>
    public FrozenDictionary<string, IMachineEngine> Engines { get; }

    /// <summary>Copies a composition's registrations and refuses duplicate or unmatched identifiers.</summary>
    /// <param name="engines">The machine factories available to this host.</param>
    /// <param name="contentProviders">Optional content providers for those factories.</param>
    /// <exception cref="ArgumentNullException">The engine collection is null.</exception>
    /// <exception cref="ArgumentException">An identifier is blank, duplicated, or has no corresponding engine.</exception>
    public WorldMachineCatalog(IEnumerable<IMachineEngine> engines, IEnumerable<IMachineContentProvider>? contentProviders = null) {
        ArgumentNullException.ThrowIfNull(engines);
        var factories = new Dictionary<string, IMachineEngine>(comparer: StringComparer.Ordinal);
        var providers = new Dictionary<string, IMachineContentProvider>(comparer: StringComparer.Ordinal);

        foreach (var engine in engines) {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentException.ThrowIfNullOrWhiteSpace(engine.Id);
            if (engine.Descriptor.Id != engine.Id) {
                throw new ArgumentException(
                    message: $"Machine engine '{engine.Id}' publishes descriptor '{engine.Descriptor.Id}'.",
                    paramName: nameof(engines)
                );
            }
            if (!factories.TryAdd(
                key: engine.Id,
                value: engine
            )) {
                throw new ArgumentException(
                    message: $"Duplicate machine engine '{engine.Id}'.",
                    paramName: nameof(engines)
                );
            }
        }
        foreach (var provider in (contentProviders ?? [])) {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.EngineId);
            if (!factories.ContainsKey(key: provider.EngineId)) {
                throw new ArgumentException(
                    message: $"Content provider names unregistered machine engine '{provider.EngineId}'.",
                    paramName: nameof(contentProviders)
                );
            }
            if (!providers.TryAdd(
                key: provider.EngineId,
                value: provider
            )) {
                throw new ArgumentException(
                    message: $"Duplicate content provider for machine engine '{provider.EngineId}'.",
                    paramName: nameof(contentProviders)
                );
            }
        }
        Engines = factories.ToFrozenDictionary(comparer: StringComparer.Ordinal);
        ContentProviders = providers.ToFrozenDictionary(comparer: StringComparer.Ordinal);
        CompositionFingerprint = MachineConfigurationFields.CatalogFingerprint(descriptors: factories.Select(selector: static pair => (EngineId: pair.Key, Descriptor: ((MachineEngineDescriptor?)pair.Value.Descriptor))));
    }

    /// <summary>Builds the catalog from a composed extension set's <see cref="IMachineEngine"/> and
    /// <see cref="IMachineContentProvider"/> contributions.</summary>
    /// <param name="extensions">The host's composed extensions.</param>
    /// <returns>The host's immutable machine catalog.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="extensions"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An engine publishes a mismatched descriptor, or a content provider names no
    /// contributed engine.</exception>
    public static WorldMachineCatalog From(PuckExtensionSet extensions) {
        ArgumentNullException.ThrowIfNull(argument: extensions);
        return new WorldMachineCatalog(
            contentProviders: extensions.Contributions<IMachineContentProvider>().Select(selector: static entry => entry.Value),
            engines: extensions.Contributions<IMachineEngine>().Select(selector: static entry => entry.Value)
        );
    }
    /// <inheritdoc/>
    public bool CanPrepare(string engineId, string contentPath) =>
        (ContentProviders.TryGetValue(
            key: engineId,
            value: out var provider
        ) && provider.Recognizes(contentPath: contentPath));
    /// <summary>Returns whether this host can construct the named engine.</summary>
    /// <param name="engineId">The ordinal engine identifier.</param>
    public bool IsRegistered(string engineId) => Engines.ContainsKey(key: engineId);
    /// <inheritdoc/>
    public bool RequiresPreparation(string contentPath) => (!string.IsNullOrEmpty(value: contentPath) &&
        ContentProviders.Values.Any(predicate: provider => provider.Recognizes(contentPath: contentPath)));
    /// <summary>Finds the content provider explicitly registered for this engine.</summary>
    /// <param name="engineId">The ordinal engine identifier.</param>
    /// <param name="provider">The provider, or null if none was registered.</param>
    /// <returns>Whether the provider exists in this catalog.</returns>
    public bool TryContentProvider(string engineId, [NotNullWhen(true)] out IMachineContentProvider? provider) =>
        ContentProviders.TryGetValue(
            key: engineId,
            value: out provider
        );
    /// <inheritdoc/>
    public bool TryDescriptor(string engineId, [NotNullWhen(true)] out MachineEngineDescriptor? descriptor) {
        descriptor = (Engines.TryGetValue(
            key: engineId,
            value: out var engine
        )
            ? engine.Descriptor
            : null
        );
        return (descriptor is not null);
    }
}
