using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Machines;

namespace Puck.World.Machines;

/// <summary>The immutable machine factories and content providers selected by one host's composition root.
/// Runtime resolution never consults another host or a process-wide registration table.</summary>
public sealed class WorldMachineCatalog : IMachineValidationCatalog {
    /// <summary>Gets the registered engines, keyed by their ordinal identifiers.</summary>
    public FrozenDictionary<string, IMachineEngine> Engines { get; }
    /// <summary>Gets the content providers, keyed by the engine they prepare content for.</summary>
    public FrozenDictionary<string, IMachineContentProvider> ContentProviders { get; }

    /// <summary>Copies a composition's registrations and refuses duplicate or unmatched identifiers.</summary>
    /// <param name="engines">The machine factories available to this host.</param>
    /// <param name="contentProviders">Optional content providers for those factories.</param>
    /// <exception cref="ArgumentNullException">The engine collection is null.</exception>
    /// <exception cref="ArgumentException">An identifier is blank, duplicated, or has no corresponding engine.</exception>
    public WorldMachineCatalog(IEnumerable<IMachineEngine> engines, IEnumerable<IMachineContentProvider>? contentProviders = null) {
        ArgumentNullException.ThrowIfNull(engines);
        var factories = new Dictionary<string, IMachineEngine>(StringComparer.Ordinal);
        var providers = new Dictionary<string, IMachineContentProvider>(StringComparer.Ordinal);
        foreach (var engine in engines) {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentException.ThrowIfNullOrWhiteSpace(engine.Id);
            if (engine.Descriptor.Id != engine.Id) {
                throw new ArgumentException($"Machine engine '{engine.Id}' publishes descriptor '{engine.Descriptor.Id}'.", nameof(engines));
            }
            if (!factories.TryAdd(engine.Id, engine)) {
                throw new ArgumentException($"Duplicate machine engine '{engine.Id}'.", nameof(engines));
            }
        }
        foreach (var provider in contentProviders ?? []) {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.EngineId);
            if (!factories.ContainsKey(provider.EngineId)) {
                throw new ArgumentException($"Content provider names unregistered machine engine '{provider.EngineId}'.", nameof(contentProviders));
            }
            if (!providers.TryAdd(provider.EngineId, provider)) {
                throw new ArgumentException($"Duplicate content provider for machine engine '{provider.EngineId}'.", nameof(contentProviders));
            }
        }
        Engines = factories.ToFrozenDictionary(StringComparer.Ordinal);
        ContentProviders = providers.ToFrozenDictionary(StringComparer.Ordinal);
        CompositionFingerprint = MachineConfigurationFields.CatalogFingerprint(factories.Select(static pair => (EngineId: pair.Key, Descriptor: (MachineEngineDescriptor?)pair.Value.Descriptor)));
    }

    /// <summary>Gets the stable descriptor fingerprint used to partition composed document images.</summary>
    public string CompositionFingerprint { get; }

    /// <summary>Returns whether this host can construct the named engine.</summary>
    /// <param name="engineId">The ordinal engine identifier.</param>
    public bool IsRegistered(string engineId) => Engines.ContainsKey(engineId);

    /// <inheritdoc/>
    public bool TryDescriptor(string engineId, [NotNullWhen(true)] out MachineEngineDescriptor? descriptor) {
        descriptor = Engines.TryGetValue(engineId, out var engine) ? engine.Descriptor : null;
        return descriptor is not null;
    }

    /// <inheritdoc/>
    public bool RequiresPreparation(string contentPath) => !string.IsNullOrEmpty(contentPath) &&
        ContentProviders.Values.Any(provider => provider.Recognizes(contentPath));

    /// <inheritdoc/>
    public bool CanPrepare(string engineId, string contentPath) =>
        ContentProviders.TryGetValue(engineId, out var provider) && provider.Recognizes(contentPath);

    /// <summary>Finds the content provider explicitly registered for this engine.</summary>
    /// <param name="engineId">The ordinal engine identifier.</param>
    /// <param name="provider">The provider, or null if none was registered.</param>
    /// <returns>Whether the provider exists in this catalog.</returns>
    public bool TryContentProvider(string engineId, [NotNullWhen(true)] out IMachineContentProvider? provider) =>
        ContentProviders.TryGetValue(engineId, out provider);
}
