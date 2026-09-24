namespace Puck.Abstractions.Machines;

/// <summary>Registers machine engines and their content providers as <see cref="IPuckExtension"/> contributions: an
/// <see cref="IMachineEngine"/> keyed by its identifier and an <see cref="IMachineContentProvider"/> keyed by the engine it
/// prepares content for.</summary>
public static class MachineExtensionRegistration {
    /// <summary>Adds an engine and, optionally, the provider that prepares its authored content. Neither half is added
    /// when the pair is refused.</summary>
    /// <param name="registry">The registering extension's registry.</param>
    /// <param name="engine">The machine factory.</param>
    /// <param name="contentProvider">The engine's content provider, or <see langword="null"/> for native images only.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> or <paramref name="engine"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="PuckExtensionException">The engine identifier is blank, the provider names another engine, or
    /// another registration already holds either key.</exception>
    public static void AddMachineEngine(this IPuckExtensionRegistry registry, IMachineEngine engine, IMachineContentProvider? contentProvider = null) {
        ArgumentNullException.ThrowIfNull(argument: registry);
        ArgumentNullException.ThrowIfNull(argument: engine);
        if (
            (contentProvider is not null) &&
            !string.Equals(
                a: engine.Id,
                b: contentProvider.EngineId,
                comparisonType: StringComparison.Ordinal
            )
        ) {
            throw new PuckExtensionException(message: $"Content provider '{contentProvider.EngineId}' cannot register with machine engine '{engine.Id}'.");
        }
        registry.Add(
            contribution: engine,
            key: engine.Id
        );
        if (contentProvider is not null) {
            registry.Add(
                contribution: contentProvider,
                key: contentProvider.EngineId
            );
        }
    }
}
